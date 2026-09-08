using System.ComponentModel;

namespace DiametroLineaDesktop.Models;

/// <summary>
/// Represents a single section of a fly line design.
/// Each section is a truncated cone (frustum) or, when diameters are equal, a cylinder.
/// All diameters are in mm; all lengths are in cm (stored) or mm (for volume).
/// </summary>
public class ProjectSegment : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public int    Index   { get; init; }
    public double StartCm { get; init; }
    public double EndCm   { get; init; }

    private double _startDiameterMm;
    public double StartDiameterMm
    {
        get => _startDiameterMm;
        set { if (Math.Abs(_startDiameterMm - value) > 1e-9) { _startDiameterMm = value; Notify(nameof(StartDiameterMm)); Notify(nameof(VolumeCm3)); Notify(nameof(VolumeText)); Notify(nameof(MassG)); Notify(nameof(MassText)); Notify(nameof(Shape)); Notify(nameof(TaperText)); } }
    }

    private double _endDiameterMm;
    public double EndDiameterMm
    {
        get => _endDiameterMm;
        set { if (Math.Abs(_endDiameterMm - value) > 1e-9) { _endDiameterMm = value; Notify(nameof(EndDiameterMm)); Notify(nameof(VolumeCm3)); Notify(nameof(VolumeText)); Notify(nameof(MassG)); Notify(nameof(MassText)); Notify(nameof(Shape)); Notify(nameof(TaperText)); } }
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Notify(nameof(Name)); } }
    }

    private bool _isHead = false;
    public bool IsHead
    {
        get => _isHead;
        set { if (_isHead != value) { _isHead = value; Notify(nameof(IsHead)); } }
    }

    private double _specWeightGCm3 = 0.0;
    /// <summary>Specific weight in g/cm³. Zero means "not set".</summary>
    public double SpecWeightGCm3
    {
        get => _specWeightGCm3;
        set
        {
            if (Math.Abs(_specWeightGCm3 - value) > 1e-9)
            {
                _specWeightGCm3 = value;
                Notify(nameof(SpecWeightGCm3));
                Notify(nameof(EffectiveSpecWeightGCm3));
                Notify(nameof(MassG));
                Notify(nameof(MassText));
            }
        }
    }

    /// <summary>
    /// What the Sp. W. column actually shows and edits. A live zone override (see
    /// ApplyZoneDensities) never touches SpecWeightGCm3 — that field stays at the design's shared
    /// base density even while this segment's real, applied material is different — so reading
    /// SpecWeightGCm3 directly showed the base density for every zone-covered segment, not the
    /// zone's own. This reads the real applied density instead whenever one exists (averaged across
    /// its slices, for the rare case a segment straddles a zone edge), falling back to
    /// SpecWeightGCm3 otherwise. Editing always writes straight to SpecWeightGCm3 — hand-typing a
    /// density sets the design's own base material, never a zone override, which comes from the
    /// Zones grid instead.
    /// </summary>
    public double EffectiveSpecWeightGCm3
    {
        get => HasCompensation && _compSliceDensities.Length > 0
            ? _compSliceDensities.Average()
            : _specWeightGCm3;
        set => SpecWeightGCm3 = value;
    }

    public double LengthCm  { get => EndCm - StartCm; set { /* handled by CellEditEnding */ } }
    public double LengthMm  => LengthCm * 10.0;

    public bool   IsCylinder => Math.Abs(StartDiameterMm - EndDiameterMm) < 0.001;
    public string Shape      => IsCylinder ? "Cylinder" : "Taper";

    /// <summary>Volume in cm³.</summary>
    public double VolumeCm3
    {
        get
        {
            double r1     = StartDiameterMm / 2.0;
            double r2     = EndDiameterMm   / 2.0;
            double L      = LengthMm;
            double volMm3 = IsCylinder
                ? Math.PI * r1 * r1 * L
                : Math.PI * L / 3.0 * (r1 * r1 + r1 * r2 + r2 * r2);
            return volMm3 / 1000.0;
        }
    }

    /// <summary>
    /// Mass in grams, from the real applied density (EffectiveSpecWeightGCm3 — the zone's own
    /// density where one applies, not the design's base) — zero when neither is set.
    /// </summary>
    public double MassG => EffectiveSpecWeightGCm3 > 0 ? VolumeCm3 * EffectiveSpecWeightGCm3 : 0;

    public string VolumeText => $"{VolumeCm3:0.000}";
    public string MassText   => EffectiveSpecWeightGCm3 > 0 ? $"{MassG:0.000}" : "—";

    /// <summary>Taper rate in mm per metre (positive = thicker toward end, negative = taper off).</summary>
    public double TaperMmPerMeter =>
        IsCylinder ? 0.0 : (EndDiameterMm - StartDiameterMm) / (LengthCm / 100.0);

    public string TaperText =>
        IsCylinder ? "—" : $"{TaperMmPerMeter:+0.000;-0.000} mm/m";

    private double _sinkSpeedMs = double.NaN;
    /// <summary>
    /// Whole-segment rigid-body terminal sinking speed in m/s. Set externally by MainWindow only
    /// for a segment with real per-slice materials (a zone with its own density) — there the
    /// segment genuinely is one physically joined piece, so treating it as one rigid body is the
    /// correct model. A plain uniform-density segment instead uses <see cref="SinkSpeedStartMs"/>/
    /// <see cref="SinkSpeedEndMs"/> below; see SinkSpeedText for which one wins.
    /// </summary>
    public double SinkSpeedMs
    {
        get => _sinkSpeedMs;
        set { if (_sinkSpeedMs != value) { _sinkSpeedMs = value; Notify(nameof(SinkSpeedMs)); Notify(nameof(SinkSpeedText)); } }
    }

    private double _sinkSpeedStartMs = double.NaN;
    private double _sinkSpeedEndMs   = double.NaN;
    /// <summary>
    /// Local (isolated-cylinder) terminal sink speed in m/s at this segment's own Start/End
    /// diameter — the same per-point model the chart's Sink Map heat-map already colours by,
    /// not a whole-segment average. Set externally by MainWindow; NaN for a segment whose real
    /// speed instead comes from <see cref="SinkSpeedMs"/> (see its own comment).
    /// </summary>
    public double SinkSpeedStartMs
    {
        get => _sinkSpeedStartMs;
        set { if (_sinkSpeedStartMs != value) { _sinkSpeedStartMs = value; Notify(nameof(SinkSpeedStartMs)); Notify(nameof(SinkSpeedText)); } }
    }

    public double SinkSpeedEndMs
    {
        get => _sinkSpeedEndMs;
        set { if (_sinkSpeedEndMs != value) { _sinkSpeedEndMs = value; Notify(nameof(SinkSpeedEndMs)); Notify(nameof(SinkSpeedText)); } }
    }

    public string SinkSpeedText
    {
        get
        {
            // Local model (matches the Sink Map): show the true range across this segment — a
            // taper shows exactly the two extremes the colour gradient shows, a cylinder
            // collapses to one value since both ends are the same diameter.
            if (!double.IsNaN(_sinkSpeedStartMs) && !double.IsNaN(_sinkSpeedEndMs))
            {
                double sIns = _sinkSpeedStartMs * 39.3701;
                double eIns = _sinkSpeedEndMs   * 39.3701;
                if (sIns <= 0 && eIns <= 0) return "floating";
                if (Math.Abs(sIns - eIns) < 0.001) return $"{sIns:0.000} in/s";
                return $"{sIns:0.000} → {eIns:0.000} in/s";
            }
            // Rigid-body model (zone-derived segment — see SinkSpeedMs's own comment)
            if (double.IsNaN(_sinkSpeedMs)) return "—";
            double ins = _sinkSpeedMs * 39.3701;
            if (ins <= 0) return "floating";
            return $"{ins:0.000} in/s";
        }
    }

    // Compensated profile — per-slice results (set by ComputeCompensation)
    private double[] _compSliceXsCm      = Array.Empty<double>();
    private double[] _compSliceDiamsMm   = Array.Empty<double>();
    private double[] _compSliceDensities = Array.Empty<double>(); // g/cm³ per slice
    private bool[]   _compSliceClamped   = Array.Empty<bool>();   // true → density hit RhoFloor
    private double   _compStartCm = 0;

    public double[] CompSliceXsCm       => _compSliceXsCm;
    public double[] CompSliceDiamsMm    => _compSliceDiamsMm;
    public double[] CompSliceDensities  => _compSliceDensities;
    public bool[]   CompSliceClamped    => _compSliceClamped;
    public double   CompStartCm         => _compStartCm;
    public bool HasClampedSlices        => _compSliceClamped.Any(c => c);

    private double _compensatedTargetSpeedMs = double.NaN;
    public double CompensatedTargetSpeedMs
    {
        get => _compensatedTargetSpeedMs;
        set { if (_compensatedTargetSpeedMs != value) { _compensatedTargetSpeedMs = value; Notify(nameof(CompensatedTargetSpeedMs)); Notify(nameof(CompSpeedText)); } }
    }

    public bool HasCompensation => _compSliceDiamsMm.Length > 0;

    public void SetCompensation(double startCm, double[] sliceXsCm, double[] sliceDiamsMm, double[] sliceDensities, bool[] clamped, double targetSpeedMs)
    {
        _compStartCm        = startCm;
        _compSliceXsCm      = sliceXsCm;
        _compSliceDiamsMm   = sliceDiamsMm;
        _compSliceDensities = sliceDensities;
        _compSliceClamped   = clamped;
        _compensatedTargetSpeedMs = targetSpeedMs;
        Notify(nameof(HasCompensation));
        Notify(nameof(HasClampedSlices));
        Notify(nameof(CompSpeedText));
        Notify(nameof(CompStartDiamText));
        Notify(nameof(CompEndDiamText));
        Notify(nameof(CompStartDensityText));
        Notify(nameof(CompEndDensityText));
        Notify(nameof(EffectiveSpecWeightGCm3));
        Notify(nameof(MassG));
        Notify(nameof(MassText));
    }

    public void ClearCompensation()
    {
        _compSliceXsCm      = Array.Empty<double>();
        _compSliceDiamsMm   = Array.Empty<double>();
        _compSliceDensities = Array.Empty<double>();
        _compSliceClamped   = Array.Empty<bool>();
        _compensatedTargetSpeedMs = double.NaN;
        Notify(nameof(HasCompensation));
        Notify(nameof(HasClampedSlices));
        Notify(nameof(CompSpeedText));
        Notify(nameof(CompStartDiamText));
        Notify(nameof(CompEndDiamText));
        Notify(nameof(CompStartDensityText));
        Notify(nameof(CompEndDensityText));
        Notify(nameof(EffectiveSpecWeightGCm3));
        Notify(nameof(MassG));
        Notify(nameof(MassText));
    }

    public string CompSpeedText
    {
        get
        {
            if (double.IsNaN(_compensatedTargetSpeedMs)) return "—";
            double ins = _compensatedTargetSpeedMs * 39.3701;
            if (ins <= 0) return "floating";
            return $"{ins:0.000} in/s";
        }
    }

    /// <summary>Compensated diameter at the start of this segment (first slice).</summary>
    public string CompStartDiamText => _compSliceDiamsMm.Length > 0
        ? $"{_compSliceDiamsMm[0]:0.00}" : "—";

    /// <summary>Compensated diameter at the end of this segment (last slice).</summary>
    public string CompEndDiamText => _compSliceDiamsMm.Length > 0
        ? $"{_compSliceDiamsMm[_compSliceDiamsMm.Length - 1]:0.00}" : "—";

    /// <summary>Required density at start of compensated segment (first slice).</summary>
    public string CompStartDensityText => _compSliceDensities.Length > 0
        ? $"{_compSliceDensities[0]:0.000}" : "—";

    /// <summary>Required density at end of compensated segment (last slice).</summary>
    public string CompEndDensityText => _compSliceDensities.Length > 0
        ? $"{_compSliceDensities[_compSliceDensities.Length - 1]:0.000}" : "—";

    // Legacy alias (used by CSV export)
    public double VolumeMm3 => VolumeCm3 * 1000.0;
}
