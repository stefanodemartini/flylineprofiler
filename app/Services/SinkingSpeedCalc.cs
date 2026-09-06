namespace DiametroLineaDesktop.Services;

/// <summary>
/// Computes the terminal sinking (or rising) speed of a fly line cylinder in water.
/// Ported from VBA FlyLineSinkSpeed by the project author.
///
/// Physics model: force balance per unit length between buoyancy/gravity and
/// cylinder drag (Cd = 1 + 10/Re^(2/3)), solved by bisection.
///
/// Units used internally: SI (m, kg, m/s, m²/s).
/// Public API accepts mm / cm / g/cm³ for convenience; returns m/s.
/// Positive result → sinking, negative → floating/rising.
/// </summary>
public static class SinkingSpeedCalc
{
    private const double G        = 9.80665;
    private const int    MaxIter  = 100;
    private const double Tol      = 1e-12;
    public  const double RhoFloor = 0.94; // g/cm³ — minimum practical material density (0.94 < ρ_water → will float)

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Compensated profile: for a tapered segment sliced into small cylinders,
    /// finds the NEW diameter of each slice such that:
    ///   1. Slice mass is preserved: ρ_new · d_new² = ρ_orig · d_orig²
    ///   2. The slice sinks at exactly targetSpeedMs
    ///
    /// When the required density would drop below RhoFloor (0.94 g/cm³), the density
    /// is clamped and the diameter recomputed from mass conservation. The affected
    /// slice is flagged in the returned <c>clamped</c> array.
    ///
    /// Returns parallel arrays: slice-centre X (cm from segment start),
    /// compensated diameters (mm), required densities (g/cm³), clamped flags.
    /// </summary>
    public static (double[] sliceXsCm, double[] sliceDiamsMm, double[] sliceDensitiesGcm3, bool[] clamped)
        CompensateProfile(
            bool isSalt, double tempC,
            double startDiamMm, double endDiamMm, double lengthCm,
            double densityGcm3, double targetSpeedMs,
            double sliceLenCm = 1.0)   // 1 cm slices for fine resolution
    {
        if (densityGcm3 <= 0 || lengthCm <= 0 || startDiamMm <= 0 || endDiamMm <= 0)
            return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<bool>());

        (double rhoW, double nu) = WaterProps(isSalt, tempC);
        double rhoOrig = densityGcm3 * 1000.0; // kg/m³

        int    n  = Math.Max(1, (int)Math.Ceiling(lengthCm / sliceLenCm));
        double dl = lengthCm / n; // slice length in cm

        double[] xs    = new double[n];
        double[] diams = new double[n];
        double[] dens  = new double[n];
        bool[]   clmpd = new bool[n];

        for (int i = 0; i < n; i++)
        {
            double t     = (i + 0.5) / n;
            double dOrig = (startDiamMm + t * (endDiamMm - startDiamMm)) / 1000.0; // m
            xs[i]        = (i + 0.5) * dl; // cm from segment start

            // Force balance per unit length with mass conservation (ρ_orig·dOrig² = ρ_new·d_new²):
            //   Net gravity: (π/4)·g·(ρ_orig·dOrig² − ρ_W·d_new²)
            //   Drag:        0.5·Cd(d_new)·|V|·V·d_new·ρ_W
            double Residual(double d)
            {
                if (d <= 0) return double.PositiveInfinity;
                double re = Math.Abs(targetSpeedMs) * d / nu;
                if (re < Tol) re = Tol;
                double cd = 1.0 + 10.0 / Math.Pow(re, 2.0 / 3.0);
                return (Math.PI / 4.0) * G * (rhoOrig * dOrig * dOrig - rhoW * d * d)
                       - 0.5 * cd * Math.Abs(targetSpeedMs) * targetSpeedMs * d * rhoW;
            }

            // Upper bracket: must be large enough that Residual(hi) < 0 for any sinking target.
            // For a sinking line Residual is positive near d=0 and goes negative as d grows;
            // 6× the original diameter gives ample headroom while staying physically plausible.
            double lo  = 1e-6;
            double hi  = Math.Max(0.030, dOrig * 6.0);
            double flo = Residual(lo);
            double fhi = Residual(hi);

            double dNew = dOrig; // fallback: kept unchanged if no root found in bracket
            if (flo * fhi <= 0.0)
            {
                for (int k = 0; k < MaxIter; k++)
                {
                    double mid  = (lo + hi) / 2.0;
                    double fmid = Residual(mid);
                    if (Math.Abs(fmid) < Tol) { dNew = mid; break; }
                    if (flo * fmid <= 0.0) { hi = mid; }
                    else                    { lo = mid; flo = fmid; }
                    dNew = mid;
                }
            }
            diams[i] = dNew * 1000.0; // m → mm
            // Density from mass conservation: ρ_new = ρ_orig × (dOrig/dNew)²
            dens[i] = (rhoOrig * (dOrig * dOrig) / (dNew * dNew)) / 1000.0; // g/cm³
            // Clamp to floor: slice cannot be lighter than RhoFloor (would float)
            if (dens[i] < RhoFloor)
            {
                dens[i]   = RhoFloor;
                dNew      = dOrig * Math.Sqrt(rhoOrig / (RhoFloor * 1000.0)); // recompute d from mass conservation
                diams[i]  = dNew * 1000.0;
                clmpd[i]  = true;
            }
        }

