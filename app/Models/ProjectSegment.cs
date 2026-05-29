namespace DiametroLineaDesktop.Models;

/// <summary>
/// Represents a single section of a fly line design.
/// Each section is a truncated cone (frustum) or, when diameters are equal, a cylinder.
/// All diameters are in mm; all lengths are in cm (stored) or mm (for volume).
/// </summary>
public class ProjectSegment
{
    public int    Index             { get; init; }
    public double StartCm          { get; init; }
    public double EndCm            { get; init; }
    public double StartDiameterMm  { get; init; }
    public double EndDiameterMm    { get; init; }

    public double LengthCm  => EndCm - StartCm;
    public double LengthMm  => LengthCm * 10.0;

    public bool   IsCylinder => Math.Abs(StartDiameterMm - EndDiameterMm) < 0.001;
    public string Shape      => IsCylinder ? "Cylinder" : "Frustum";

    public double VolumeMm3
    {
        get
        {
            double r1 = StartDiameterMm / 2.0;
            double r2 = EndDiameterMm   / 2.0;
            double L  = LengthMm;
            return IsCylinder
                ? Math.PI * r1 * r1 * L
                : Math.PI * L / 3.0 * (r1 * r1 + r1 * r2 + r2 * r2);
        }
    }

    public string VolumeText => $"{VolumeMm3 / 1000.0:0.00} cm\u00b3";

    /// <summary>Taper rate in mm per metre (positive = thicker toward end, negative = taper off).</summary>
    public double TaperMmPerMeter =>
        IsCylinder ? 0.0 : (EndDiameterMm - StartDiameterMm) / (LengthCm / 100.0);

    public string TaperText =>
        IsCylinder ? "—" : $"{TaperMmPerMeter:+0.000;-0.000} mm/m";
}
