using DiametroLineaDesktop.Models;
using ScottColor = ScottPlot.Color;

namespace DiametroLineaDesktop.Services;

/// <summary>
/// Geometry and colour lookups shared by every chart renderer — the on-screen chart
/// (MainWindow.RenderSegmentOverlay), the PDF chart image (ChartRenderer, used by both the GUI's
/// PDF export and the headless CLI) — so a fix here is a fix everywhere at once, instead of the
/// same bug needing to be found and fixed separately in each renderer (which happened more than
/// once before this was pulled out: see the display-consistency skill).
///
/// Everything here is a pure function of the segments/zones/nozzles passed in — no UI state, no
/// mutation — which is what makes it safe to call from a console app with no window at all.
/// </summary>
public static class ChartGeometry
{
    /// <summary>
    /// Builds a node list from compensated segment start/end diameters so the compensated profile
    /// can be rendered in the same style as the design profile. A tiny epsilon is added when two
    /// consecutive nodes share the same X to avoid division-by-zero in piecewise interpolation.
    /// </summary>
    public static List<(double X, double Y)> GetCompNodes(IReadOnlyList<ProjectSegment> segments)
    {
        var nodes = new List<(double X, double Y)>();
        foreach (var seg in segments.OrderBy(s => s.StartCm).Where(s => s.HasCompensation))
        {
            double xs = seg.StartCm;
            if (nodes.Count > 0 && Math.Abs(nodes[^1].X - xs) < 1e-6)
                xs += 1e-4;
            nodes.Add((xs, seg.CompSliceDiamsMm[0]));
            nodes.Add((seg.EndCm, seg.CompSliceDiamsMm[^1]));
        }
        return nodes;
    }

    /// <summary>
    /// The real material zones as (start, end, density) spans, found from each segment's own
    /// compensated slices — not from a segment's top-level SpecWeightGCm3, which for a LIVE
    /// (not-yet-baked) zone design stays at the shared base density regardless of its zones
    /// (ApplyZoneDensities only ever writes the real per-slice material into CompSliceDensities). A
    /// material can also start or end mid-segment — "il cambio di colore può avvenire in un punto
    /// qualsiasi dei tapers senza che coincida con il cambio di pendenza" — so this flattens every
    /// HasCompensation segment's own slices into one continuous sequence and walks that, which finds
    /// a transition wherever it really is, whether that's at a segment boundary (a loaded/baked
    /// snapshot, effectively one slice per segment) or partway through one (a live zone design).
    /// Kept as the one place that computes these spans so material boundaries and the "M{n}" tag
    /// rendering can't drift apart into two different ideas of where a zone starts and ends.
    /// </summary>
    public static List<(double StartX, double EndX, double Density)> GetMaterialZoneSpans(
        IReadOnlyList<ProjectSegment> segments, List<(double X, double Y)> sorted)
    {
        var flat = new List<(double X, double Density)>();
        foreach (var seg in segments.OrderBy(s => s.StartCm).Where(s => s.HasCompensation))
        {
            int ns = seg.CompSliceXsCm.Length;
            for (int i = 0; i < ns; i++)
                flat.Add((seg.StartCm + seg.CompSliceXsCm[i], seg.CompSliceDensities[i]));
        }
        if (flat.Count == 0) return new();
        flat = flat.OrderBy(f => f.X).ToList();

        var spans = new List<(double StartX, double EndX, double Density)>();
        double spanStart = sorted[0].X;
        for (int i = 0; i < flat.Count; i++)
        {
            bool last = i == flat.Count - 1;
            if (!last && Math.Abs(flat[i + 1].Density - flat[i].Density) <= 1e-6) continue;
            double x = last ? sorted[^1].X : (flat[i].X + flat[i + 1].X) / 2.0;
            spans.Add((spanStart, x, flat[i].Density));
            spanStart = x;
        }
        return spans;
    }

    /// <summary>The real material-zone boundaries, from the same slice-flattening as <see cref="GetMaterialZoneSpans"/>.</summary>
    public static List<(double X, double Y)> GetMaterialZoneBoundaries(
        IReadOnlyList<ProjectSegment> segments, List<(double X, double Y)> sorted)
    {
        var spans = GetMaterialZoneSpans(segments, sorted);
        if (spans.Count == 0) return sorted;
        var result = new List<(double X, double Y)> { (spans[0].StartX, InterpolateProfileY(sorted, spans[0].StartX)) };
        foreach (var s in spans)
            result.Add((s.EndX, InterpolateProfileY(sorted, s.EndX)));
        return result;
    }