        return (xs, diams, dens, clmpd);
    }

    /// <summary>
    /// Sink speed of the entire line (all segments) treated as a single rigid body
    /// at a given uniform density.  All segments contribute to the combined force balance.
    /// </summary>
    public static double LineSinkSpeed(
        bool isSalt, double tempC,
        IEnumerable<(double StartDiamMm, double EndDiamMm, double LengthCm)> segments,
        double densityGcm3,
        double sliceLengthCm = 5.0)
    {
        if (densityGcm3 <= 0) return double.NaN;
        var slices = BuildSlices(segments, sliceLengthCm);
        return slices.Count == 0 ? double.NaN
            : LineSinkSpeedCore(isSalt, tempC, slices, densityGcm3);
    }

    /// <summary>
    /// Back-calculates the uniform density required to make the whole line sink
    /// at <paramref name="targetSpeedMs"/> (m/s).
    /// Returns (density, achievable=true) on success, or the nearest boundary
    /// density with achievable=false when the target is outside the physical range.
    /// </summary>
    public static (double DensityGcm3, bool Achievable) DensityForTargetSinkSpeed(
        bool isSalt, double tempC,
        IEnumerable<(double StartDiamMm, double EndDiamMm, double LengthCm)> segments,
        double targetSpeedMs,
        double sliceLengthCm = 5.0)
    {
        if (targetSpeedMs <= 0) return (0, false);
        var slices = BuildSlices(segments, sliceLengthCm);
        if (slices.Count == 0) return (0, false);

        const double rhoMin = 1.001;   // just above fresh water — anything less floats
        const double rhoMax = 2.5;

        double sMin = LineSinkSpeedCore(isSalt, tempC, slices, rhoMin);
        double sMax = LineSinkSpeedCore(isSalt, tempC, slices, rhoMax);
        if (double.IsNaN(sMin) || double.IsNaN(sMax)) return (0, false);

        if (targetSpeedMs <= sMin) return (rhoMin, false);
        if (targetSpeedMs >= sMax) return (rhoMax, false);

        // Bisect on density
        double lo = rhoMin, hi = rhoMax;
        double flo = sMin - targetSpeedMs;
        for (int i = 0; i < 80; i++)
        {
            double mid  = (lo + hi) / 2.0;
            double fmid = LineSinkSpeedCore(isSalt, tempC, slices, mid) - targetSpeedMs;
            if (Math.Abs(fmid) < 1e-7) return (mid, true);
            if (flo * fmid <= 0.0) { hi = mid; }
            else                    { lo = mid; flo = fmid; }
        }
        return ((lo + hi) / 2.0, true);
    }

    // Returns the sink speed range achievable for the line between rho 1.001 and 2.5
    public static (double MinSpeedMs, double MaxSpeedMs) LineSinkSpeedRange(
        bool isSalt, double tempC,
        IEnumerable<(double StartDiamMm, double EndDiamMm, double LengthCm)> segments,
        double sliceLengthCm = 5.0)
    {
        var slices = BuildSlices(segments, sliceLengthCm);
        if (slices.Count == 0) return (double.NaN, double.NaN);
        return (LineSinkSpeedCore(isSalt, tempC, slices, 1.001),
                LineSinkSpeedCore(isSalt, tempC, slices, 2.5));
    }

    private static List<(double Dm, double Dl)> BuildSlices(
        IEnumerable<(double StartDiamMm, double EndDiamMm, double LengthCm)> segments,
        double sliceLengthCm)
    {
        var slices = new List<(double Dm, double Dl)>();
        foreach (var (s, e, l) in segments)
        {
            if (l <= 0 || s <= 0 || e <= 0) continue;
            double Lm = l / 100.0;
            int    n  = Math.Max(1, (int)Math.Ceiling(l / sliceLengthCm));
            double dl = Lm / n;
            for (int i = 0; i < n; i++)
            {
                double t  = (i + 0.5) / n;
                double dm = (s + t * (e - s)) / 1000.0;
                slices.Add((dm, dl));
            }
        }
        return slices;
    }

    private static double LineSinkSpeedCore(
        bool isSalt, double tempC,
        List<(double Dm, double Dl)> slices,
        double densityGcm3)
    {
        (double rhoW, double nu) = WaterProps(isSalt, tempC);
        double rhoL = densityGcm3 * 1000.0;

        double totalGrav = 0;
        foreach (var (dm, dl) in slices)
            totalGrav += (Math.PI / 4.0) * dm * dm * dl * G * (rhoL - rhoW);

        double lo, hi;
        if (totalGrav >= 0.0) { lo = 0.0; hi = 3.0; }
        else                   { lo = -3.0; hi = 0.0; }

        double ResidualFull(double v)
        {
            double drag = 0;
            foreach (var (dm, dl) in slices)
            {
                double re = Math.Abs(v) * dm / nu;
                if (re < Tol) re = Tol;
                double cd = 1.0 + 10.0 / Math.Pow(re, 2.0 / 3.0);
                drag += 0.5 * cd * Math.Abs(v) * v * dm * dl * rhoW;
            }
            return totalGrav - drag;
        }

        double flo = ResidualFull(lo), fhi = ResidualFull(hi);
        if (flo * fhi > 0.0) return double.NaN;

        double mid = 0.0;
        for (int i = 0; i < MaxIter; i++)
        {
            mid = (lo + hi) / 2.0;
            double fmid = ResidualFull(mid);
            if (Math.Abs(fmid) < Tol) break;
            if (flo * fmid <= 0.0) { hi = mid; }
            else                    { lo = mid; flo = ResidualFull(lo); }
        }
        return mid;
    }

    /// <summary>
    /// Accurate sink speed for a tapered segment.
    ///
    /// The segment is divided into sub-cylinders of at most <paramref name="sliceLengthCm"/> cm.
    /// All slices move at the same speed V; V is found by solving the combined
    /// force balance (total gravity + buoyancy = total drag) via bisection.
    ///
    /// This is physically correct for a rigid body: the thick and thin parts
    /// do not sink independently — they negotiate a single equilibrium speed.
    /// </summary>
    public static double TaperedSegmentSinkSpeed(
        bool isSalt, double tempC,
        double startDiamMm, double endDiamMm, double lengthCm,
        double densityGcm3,
        double sliceLengthCm = 12.0)
    {
        if (densityGcm3 <= 0 || lengthCm <= 0) return double.NaN;
        if (startDiamMm <= 0 || endDiamMm <= 0) return double.NaN;

        (double rhoW, double nu) = WaterProps(isSalt, tempC);
        double rhoL  = densityGcm3 * 1000.0;   // kg/m³
        double Lm    = lengthCm / 100.0;        // m

        int    n  = Math.Max(1, (int)Math.Ceiling(lengthCm / sliceLengthCm));
        double dl = Lm / n;                     // slice length in m

        // Build array of slice mid-point diameters in m
        double[] dm = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t   = (i + 0.5) / n;
            dm[i] = (startDiamMm + t * (endDiamMm - startDiamMm)) / 1000.0;
        }

        // Pre-compute the total net gravity/buoyancy force (independent of V)
        double totalGrav = 0.0;
        foreach (var d in dm)
            totalGrav += (Math.PI / 4.0) * d * d * dl * G * (rhoL - rhoW);

        // Sign of velocity bracket depends on net gravity direction
        double lo, hi;
        if (totalGrav >= 0.0) { lo = 0.0; hi = 3.0; }
        else                   { lo = -3.0; hi = 0.0; }

        double flo = CombinedResidual(lo, dm, dl, rhoL, rhoW, nu, totalGrav);
        double fhi = CombinedResidual(hi, dm, dl, rhoL, rhoW, nu, totalGrav);

        if (flo * fhi > 0.0) return double.NaN;

        double mid = 0.0;
        for (int i = 0; i < MaxIter; i++)
        {
            mid = (lo + hi) / 2.0;
            double fmid = CombinedResidual(mid, dm, dl, rhoL, rhoW, nu, totalGrav);
            if (Math.Abs(fmid) < Tol) break;
            if (flo * fmid <= 0.0) { hi = mid; }
            else                    { lo = mid; flo = CombinedResidual(lo, dm, dl, rhoL, rhoW, nu, totalGrav); }
        }
        return mid;
    }

    /// <summary>
    /// Rigid-body sink speed for a set of slices that may each carry a different diameter
    /// AND density — e.g. a line with a manually assigned denser/lighter zone. All slices move
    /// at one equilibrium speed V, exactly like <see cref="TaperedSegmentSinkSpeed"/> but without
    /// its single-uniform-density restriction.
    /// </summary>
    public static double RigidBodySinkSpeed(
        bool isSalt, double tempC,
        IReadOnlyList<double> sliceDiamsMm, IReadOnlyList<double> sliceLengthsCm, IReadOnlyList<double> sliceDensitiesGcm3)
    {
        int n = sliceDiamsMm.Count;
        if (n == 0 || n != sliceLengthsCm.Count || n != sliceDensitiesGcm3.Count) return double.NaN;

        (double rhoW, double nu) = WaterProps(isSalt, tempC);

        double[] dm  = new double[n];
        double[] dl  = new double[n];
        double totalGrav = 0.0;
        for (int i = 0; i < n; i++)
        {
            dm[i] = sliceDiamsMm[i] / 1000.0;
            dl[i] = sliceLengthsCm[i] / 100.0;
            double rho = sliceDensitiesGcm3[i] * 1000.0;
            totalGrav += (Math.PI / 4.0) * dm[i] * dm[i] * dl[i] * G * (rho - rhoW);
        }

        double lo, hi;
        if (totalGrav >= 0.0) { lo = 0.0; hi = 3.0; }
        else                   { lo = -3.0; hi = 0.0; }

        double Residual(double v)
        {
            double drag = 0.0;
            for (int i = 0; i < n; i++)
            {
                double re = Math.Abs(v) * dm[i] / nu;
                if (re < Tol) re = Tol;
                double cd = 1.0 + 10.0 / Math.Pow(re, 2.0 / 3.0);
                drag += 0.5 * cd * Math.Abs(v) * v * dm[i] * dl[i] * rhoW;
            }
            return totalGrav - drag;
        }

        double flo = Residual(lo), fhi = Residual(hi);
        if (flo * fhi > 0.0) return double.NaN;

        double mid = 0.0;
        for (int i = 0; i < MaxIter; i++)
        {
            mid = (lo + hi) / 2.0;
            double fmid = Residual(mid);
            if (Math.Abs(fmid) < Tol) break;
            if (flo * fmid <= 0.0) { hi = mid; }
            else                    { lo = mid; flo = Residual(lo); }
        }
        return mid;
    }

    /// <summary>
    /// Sink speed of a single uniform cylinder (used for per-cylinder checks).
    /// </summary>
    public static double CylinderSinkSpeed(
        bool isSalt, double tempC, double diameterMm, double densityGcm3)
    {
        if (densityGcm3 <= 0 || diameterMm <= 0) return double.NaN;
        double rhoL = densityGcm3 * 1000.0;
        double dm   = diameterMm  / 1000.0;
        (double rhoW, double nu) = WaterProps(isSalt, tempC);

        double lo, hi;
        double grav = (Math.PI / 4.0) * dm * dm * G * (rhoL - rhoW);
        if (grav >= 0.0) { lo = 0.0; hi = 3.0; }
        else              { lo = -3.0; hi = 0.0; }

        double flo = Residual(lo, dm, rhoL, rhoW, nu);
        double fhi = Residual(hi, dm, rhoL, rhoW, nu);
        if (flo * fhi > 0.0) return double.NaN;

        double mid = 0.0;
        for (int i = 0; i < MaxIter; i++)
        {
            mid = (lo + hi) / 2.0;
            double fmid = Residual(mid, dm, rhoL, rhoW, nu);
            if (Math.Abs(fmid) < Tol) break;
            if (flo * fmid <= 0.0) { hi = mid; }
            else                    { lo = mid; flo = Residual(lo, dm, rhoL, rhoW, nu); }
        }
        return mid;
    }

    // ── Private physics helpers ──────────────────────────────────────────────

    /// <summary>
    /// Combined force residual for an assembly of slices at speed V.
    /// F_net = totalGrav - Σ drag_i(V)
    /// </summary>
    private static double CombinedResidual(
        double v, double[] dm, double dl,
        double rhoL, double rhoW, double nu,
        double totalGrav)
    {
        double totalDrag = 0.0;
        foreach (var d in dm)
        {
            double re = Math.Abs(v) * d / nu;
            if (re < Tol) re = Tol;
            double cd = 1.0 + 10.0 / Math.Pow(re, 2.0 / 3.0);
            totalDrag += 0.5 * cd * Math.Abs(v) * v * d * dl * rhoW;
        }
        return totalGrav - totalDrag;
    }

    /// <summary>Per-unit-length residual for a single uniform cylinder.</summary>
    private static double Residual(double v, double dm, double rhoL, double rhoW, double nu)
    {
        double re = Math.Abs(v) * dm / nu;
        if (re < Tol) re = Tol;
        double cd = 1.0 + 10.0 / Math.Pow(re, 2.0 / 3.0);
        return (Math.PI / 4.0) * dm * dm * G * (rhoL - rhoW)
               - 0.5 * cd * Math.Abs(v) * v * dm * rhoW;
    }

    private static (double rhoW, double nu) WaterProps(bool isSalt, double t)
        => isSalt
            ? (1027.0 - 0.2 * t,     1.07 * KinViscFresh(t))
            : (WaterDensityFresh(t),  KinViscFresh(t));

    private static double WaterDensityFresh(double t)
        => 999.842594
           + 0.06793952        * t
           - 0.00909529        * t * t
           + 0.0001001685      * t * t * t
           - 0.000001120083    * t * t * t * t
           + 0.000000006536332 * t * t * t * t * t;

    private static double KinViscFresh(double t)
    {
        double[] temps = { 0, 5, 10, 15, 20, 25, 30, 35, 40 };
        double[] nus   =
        {
            1.7916e-6, 1.5182e-6, 1.3063e-6,
            1.1385e-6, 1.0034e-6, 0.8927e-6,
            0.8007e-6, 0.7234e-6, 0.6578e-6
        };
        if (t <= temps[0]) return nus[0];
        if (t >= temps[8]) return nus[8];
        for (int i = 0; i < 8; i++)
            if (t >= temps[i] && t <= temps[i + 1])
                return nus[i] + (nus[i + 1] - nus[i]) * (t - temps[i]) / (temps[i + 1] - temps[i]);
        return nus[4];
    }
}
