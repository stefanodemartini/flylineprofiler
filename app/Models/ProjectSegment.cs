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

    public int    Index            { get; init; }
    public double StartCm         { get; init; }
    public double EndCm           { get; init; }
    public double StartDiameterMm { get; init; }
    public double EndDiameterMm   { get; init; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Notify(nameof(Name)); } }
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

    public double LengthCm  => EndCm - StartCm;
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

    // Legacy alias (used by CSV export)
    public double VolumeMm3 => VolumeCm3 * 1000.0;
}