    /// <summary>
    /// Where the ORIGINAL taper's own shape changes — every fine slice inherits the Name of the
    /// real NC segment it was cut from (see BuildCompensatedSnapshotProject), so a Name change here
    /// marks a true taper transition (e.g. a straight taper ending into a level run), independent of
    /// whether the material also changes there. A manufacturer needs the diameter and position at
    /// these points just as much as at a material change — the two are different things and don't
    /// always land on the same X (see <see cref="GetManufacturingCheckpoints"/>, which unions both).
    /// Walks <paramref name="segments"/> by its own StartCm/EndCm and interpolates Y from whichever
    /// <paramref name="sorted"/> node list the caller passed — never by indexing into it directly.
    /// </summary>
    public static List<(double X, double Y)> GetTaperShapeBoundaries(
        IReadOnlyList<ProjectSegment> segments, List<(double X, double Y)> sorted)
    {
        var segs = segments.OrderBy(s => s.StartCm).ToList();
        if (segs.Count == 0) return sorted;

        var result = new List<(double X, double Y)> { (segs[0].StartCm, InterpolateProfileY(sorted, segs[0].StartCm)) };
        for (int i = 0; i < segs.Count; i++)
        {
            bool lastSeg = i == segs.Count - 1;
            if (lastSeg || segs[i + 1].Name != segs[i].Name)
                result.Add((segs[i].EndCm, InterpolateProfileY(sorted, segs[i].EndCm)));
        }
        return result;
    }

    /// <summary>
    /// Every position a producer actually needs the diameter called out for: where the material
    /// changes (<see cref="GetMaterialZoneBoundaries"/>) UNION where the taper shape changes
    /// (<see cref="GetTaperShapeBoundaries"/>) — the two don't necessarily coincide, so neither list
    /// alone is enough.
    /// </summary>
    public static List<(double X, double Y)> GetManufacturingCheckpoints(
        IReadOnlyList<ProjectSegment> segments, List<(double X, double Y)> sorted)
    {
        return GetMaterialZoneBoundaries(segments, sorted)
            .Concat(GetTaperShapeBoundaries(segments, sorted))
            .GroupBy(n => Math.Round(n.X, 3))
            .Select(g => g.First())
            .OrderBy(n => n.X)
            .ToList();
    }

    /// <summary>
    /// The colour to paint at one slice of a C profile: if a real Nozzle Zone covers this position,
    /// its own user-chosen colour (or M1's, outside any zone) — otherwise (no zones defined at all,
    /// e.g. a plain physics target-speed compensation) the auto density gradient. Used for both the
    /// live preview and a loaded baked snapshot alike.
    /// </summary>
    public static ScottColor GetSliceColor(double xAbsCm, double density, double minDens, double densRng,
        IReadOnlyList<NozzleZone> zones, IReadOnlyList<NozzleDefinition> nozzles)
    {
        if (zones.Count > 0)
        {
            var zone = zones.FirstOrDefault(z => xAbsCm >= z.StartCm && xAbsCm < z.EndCm);
            // A NozzleZone itself carries no colour — only which nozzle it uses; the colour is the
            // nozzle's own (matches NozzleZoneVm.ColorHex, which is likewise just nozzles[idx]).
            string hex = zone != null && zone.NozzleIndex >= 0 && zone.NozzleIndex < nozzles.Count
                ? nozzles[zone.NozzleIndex].ColorHex
                : (nozzles.Count > 0 ? nozzles[0].ColorHex : "DC3232");
            if (TryParseHexColor(hex, out var col)) return col;
        }
        return DensityColor(densRng > 0 ? Math.Clamp((density - minDens) / densRng, 0, 1) : 0.5);
    }

    /// <summary>
    /// Which real nozzle (M1-M4) a material zone is, for the "M{n}" tag at its boundary — never
    /// "S{n}", which is reserved for taper shape (see the mandatory principle above
    /// <see cref="GetTaperShapeBoundaries"/>). A live zone design's zones are the authored ground
    /// truth for "what material is physically here"; a loaded/baked C snapshot has no zones any more
    /// (baking only carries density), so it falls back to matching this span's density against the
    /// file's own nozzle definitions (populated to the real baked values on save).
    /// </summary>
    public static (string Label, ScottColor Color) GetMaterialTag(double xAbsCm, double density,
        IReadOnlyList<NozzleZone> zones, IReadOnlyList<NozzleDefinition> nozzles)
    {
        int idx = zones.FirstOrDefault(z => xAbsCm >= z.StartCm && xAbsCm < z.EndCm)?.NozzleIndex ?? -1;
        if (idx < 0)
        {
            double bestDiff = double.MaxValue;
            for (int i = 0; i < nozzles.Count; i++)
            {
                if (nozzles[i].DensityGCm3 <= 0) continue;
                double diff = Math.Abs(nozzles[i].DensityGCm3 - density);
                if (diff < bestDiff) { bestDiff = diff; idx = i; }
            }
        }
        if (idx < 0 || idx >= nozzles.Count)
            return ($"ρ {density:0.00}", DensityColor(0.5));

        var n = nozzles[idx];
        TryParseHexColor(n.ColorHex, out var col);
        return ($"M{idx + 1}", col);
    }

    // ── Low-level primitives ────────────────────────────────────────────────
    // Pure functions with no dependency on segments/zones/nozzles at all — moved here from
    // MainWindow (where they were already `private static`) purely so ChartRenderer can call them
    // too, without a WPF reference. MainWindow keeps calling these same ones (it references
    // Services already), so there remains exactly one implementation of each.

