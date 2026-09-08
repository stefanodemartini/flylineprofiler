using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Design spec — the headless input format
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A complete fly line description in the terms a designer actually thinks in: a chain of tapered
/// sections plus, optionally, stretches extruded from a different material. Deliberately smaller
/// than <see cref="FlyLineProject"/>, which also carries scan data, view state and derived values —
/// this is only what a design *is*, so it can be written by hand or by an agent and round-tripped.
/// </summary>
public sealed class LineDesignSpec
{
    public string Name       { get; set; } = "Untitled";
    public string Notes      { get; set; } = string.Empty;

    /// <summary>false = shooting head (every section is head), true = full line.</summary>
    public bool   IsFullLine { get; set; }
    public bool   IsSinking  { get; set; }
    public string WaterType  { get; set; } = "fresh";     // "fresh" | "salt"
    public double WaterTempC { get; set; } = 20.0;

    /// <summary>Base material (M1) — used everywhere no zone covers.</summary>
    public double DensityGCm3  { get; set; }
    public string BaseColorHex { get; set; } = "DC3232";

    public string CoreType  { get; set; } = string.Empty;
    public string ColorNote { get; set; } = string.Empty;
    public string LaserMark { get; set; } = string.Empty;

    public List<SpecSegment> Segments { get; set; } = new();
    public List<SpecZone>    Zones    { get; set; } = new();
}

/// <summary>One tapered section, described by its length and where its diameter ends up.</summary>
public sealed class SpecSegment
{
    public string  Name            { get; set; } = string.Empty;
    public double  LengthCm        { get; set; }
    /// <summary>Omit to continue from the previous section's end diameter — the normal case, and
    /// the only way to stay continuous. Giving a value that disagrees with it is rejected.</summary>
    public double? StartDiameterMm { get; set; }
    public double  EndDiameterMm   { get; set; }
    /// <summary>Omit to let the format decide: every section of a shooting head is head.</summary>
    public bool?   IsHead          { get; set; }
}

/// <summary>A stretch extruded from a material other than the base one.</summary>
public sealed class SpecZone
{
    public double StartCm     { get; set; }
    public double EndCm       { get; set; }
    public double DensityGCm3 { get; set; }
    public string ColorHex    { get; set; } = string.Empty;
    public string Label       { get; set; } = string.Empty;
    /// <summary>
    /// true (default): the drawn diameters inside the zone are scaled by √(ρ_base/ρ_zone) so the
    /// stretch keeps the mass it had in the base material, and the zone edges are pinned to real
    /// design nodes so the taper stays continuous across them. false: diameters are left as drawn
    /// and the stretch simply weighs more (or less).
    /// </summary>
    public bool   PreserveMass { get; set; } = true;
}

// ─────────────────────────────────────────────────────────────────────────────
//  Builder
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DesignSpecException : Exception
{
    public DesignSpecException(string message) : base(message) { }
}

/// <summary>
/// Turns a <see cref="LineDesignSpec"/> into a real project — and back. UI-free on purpose: the
/// same code can be driven from a CLI, a test, or an agent, and produces files indistinguishable
/// from ones drawn by hand in the app.
/// </summary>
public static class LineDesignBuilder
{
    /// <summary>Below this the app treats a zone as the base material and ignores it entirely.</summary>
    public const double MinZoneDensityDelta = 0.02;
    private const double Eps = 1e-6;

    // ── Build ────────────────────────────────────────────────────────────────

    public static FlyLineProject Build(LineDesignSpec spec) => Build(spec, out _);

