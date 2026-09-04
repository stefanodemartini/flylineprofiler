using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Services;

/// <summary>
/// Generates sibling line-weight designs (AFTMA #1–#14) from one source design,
/// preserving taper shape and material (density): every diameter (head, transition,
/// and running line alike) is scaled by the same factor, lengths unchanged, so the
/// first-30ft mass hits each target class's grain weight.
/// </summary>
public static class LineWeightFamilyCalc
{
    public const double Target30FtCm = 914.4;   // 30 ft in cm
    public const double GramsToGrains = 15.4324;

    /// <summary>Standard AFTMA line-weight target masses, in grains.</summary>
    public static readonly (int Lw, double Gr)[] Targets =
    {
        (1,60),(2,80),(3,100),(4,120),(5,140),(6,160),(7,185),
        (8,210),(9,240),(10,280),(11,330),(12,380),(13,450),(14,500)
    };

    /// <summary>Mass (grams) of the segments within the first <paramref name="targetCm"/> cm of the line.</summary>
    public static double MassInFirstCm(IEnumerable<ProjectSegment> segsSortedByStart, double targetCm)
    {
        double totalMassG = 0;
        double covered    = 0;
        foreach (var seg in segsSortedByStart)
        {
            if (covered >= targetCm || seg.StartCm >= targetCm) break;
            double segLen  = seg.LengthCm;
            double usedLen = Math.Min(segLen, targetCm - covered);
            if (segLen <= 0 || seg.SpecWeightGCm3 <= 0) { covered += usedLen; continue; }
            double frac   = usedLen / segLen;
            double r1Mm   = seg.StartDiameterMm / 2.0;
            double r2Mm   = seg.EndDiameterMm   / 2.0;
            double r2pMm  = r1Mm + (r2Mm - r1Mm) * frac;
            double lenMm  = usedLen * 10.0;
            double volMm3 = Math.PI * lenMm / 3.0 * (r1Mm * r1Mm + r1Mm * r2pMm + r2pMm * r2pMm);
            totalMassG   += volMm3 / 1000.0 * seg.SpecWeightGCm3;
            covered      += usedLen;
        }
        return totalMassG;
    }

    /// <summary>Sorts once and computes the first-30ft mass once — call before generating multiple family members from the same source, instead of letting each call redo both.</summary>
    public static (List<ProjectSegment> Sorted, double SourceGrams) PrepareSource(IEnumerable<ProjectSegment> sourceSegs)
    {
        var sorted = sourceSegs.OrderBy(seg => seg.StartCm).ToList();
        return (sorted, MassInFirstCm(sorted, Target30FtCm));
    }

    /// <summary>Closest AFTMA line-weight class and the first-30ft grain weight it was matched from. Lw=0 when it can't be computed (no density set).</summary>
    public static (int Lw, double Grains) ClassifyAffta(IEnumerable<ProjectSegment> segsSortedByStart)
    {
        var segs = segsSortedByStart as IList<ProjectSegment> ?? segsSortedByStart.ToList();
        if (segs.Count == 0 || segs.All(s => s.SpecWeightGCm3 <= 0)) return (0, 0);
        double grains = MassInFirstCm(segs, Target30FtCm) * GramsToGrains;
        if (grains <= 0) return (0, 0);
        int lw = Targets.OrderBy(t => Math.Abs(t.Gr - grains)).First().Lw;
        return (lw, grains);
    }

    public sealed class FamilyGenerationResult
    {
        public List<ProjectSegment> Segments = new();
        public Func<double, double> RemapCm = x => x;
        public double AchievedGrains;
        public bool   Achieved;
        /// <summary>Sink-speed family only: false if the required density hit the practical density floor/ceiling.</summary>
        public bool   SpeedAchievable = true;
    }

    /// <summary>
    /// Scales every diameter in <paramref name="sortedSourceSegs"/> by the same factor <c>s</c> so the
    /// first-30ft mass matches <paramref name="targetGrains"/>. For a uniform scale with lengths
    /// unchanged, mass scales exactly as <c>s²</c> (volume is quadratic in radius), so <c>s</c> is
    /// solved in closed form rather than bisected — and is always achievable for any positive
    /// target. Density is never touched — same material throughout. Positions never move, so
    /// nozzle zones and the laser mark need no remapping.
    /// </summary>
    public static FamilyGenerationResult GenerateFamilyMember(
        List<ProjectSegment> sortedSourceSegs, double sourceGrams, double targetGrains)
    {
        double targetGrams = targetGrains / GramsToGrains;
        if (sourceGrams <= 0 || targetGrams <= 0)
            return new FamilyGenerationResult { Achieved = false };

        double s = Math.Sqrt(targetGrams / sourceGrams);
        var scaled = sortedSourceSegs.Select(seg => new ProjectSegment
        {
            Index           = seg.Index,
            StartCm         = seg.StartCm,
            EndCm           = seg.EndCm,
            StartDiameterMm = seg.StartDiameterMm * s,
            EndDiameterMm   = seg.EndDiameterMm   * s,
            Name            = seg.Name,
            SpecWeightGCm3  = seg.SpecWeightGCm3,
            IsHead          = seg.IsHead
        }).ToList();

        double achievedGrams = MassInFirstCm(scaled, Target30FtCm);
        return new FamilyGenerationResult
        {
            Segments       = scaled,
            RemapCm        = x => x,
            AchievedGrains = achievedGrams * GramsToGrains,
            Achieved       = true
        };
    }