    public static double InterpolateProfileY(List<(double X, double Y)> sorted, double x)
    {
        if (x <= sorted[0].X)  return sorted[0].Y;
        if (x >= sorted[^1].X) return sorted[^1].Y;
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (x >= sorted[i].X && x <= sorted[i + 1].X)
            {
                double t = (x - sorted[i].X) / (sorted[i + 1].X - sorted[i].X);
                return sorted[i].Y + t * (sorted[i + 1].Y - sorted[i].Y);
            }
        }
        return sorted[^1].Y;
    }

    public static bool TryParseHexColor(string hex6, out ScottColor color)
    {
        color = new ScottColor(200, 200, 200);
        if (string.IsNullOrWhiteSpace(hex6)) return false;
        hex6 = hex6.TrimStart('#');
        if (hex6.Length < 6) return false;
        try
        {
            byte r = Convert.ToByte(hex6[0..2], 16);
            byte g = Convert.ToByte(hex6[2..4], 16);
            byte b = Convert.ToByte(hex6[4..6], 16);
            color = new ScottColor(r, g, b);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Maps t ∈ [0,1] to a blue→cyan→green→yellow→red colour ramp.</summary>
    public static ScottColor DensityColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r, g, b;
        if (t < 0.25)      { double s = t / 0.25;       r = 0;         g = s;         b = 1; }
        else if (t < 0.5)  { double s = (t-0.25)/0.25;  r = 0;         g = 1;         b = 1-s; }
        else if (t < 0.75) { double s = (t-0.5)/0.25;   r = s;         g = 1;         b = 0; }
        else               { double s = (t-0.75)/0.25;  r = 1;         g = 1-s;       b = 0; }
        return new ScottColor((byte)(r*255), (byte)(g*255), (byte)(b*255));
    }

    /// <summary>Draws a filled band between top/bottom Y arrays, either flat-tinted or with Lambert cylindrical shading.</summary>
    public static void DrawLineFill(ScottPlot.Plot plot,
                                     double[] xs, double[] topYs, double[] botYs,
                                     ScottColor bodyColor,
                                     bool solid = false)
    {
        if (xs.Length < 2) return;

        ScottPlot.Coordinates[] Band(double fraction)
        {
            var top = xs.Select((x, i) => new ScottPlot.Coordinates(x,  topYs[i] * fraction));
            var bot = xs.Select((x, i) => new ScottPlot.Coordinates(x,  botYs[i] * fraction))
                        .Reverse();
            return top.Concat(bot).ToArray();
        }

        if (solid)
        {
            // ── Lambert cylindrical shading ───────────────────────────────
            // Draw 20 concentric bands, outermost (darkest) first.
            // Each inner band is brighter and, at high alpha, nearly replaces
            // the region covered by the previous darker band.
            // v=1 (edge) → b=0 → darkest; v=0 (centre) → b=1 → brightest.
            const int N = 20;
            const float layerAlpha = 0.91f;

            for (int i = 0; i <= N; i++)
            {
                double frac = 1.0 - (double)i / N;        // 1.0 → ~0
                double v    = frac;
                double b    = Math.Sqrt(1.0 - v * v);     // 0 at edge, 1 at centre

                // Shade factor: 0.25 (very dark edge) → 1.0 (full body at centre)
                float shade = (float)(0.25 + 0.75 * b);
                byte  r     = (byte)Math.Min(255, (int)(bodyColor.Red   * shade));
                byte  g     = (byte)Math.Min(255, (int)(bodyColor.Green * shade));
                byte  bl    = (byte)Math.Min(255, (int)(bodyColor.Blue  * shade));

                // Specular glint in the inner 15% of the radius
                if (b > 0.85)
                {
                    float spec = (float)((b - 0.85) / 0.15) * 0.55f;
                    r  = (byte)Math.Min(255, r  + (int)((255 - r)  * spec));
                    g  = (byte)Math.Min(255, g  + (int)((255 - g)  * spec));
                    bl = (byte)Math.Min(255, bl + (int)((255 - bl) * spec));
                }

                var band = plot.Add.Polygon(Band(frac));
                band.FillColor = new ScottColor(r, g, bl).WithAlpha(layerAlpha);
                band.LineWidth = 0;
                band.LineColor = ScottPlot.Colors.Transparent;
            }
        }
        else
        {
            // ── Lightweight 3-layer blend for scan / imported series ──────
            var body = plot.Add.Polygon(Band(1.0));
            body.FillColor = bodyColor.WithAlpha(0.55f);
            body.LineWidth = 0;
            body.LineColor = ScottPlot.Colors.Transparent;

            var mid = plot.Add.Polygon(Band(0.60));
            mid.FillColor = ScottPlot.Colors.White.WithAlpha(0.14f);
            mid.LineWidth = 0;
            mid.LineColor = ScottPlot.Colors.Transparent;

            var hi = plot.Add.Polygon(Band(0.25));
            hi.FillColor = ScottPlot.Colors.White.WithAlpha(0.22f);
            hi.LineWidth = 0;
            hi.LineColor = ScottPlot.Colors.Transparent;
        }
    }
}
