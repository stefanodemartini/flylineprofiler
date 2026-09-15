using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Services;

namespace DiametroLineaDesktop.Physics;

/// <summary>
/// Exposes a loaded <see cref="FlyLineProject"/> as continuous functions of arc length s (cm):
/// diameter D(s) (mm) and density ρ(s) (g/cm³). Built on top of
/// <see cref="LineDesignBuilder.ToProjectSegments"/> — the same node/zone resolution the app's own
/// segment table and PDF export use — rather than re-deriving diameter/density resolution from
/// <see cref="FlyLineProject.DesignNodes"/>/<see cref="FlyLineProject.NozzleZones"/> a second time.
/// Read-only: never touches the project it wraps.
/// </summary>
public sealed class LineArcSampler
{
    private readonly List<ProjectSegment> _pieces;

    /// <summary>Total line length in cm (0 for an empty/degenerate project).</summary>
    public double TotalLengthCm { get; }

    public LineArcSampler(FlyLineProject project)
    {
        _pieces = LineDesignBuilder.ToProjectSegments(project)
            .Where(p => p.EndCm > p.StartCm)
            .OrderBy(p => p.StartCm)
            .ToList();
        TotalLengthCm = _pieces.Count > 0 ? _pieces[^1].EndCm - _pieces[0].StartCm : 0;
    }

    /// <summary>Diameter in mm at arc length s (cm from the butt end, clamped to the line's extent).</summary>
    public double DiameterMmAt(double sCm)
    {
        var p = PieceAt(sCm);
        if (p is null) return 0;
        double t = p.LengthCm > 1e-9 ? (sCm - p.StartCm) / p.LengthCm : 0;
        return p.StartDiameterMm + t * (p.EndDiameterMm - p.StartDiameterMm);
    }

    /// <summary>Density in g/cm³ at arc length s — piecewise-constant within each resolved piece
    /// (a piece is already cut at every zone edge by <see cref="LineDesignBuilder.ToProjectSegments"/>,
    /// so there is no interpolation to do here — a step at a material boundary is physically real).</summary>
    public double DensityGCm3At(double sCm)
    {
        var p = PieceAt(sCm);
        return p?.SpecWeightGCm3 ?? 0;
    }

    /// <summary>Evenly spaced arc-length samples from 0 to <see cref="TotalLengthCm"/> inclusive,
    /// stepped at <paramref name="stepCm"/> (the last interval may be shorter to land exactly on the
    /// line's end).</summary>
    public double[] SampleArcLengths(double stepCm)
    {
        if (TotalLengthCm <= 0 || stepCm <= 0) return Array.Empty<double>();
        int n = (int)Math.Ceiling(TotalLengthCm / stepCm) + 1;
        var xs = new double[n];
        for (int i = 0; i < n - 1; i++) xs[i] = i * stepCm;
        xs[n - 1] = TotalLengthCm;
        return xs;
    }

    private ProjectSegment? PieceAt(double sCm)
    {
        if (_pieces.Count == 0) return null;
        double clamped = Math.Clamp(sCm, _pieces[0].StartCm, _pieces[^1].EndCm);
        foreach (var p in _pieces)
            if (clamped >= p.StartCm - 1e-9 && clamped <= p.EndCm + 1e-9) return p;
        return _pieces[^1];
    }
}