    public static FlyLineProject Build(LineDesignSpec spec, out List<string> notes)
    {
        notes = new List<string>();
        Validate(spec, notes);

        // 1. Chain the sections into nodes. Each gap between consecutive nodes remembers which
        //    spec section it came from, so a node inserted later at a zone edge splits a section
        //    without losing its name or head flag.
        var nodes  = new List<ProjectDesignNode>();
        var owners = new List<int>();                 // owners[i] owns the gap nodes[i]→nodes[i+1]

        nodes.Add(new ProjectDesignNode { X = 0, Y = spec.Segments[0].StartDiameterMm!.Value });
        double x = 0;
        for (int i = 0; i < spec.Segments.Count; i++)
        {
            x += spec.Segments[i].LengthCm;
            nodes.Add(new ProjectDesignNode { X = Round(x), Y = spec.Segments[i].EndDiameterMm });
            owners.Add(i);
        }
        double lineEndCm = nodes[^1].X;

        // 2. Pin every zone edge to a real node — inserting one, on the *original* taper, where
        //    there isn't one already. A node carries a single diameter shared by both sections
        //    meeting there, so once the edge is a node the profile cannot step across it no matter
        //    what the scaling below does.
        foreach (var z in spec.Zones.Where(z => z.PreserveMass))
        {
            EnsureNodeAt(nodes, owners, z.StartCm);
            EnsureNodeAt(nodes, owners, z.EndCm);
        }

        // 3. Mass-preserving scaling: ρ_new·d_new² = ρ_base·d_base². The factor is constant across
        //    a zone, and scaling both ends of a straight section by the same factor scales every
        //    point between them by it too — so mass is conserved exactly along the whole stretch,
        //    not merely at sampled points.
        var scaledBy = new Dictionary<int, double>();   // node index → factor already applied
        foreach (var z in spec.Zones.Where(z => z.PreserveMass))
        {
            double factor = Math.Sqrt(spec.DensityGCm3 / z.DensityGCm3);
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].X < z.StartCm - Eps || nodes[i].X > z.EndCm + Eps) continue;
                if (scaledBy.TryGetValue(i, out double already) && Math.Abs(already - factor) > Eps)
                    throw new DesignSpecException(
                        $"Node at {nodes[i].X:0.#} cm sits on the edge between two mass-preserving zones of " +
                        "different density. One node cannot hold both diameters — the design would step there. " +
                        "Separate the zones, give them the same density, or set preserveMass=false on one.");
                nodes[i] = new ProjectDesignNode { X = nodes[i].X, Y = Round4(nodes[i].Y * factor) };
                scaledBy[i] = factor;
            }
        }

        // 4. Metadata: one entry per gap, inheriting name/head from the section it came from.
        var meta = new List<ProjectSegmentMeta>();
        for (int i = 0; i < owners.Count; i++)
        {
            var src = spec.Segments[owners[i]];
            meta.Add(new ProjectSegmentMeta
            {
                StartCm    = nodes[i].X,
                EndCm      = nodes[i + 1].X,
                Name       = string.IsNullOrWhiteSpace(src.Name) ? $"S{owners[i] + 1}" : src.Name,
                SpecWeight = spec.DensityGCm3,
                IsHead     = src.IsHead ?? !spec.IsFullLine,
            });
        }

        // 5. Nozzles: slot 0 is always the base material; zones sharing a material share a slot.
        var nozzles = new List<NozzleDefinition>
        {
            new() { ColorHex = Hex(spec.BaseColorHex, "DC3232"), DensityGCm3 = spec.DensityGCm3, Label = "Base" }
        };
        var zoneAssignments = new List<NozzleZone>();
        var defaultZoneColors = new[] { "FFD700", "28A428", "3296FF" };
        foreach (var z in spec.Zones)
        {
            int idx = nozzles.FindIndex(n => Math.Abs(n.DensityGCm3 - z.DensityGCm3) < Eps
                                          && (string.IsNullOrWhiteSpace(z.ColorHex)
                                              || string.Equals(n.ColorHex, Hex(z.ColorHex, ""), StringComparison.OrdinalIgnoreCase)));
            if (idx < 0)
            {
                if (nozzles.Count >= 4)
                    throw new DesignSpecException(
                        "More than 4 distinct materials: a line can only be extruded through 4 nozzles " +
                        "(the base material plus 3 zone materials).");
                nozzles.Add(new NozzleDefinition
                {
                    ColorHex    = Hex(z.ColorHex, defaultZoneColors[Math.Min(nozzles.Count - 1, defaultZoneColors.Length - 1)]),
                    DensityGCm3 = z.DensityGCm3,
                    Label       = z.Label,
                });
                idx = nozzles.Count - 1;
            }
            zoneAssignments.Add(new NozzleZone { StartCm = z.StartCm, EndCm = z.EndCm, NozzleIndex = idx });
        }
        while (nozzles.Count < 4)
            nozzles.Add(new NozzleDefinition { ColorHex = defaultZoneColors[nozzles.Count - 1], DensityGCm3 = 0 });

        var project = ProjectService.New(spec.Name);
        project.Notes             = spec.Notes;
        project.UseSharedDensity  = true;
        project.SharedDensityGCm3 = spec.DensityGCm3;
        project.IsSinking         = spec.IsSinking;
        project.IsFullLine        = spec.IsFullLine;
        project.WaterType         = spec.WaterType;
        project.WaterTempC        = spec.WaterTempC;
        project.DesignNodes       = nodes;
        project.SegmentMetadata   = meta;
        project.NozzleDefinitions = nozzles;
        project.NozzleZones       = zoneAssignments;
        project.DesignLineColorHex = Hex(spec.BaseColorHex, "DC3232");
        project.CoreType          = spec.CoreType;
        project.ColorNote         = spec.ColorNote;
        project.LaserMark         = spec.LaserMark;
        // Any mass preservation asked for is already baked into the nodes above. Leaving this true
        // would make the app scale those same diameters a second time on load.
        project.ZoneDensityAdaptDiameters = false;

        if (spec.Zones.Any(z => !z.PreserveMass))
            notes.Add("Zones with preserveMass=false keep the drawn diameters — their mass changes with the density.");
        notes.Add($"Line built: {nodes.Count} nodes, {meta.Count} sections, {lineEndCm:0.#} cm total.");
        return project;
    }

    private static void Validate(LineDesignSpec spec, List<string> notes)
    {
        if (spec.Segments.Count == 0)
            throw new DesignSpecException("The spec has no segments — a line needs at least one section.");
        if (spec.DensityGCm3 <= 0)
            throw new DesignSpecException("densityGCm3 (the base material) must be greater than 0.");
        if (spec.Segments[0].StartDiameterMm is null or <= 0)
            throw new DesignSpecException("The first segment must give startDiameterMm — there is no previous section to continue from.");

        double prevEnd = spec.Segments[0].StartDiameterMm!.Value;
        double x = 0;
        for (int i = 0; i < spec.Segments.Count; i++)
        {
            var s = spec.Segments[i];
            string where = $"segment {i + 1}" + (string.IsNullOrWhiteSpace(s.Name) ? "" : $" ('{s.Name}')");
            if (s.LengthCm <= 0)      throw new DesignSpecException($"{where}: lengthCm must be greater than 0.");
            if (s.EndDiameterMm <= 0) throw new DesignSpecException($"{where}: endDiameterMm must be greater than 0.");
            if (i > 0 && s.StartDiameterMm is double sd && Math.Abs(sd - prevEnd) > 1e-4)
                throw new DesignSpecException(
                    $"{where}: startDiameterMm {sd:0.###} doesn't match the previous section's end {prevEnd:0.###} — " +
                    "that is a diameter step, which no extruder can make. Omit startDiameterMm to continue smoothly.");
            prevEnd = s.EndDiameterMm;
            x      += s.LengthCm;
        }
        double lineEnd = x;

        var zones = spec.Zones.OrderBy(z => z.StartCm).ToList();
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (z.EndCm <= z.StartCm)
                throw new DesignSpecException($"Zone {z.StartCm:0.#}–{z.EndCm:0.#} cm: endCm must be greater than startCm.");
            if (z.StartCm < -Eps || z.EndCm > lineEnd + Eps)
                throw new DesignSpecException($"Zone {z.StartCm:0.#}–{z.EndCm:0.#} cm falls outside the line (0–{lineEnd:0.#} cm).");
            if (z.DensityGCm3 <= 0)
                throw new DesignSpecException($"Zone {z.StartCm:0.#}–{z.EndCm:0.#} cm: densityGCm3 must be greater than 0.");
            if (i > 0 && z.StartCm < zones[i - 1].EndCm - Eps)
                throw new DesignSpecException($"Zones {zones[i - 1].StartCm:0.#}–{zones[i - 1].EndCm:0.#} and {z.StartCm:0.#}–{z.EndCm:0.#} cm overlap.");
            if (Math.Abs(z.DensityGCm3 - spec.DensityGCm3) <= MinZoneDensityDelta)
                notes.Add($"Zone {z.StartCm:0.#}–{z.EndCm:0.#} cm is within {MinZoneDensityDelta:0.00} g/cm³ of the base " +
                          "material — the app treats that as the same material and will ignore the zone.");
        }
    }

    /// <summary>Inserts a node at <paramref name="atCm"/> on the current taper if none is there yet.</summary>
    private static void EnsureNodeAt(List<ProjectDesignNode> nodes, List<int> owners, double atCm)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (Math.Abs(nodes[i].X - atCm) < Eps) return;               // already a node
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            if (atCm <= nodes[i].X || atCm >= nodes[i + 1].X) continue;
            double t = (atCm - nodes[i].X) / (nodes[i + 1].X - nodes[i].X);
            double y = nodes[i].Y + t * (nodes[i + 1].Y - nodes[i].Y);
            nodes.Insert(i + 1, new ProjectDesignNode { X = Round(atCm), Y = Round4(y) });
            owners.Insert(i + 1, owners[i]);                             // both halves keep the section
            return;
        }
    }

    // ── Inspect (project → spec) ─────────────────────────────────────────────

    /// <summary>
    /// Recovers the spec a project was (or could have been) built from — the way to start from a
    /// proven design: read it, change a few numbers, build it again.
    /// </summary>
    public static LineDesignSpec ToSpec(FlyLineProject p)
    {
        var nodes = p.DesignNodes.OrderBy(n => n.X).ToList();
        var spec = new LineDesignSpec
        {
            Name         = p.Name,
            Notes        = p.Notes,
            IsFullLine   = p.IsFullLine,
            IsSinking    = p.IsSinking,
            WaterType    = p.WaterType,
            WaterTempC   = p.WaterTempC,
            DensityGCm3  = p.SharedDensityGCm3 > 0
                ? p.SharedDensityGCm3
                : (p.NozzleDefinitions.Count > 0 ? p.NozzleDefinitions[0].DensityGCm3 : 0),
            BaseColorHex = p.DesignLineColorHex,
            CoreType     = p.CoreType,
            ColorNote    = p.ColorNote,
            LaserMark    = p.LaserMark,
        };
        if (nodes.Count < 2) return spec;

        for (int i = 0; i < nodes.Count - 1; i++)
        {
            var m = p.SegmentMetadata.FirstOrDefault(mm => Math.Abs(mm.StartCm - nodes[i].X) < Eps);
            spec.Segments.Add(new SpecSegment
            {
                Name            = m?.Name ?? $"S{i + 1}",
                LengthCm        = Round(nodes[i + 1].X - nodes[i].X),
                StartDiameterMm = i == 0 ? nodes[i].Y : null,
                EndDiameterMm   = nodes[i + 1].Y,
                IsHead          = m is null ? null : m.IsHead,
            });
        }
        foreach (var z in p.NozzleZones)
        {
            var noz = z.NozzleIndex >= 0 && z.NozzleIndex < p.NozzleDefinitions.Count
                ? p.NozzleDefinitions[z.NozzleIndex] : null;
            spec.Zones.Add(new SpecZone
            {
                StartCm      = z.StartCm,
                EndCm        = z.EndCm,
                DensityGCm3  = noz?.DensityGCm3 ?? 0,
                ColorHex     = noz?.ColorHex ?? string.Empty,
                Label        = noz?.Label ?? string.Empty,
                // The geometry on disk already reflects whatever adaptation was applied, so
                // rebuilding must not apply it a second time.
                PreserveMass = false,
            });
        }
        return spec;
    }

    // ── Report ───────────────────────────────────────────────────────────────

    public sealed class SectionReport
    {
        public string Name        { get; set; } = string.Empty;
        public double StartCm     { get; set; }
        public double EndCm       { get; set; }
        public double StartDiamMm { get; set; }
        public double EndDiamMm   { get; set; }
        public double DensityGCm3 { get; set; }
        public double MassG       { get; set; }
        public bool   IsHead      { get; set; }
        public string SinkText    { get; set; } = string.Empty;
    }

    public sealed class DesignReport
    {
        public string Name          { get; set; } = string.Empty;
        public double TotalLengthCm { get; set; }
        public double HeadLengthCm  { get; set; }
        public double TotalMassG     { get; set; }
        public double TotalMassGrains { get; set; }
        public double HeadMassGrains  { get; set; }
        public int    AfftaClass      { get; set; }
        public double Affta30FtGrains { get; set; }
        public List<SectionReport> Sections { get; set; } = new();
    }

    /// <summary>
    /// Everything worth checking after a build, computed with the same services the app uses —
    /// mass, AFFTA class, and each section's real sink speed at its own material.
    /// </summary>
    public static DesignReport Report(FlyLineProject p)
    {
        var segs  = ToProjectSegments(p);
        var (lw, grains) = LineWeightFamilyCalc.ClassifyAffta(segs);
        bool isSalt = p.WaterType == "salt";

        var rep = new DesignReport
        {
            Name            = p.Name,
            TotalLengthCm   = segs.Count > 0 ? segs[^1].EndCm - segs[0].StartCm : 0,
            HeadLengthCm    = segs.Where(s => s.IsHead).Sum(s => s.LengthCm),
            TotalMassG      = segs.Sum(s => s.MassG),
            AfftaClass      = lw,
            Affta30FtGrains = grains,
        };
        rep.TotalMassGrains = rep.TotalMassG * LineWeightFamilyCalc.GramsToGrains;
        rep.HeadMassGrains  = segs.Where(s => s.IsHead).Sum(s => s.MassG) * LineWeightFamilyCalc.GramsToGrains;

        foreach (var s in segs)
        {
            double v0 = SinkingSpeedCalc.CylinderSinkSpeed(isSalt, p.WaterTempC, s.StartDiameterMm, s.SpecWeightGCm3);
            double v1 = SinkingSpeedCalc.CylinderSinkSpeed(isSalt, p.WaterTempC, s.EndDiameterMm,   s.SpecWeightGCm3);
            double i0 = Math.Max(0, v0) * 39.3701, i1 = Math.Max(0, v1) * 39.3701;
            rep.Sections.Add(new SectionReport
            {
                Name        = s.Name,
                StartCm     = s.StartCm,
                EndCm       = s.EndCm,
                StartDiamMm = s.StartDiameterMm,
                EndDiamMm   = s.EndDiameterMm,
                DensityGCm3 = s.SpecWeightGCm3,
                MassG       = s.MassG,
                IsHead      = s.IsHead,
                SinkText    = i0 <= 1e-6 && i1 <= 1e-6 ? "floating"
                            : Math.Abs(i1 - i0) < 5e-4 ? $"{i0:0.000} in/s"
                            : $"{i0:0.000} → {i1:0.000} in/s",
            });
        }
        return rep;
    }

    /// <summary>
    /// Rebuilds the segment list the way the app does on load, but with each section carrying the
    /// density really applied there (its zone's, where one covers it) rather than the design's base
    /// — so mass and AFFTA come out right for a multi-material line.
    /// </summary>
    public static List<ProjectSegment> ToProjectSegments(FlyLineProject p)
    {
        var nodes = p.DesignNodes.OrderBy(n => n.X).ToList();
        var segs  = new List<ProjectSegment>();
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            var m = p.SegmentMetadata.FirstOrDefault(mm => Math.Abs(mm.StartCm - nodes[i].X) < Eps);
            double mid = (nodes[i].X + nodes[i + 1].X) / 2.0;
            var zone = p.NozzleZones.FirstOrDefault(z => mid >= z.StartCm && mid < z.EndCm);
            double rho = zone != null && zone.NozzleIndex < p.NozzleDefinitions.Count
                         && p.NozzleDefinitions[zone.NozzleIndex].DensityGCm3 > 0
                ? p.NozzleDefinitions[zone.NozzleIndex].DensityGCm3
                : (p.UseSharedDensity ? p.SharedDensityGCm3 : m?.SpecWeight ?? 0);

            segs.Add(new ProjectSegment
            {
                Index           = i + 1,
                StartCm         = nodes[i].X,
                EndCm           = nodes[i + 1].X,
                StartDiameterMm = nodes[i].Y,
                EndDiameterMm   = nodes[i + 1].Y,
                Name            = m?.Name   ?? $"S{i + 1}",
                IsHead          = m?.IsHead ?? !p.IsFullLine,
                SpecWeightGCm3  = rho,
            });
        }
        return segs;
    }

    private static double Round(double v)  => Math.Round(v, 1);
    private static double Round4(double v) => Math.Round(v, 4);

    private static string Hex(string? value, string fallback)
    {
        string h = (value ?? string.Empty).TrimStart('#').Trim();
        return h.Length == 6 ? h.ToUpperInvariant() : fallback;
    }
}