    /// <summary>
    /// Generates one sink-speed family member: same taper shape as <paramref name="sortedSourceSegs"/>,
    /// scaled by a single diameter factor <c>s</c>, with a single uniform density chosen so the
    /// line sinks at <paramref name="targetSpeedMs"/> — solved jointly with <c>s</c> so the
    /// first-30ft mass matches <paramref name="targetGrains"/> (normally the source's own mass,
    /// so every generated speed variant keeps the same nominal line-weight class, the way real
    /// product families keep e.g. WF6F/WF6I/WF6S3/WF6S5 all at "WF6"). <paramref name="targetSpeedMs"/>
    /// &lt;= 0 means floating: density is fixed at <see cref="SinkingSpeedCalc.RhoFloor"/> and only
    /// the diameter is solved (same math as <see cref="GenerateFamilyMember"/>).
    /// </summary>
    public static FamilyGenerationResult GenerateSinkSpeedFamilyMember(
        List<ProjectSegment> sortedSourceSegs, bool isSalt, double tempC,
        double targetSpeedMs, double targetGrains)
    {
        if (targetSpeedMs <= 0)
        {
            var floatSegs = sortedSourceSegs.Select(seg => new ProjectSegment
            {
                Index = seg.Index, StartCm = seg.StartCm, EndCm = seg.EndCm,
                StartDiameterMm = seg.StartDiameterMm, EndDiameterMm = seg.EndDiameterMm,
                Name = seg.Name, SpecWeightGCm3 = SinkingSpeedCalc.RhoFloor, IsHead = seg.IsHead
            }).ToList();
            double floatSourceGrams = MassInFirstCm(floatSegs, Target30FtCm);
            return GenerateFamilyMember(floatSegs, floatSourceGrams, targetGrains);
        }

        double targetGrams = targetGrains / GramsToGrains;

        (double Density, bool Achievable) DensityAt(double s)
        {
            var tuples = sortedSourceSegs.Select(seg =>
                (seg.StartDiameterMm * s, seg.EndDiameterMm * s, seg.LengthCm));
            return SinkingSpeedCalc.DensityForTargetSinkSpeed(isSalt, tempC, tuples, targetSpeedMs);
        }

        List<ProjectSegment> BuildAt(double s, double density) => sortedSourceSegs.Select(seg => new ProjectSegment
        {
            Index = seg.Index, StartCm = seg.StartCm, EndCm = seg.EndCm,
            StartDiameterMm = seg.StartDiameterMm * s, EndDiameterMm = seg.EndDiameterMm * s,
            Name = seg.Name, SpecWeightGCm3 = density, IsHead = seg.IsHead
        }).ToList();

        double MassAt(double s)
        {
            var (density, _) = DensityAt(s);
            return density <= 0 ? double.NaN : MassInFirstCm(BuildAt(s, density), Target30FtCm);
        }

        double lo = 0.1, hi = 10.0;
        double mLo = MassAt(lo), mHi = MassAt(hi);
        if (double.IsNaN(mLo) || double.IsNaN(mHi) || mHi <= mLo ||
            targetGrams < mLo || targetGrams > mHi)
        {
            return new FamilyGenerationResult { Achieved = false };
        }

        double s = 1.0;
        for (int i = 0; i < 80; i++)
        {
            s = (lo + hi) / 2.0;
            double m = MassAt(s);
            if (double.IsNaN(m)) { hi = s; continue; }
            if (Math.Abs(m - targetGrams) < 1e-6) break;
            if (m < targetGrams) lo = s; else hi = s;
        }

        var (finalDensity, speedAchievable) = DensityAt(s);
        var finalSegs = BuildAt(s, finalDensity);
        double achievedGrams = MassInFirstCm(finalSegs, Target30FtCm);

        return new FamilyGenerationResult
        {
            Segments        = finalSegs,
            RemapCm         = x => x,
            AchievedGrains  = achievedGrams * GramsToGrains,
            Achieved        = true,
            SpeedAchievable = speedAchievable
        };
    }
}
