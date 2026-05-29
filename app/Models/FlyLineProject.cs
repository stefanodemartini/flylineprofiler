namespace DiametroLineaDesktop.Models;

/// <summary>
/// Serializable model for a FlyLine Profiler project (.flp file).
/// Contains the acquired scan profile, any imported comparison series,
/// and the design nodes drawn over the profile.
/// </summary>
public class FlyLineProject
{
    public string   Name       { get; set; } = "Untitled";
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public string   Notes      { get; set; } = string.Empty;

    public List<MeasurementPoint>      ScanPoints     { get; set; } = new();
    public List<ProjectImportedSeries> ImportedSeries { get; set; } = new();
    public List<ProjectDesignNode>     DesignNodes    { get; set; } = new();
}

public class ProjectImportedSeries
{
    public string   Name     { get; set; } = string.Empty;
    public double[] Xs       { get; set; } = Array.Empty<double>();
    public double[] Ys       { get; set; } = Array.Empty<double>();
    /// <summary>RGB hex color string, e.g. "#28C996".</summary>
    public string   ColorHex { get; set; } = "#28C996";
}

public class ProjectDesignNode
{
    public double X { get; set; }   // position in cm
    public double Y { get; set; }   // full diameter in mm
}
