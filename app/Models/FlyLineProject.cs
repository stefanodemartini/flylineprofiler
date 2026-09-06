namespace DiametroLineaDesktop.Models;

/// <summary>
/// Serializable model for a FlyLine Profiler project (.flp file).
/// Contains the acquired scan profile, any imported comparison series,
/// and the design nodes drawn over the profile.
/// </summary>
public class FlyLineProject
{
    /// <summary>Schema version — increment when adding breaking fields so loaders can migrate.</summary>
    public int Version { get; set; } = 1;

    public string   Name       { get; set; } = "Untitled";
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public string   Notes      { get; set; } = string.Empty;

    /// <summary>Shared density mode: all segments use the same g/cm³ value.</summary>
    public bool   UseSharedDensity    { get; set; } = true;
    public double SharedDensityGCm3   { get; set; } = 0.0;

    /// <summary>Line type and format.</summary>
    public bool IsSinking  { get; set; } = false;   // false = floating, true = sinking
    public bool IsFullLine { get; set; } = false;   // false = shooting head, true = full line

    /// <summary>Sinking speed calculation settings.</summary>
    public string WaterType { get; set; } = "fresh";   // "fresh" | "salt"
    public double WaterTempC { get; set; } = 20.0;

    public List<MeasurementPoint>      ScanPoints        { get; set; } = new();
    public List<ProjectImportedSeries> ImportedSeries    { get; set; } = new();
    public List<ProjectDesignNode>     DesignNodes       { get; set; } = new();
    /// <summary>Persists user-edited segment names, specific weights and head flag across saves.</summary>
    public List<ProjectSegmentMeta>    SegmentMetadata   { get; set; } = new();
    /// <summary>Persists manually repositioned node label offsets (px).</summary>
    public List<NodeLabelOffset>       NodeLabelOffsets  { get; set; } = new();
    /// <summary>Hex colour of the design profile line, e.g. "DC3232".</summary>
    public string DesignLineColorHex { get; set; } = "DC3232";
    /// <summary>Free-text colour description shown in the PDF (e.g. "Red / White / Green in equal parts").</summary>
    public string ColorNote { get; set; } = string.Empty;
    /// <summary>Core material (e.g. "Braided multifilament nylon", "GSP (Spectra/Dyneema) braid").</summary>
    public string CoreType { get; set; } = string.Empty;
    /// <summary>Text laser-marked on the line, shown in the PDF.</summary>
    public string LaserMark { get; set; } = string.Empty;
    /// <summary>Mark position: mm from the tip (X = 0). Empty = no mark at the tip side.</summary>
    public string LaserMarkFromTipMm { get; set; } = string.Empty;
    /// <summary>Mark position: mm from the rear end of the line. Empty = no mark at the rear side.</summary>
    public string LaserMarkFromEndMm { get; set; } = string.Empty;
    /// <summary>Coloured bands — legacy field kept for migration of old .flp files.</summary>
    public List<LineColorSection> ColorSections { get; set; } = new();

    /// <summary>Up to 4 nozzle materials, each with a fixed colour + density.</summary>
    public List<NozzleDefinition> NozzleDefinitions { get; set; } = new();
    /// <summary>Zone assignments: each zone uses one nozzle for its entire length.</summary>
    public List<NozzleZone>       NozzleZones       { get; set; } = new();

    /// <summary>Target sink speed (m/s) for the compensated profile. 0 = no C profile saved.</summary>
    public double CompTargetSpeedMs  { get; set; } = 0.0;
    /// <summary>Whether the compensated view was active when the project was last saved.</summary>
    public bool   ShowCompProfile    { get; set; } = false;
    /// <summary>True if this file is a forked compensated snapshot — it must not be compensated further.</summary>
    public bool   IsCompensatedDerivative { get; set; } = false;
    /// <summary>
    /// When a zone was given its own density (rather than a whole-line target-speed compensation),
    /// whether its diameters were mass-adjusted (true, the default) or left as drawn (false).
    /// Re-applied silently on load so a zone-derived C profile doesn't need re-confirming every time.
    /// </summary>
    public bool   ZoneDensityAdaptDiameters { get; set; } = true;
}

/// <summary>One nozzle = one material: a fixed (colour, density) compound.</summary>
public class NozzleDefinition
{
    public string ColorHex    { get; set; } = "DC3232";   // 6-char RGB, no '#'
    public double DensityGCm3 { get; set; } = 0.0;
    public string Label       { get; set; } = string.Empty;
}

/// <summary>A zone on the line that is extruded by a single nozzle.</summary>
public class NozzleZone
{
    public double StartCm    { get; set; }
    public double EndCm      { get; set; }
    public int    NozzleIndex { get; set; }   // 0-based index into NozzleDefinitions
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

public class NodeLabelOffset
{
    public double NodeX { get; set; }
    public double LX    { get; set; }   // absolute data-coord X of label anchor
    public double LY    { get; set; }   // absolute data-coord Y of label anchor
}

public class LineColorSection
{
    public double StartCm  { get; set; }
    public double EndCm    { get; set; }
    public string ColorHex { get; set; } = "DC3232";  // 6-char RGB hex, no #
    public string Label    { get; set; } = string.Empty;
}

public class ProjectSegmentMeta
{
    public double StartCm    { get; set; }
    public double EndCm      { get; set; }
    public string Name       { get; set; } = string.Empty;
    public double SpecWeight { get; set; } = 0.0;
    public bool   IsHead     { get; set; } = false;
}
