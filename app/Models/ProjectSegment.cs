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
                Notify(nameof(MassG));
                Notify(nameof(MassText));
            }
        }
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

    /// <summary>Mass in grams. Zero when SpecWeightGCm3 is not set.</summary>
    public double MassG => _specWeightGCm3 > 0 ? VolumeCm3 * _specWeightGCm3 : 0;

    public string VolumeText => $"{VolumeCm3:0.000}";
    public string MassText   => _specWeightGCm3 > 0 ? $"{MassG:0.000}" : "—";

    /// <summary>Taper rate in mm per metre (positive = thicker toward end, negative = taper off).</summary>
    public double TaperMmPerMeter =>
        IsCylinder ? 0.0 : (EndDiameterMm - StartDiameterMm) / (LengthCm / 100.0);

    public string TaperText =>
        IsCylinder ? "—" : $"{TaperMmPerMeter:+0.000;-0.000} mm/m";

    private double _sinkSpeedMs = double.NaN;
    /// <summary>Terminal sinking speed in m/s. Set externally by MainWindow after computing.</summary>
    public double SinkSpeedMs
    {
        get => _sinkSpeedMs;
        set { if (_sinkSpeedMs != value) { _sinkSpeedMs = value; Notify(nameof(SinkSpeedMs)); Notify(nameof(SinkSpeedText)); } }
    }

    public string SinkSpeedText
    {
        get
        {
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
    private double   _compStartCm = 0;

    public double[] CompSliceXsCm       => _compSliceXsCm;
    public double[] CompSliceDiamsMm    => _compSliceDiamsMm;
    public double[] CompSliceDensities  => _compSliceDensities;
    public double   CompStartCm         => _compStartCm;

    private double _compensatedTargetSpeedMs = double.NaN;
    public double CompensatedTargetSpeedMs
    {
        get => _compensatedTargetSpeedMs;
        set { if (_compensatedTargetSpeedMs != value) { _compensatedTargetSpeedMs = value; Notify(nameof(CompensatedTargetSpeedMs)); Notify(nameof(CompSpeedText)); } }
    }

    public bool HasCompensation => _compSliceDiamsMm.Length > 0;

    public void SetCompensation(double startCm, double[] sliceXsCm, double[] sliceDiamsMm, double[] sliceDensities, double targetSpeedMs)
    {
        _compStartCm        = startCm;
        _compSliceXsCm      = sliceXsCm;
        _compSliceDiamsMm   = sliceDiamsMm;
        _compSliceDensities = sliceDensities;
        _compensatedTargetSpeedMs = targetSpeedMs;
        Notify(nameof(HasCompensation));
        Notify(nameof(CompSpeedText));
        Notify(nameof(CompStartDiamText));
        Notify(nameof(CompEndDiamText));
    }

    public void ClearCompensation()
    {
        _compSliceXsCm      = Array.Empty<double>();
        _compSliceDiamsMm   = Array.Empty<double>();
        _compSliceDensities = Array.Empty<double>();
        _compensatedTargetSpeedMs = double.NaN;
        Notify(nameof(HasCompensation));
        Notify(nameof(CompSpeedText));
        Notify(nameof(CompStartDiamText));
        Notify(nameof(CompEndDiamText));
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
        ? $"{_compSliceDiamsMm[0]:0.000}" : "—";

    /// <summary>Compensated diameter at the end of this segment (last slice).</summary>
    public string CompEndDiamText => _compSliceDiamsMm.Length > 0
        ? $"{_compSliceDiamsMm[_compSliceDiamsMm.Length - 1]:0.000}" : "—";

    // Legacy alias (used by CSV export)
    public double VolumeMm3 => VolumeCm3 * 1000.0;
}
