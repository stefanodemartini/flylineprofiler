using DiametroLineaDesktop.Models;
using ScottColor = ScottPlot.Color;
using static DiametroLineaDesktop.Services.ChartGeometry;

namespace DiametroLineaDesktop.Services;

/// <summary>
/// Plain data a chart needs — a UI-free stand-in for the state MainWindow.RenderPdfChart used to
/// read straight off its own fields. Build one from a <see cref="FlyLineProject"/> (see
/// <see cref="LineDesignBuilder.ToProjectSegments"/> for the segments) and everything downstream is
/// identical to what the GUI itself would produce for the same project.
/// </summary>
public sealed class ChartRenderInput
{
    public List<MeasurementPoint>  ScanPoints  { get; set; } = new();
    /// <summary>The drawn taper, one point per design node — <c>_segmentNodes</c> in the GUI.</summary>
    public List<(double X, double Y)> DesignNodes { get; set; } = new();
    public List<ProjectSegment>    Segments    { get; set; } = new();
    public List<NozzleZone>        NozzleZones { get; set; } = new();
    public List<NozzleDefinition>  Nozzles     { get; set; } = new();
    /// <summary>The GUI's <c>_inCompMode || _zoneDerivedComp</c> — whether to render the per-slice
    /// coloured profile at all (still needs at least one segment with real compensation data).</summary>
    public bool UseCompensatedView { get; set; }
    /// <summary>Manually dragged PDF label positions, keyed by node X — empty for a headless
    /// render, which has no interactive dragging to remember in the first place.</summary>
    public Dictionary<double, (double LX, double LY)> NodeLabelOffsets { get; set; } = new();
}

/// <summary>
/// Renders the same chart image MainWindow embeds in an exported PDF — ported line-for-line from
/// MainWindow.RenderPdfChart so the CLI's headless export and the GUI's produce byte-identical
/// output for the same project. Geometry and colour lookups live in <see cref="ChartGeometry"/>,
/// shared with the on-screen chart too.
/// </summary>
public static class ChartRenderer
{
    public static byte[] RenderPdfChart(ChartRenderInput input)
    {
        var plot = new ScottPlot.Plot();
        plot.FigureBackground.Color = ScottPlot.Colors.White;
        plot.DataBackground.Color   = ScottPlot.Colors.White;
        plot.Axes.Color(new ScottColor(80, 80, 80));

        var pts = input.ScanPoints.OrderBy(p => p.X).ToList();

        // Scan data (if present)
        if (pts.Count > 0)
        {
            double[] xs     = pts.Select(p => p.X).ToArray();
            double[] topYs  = pts.Select(p =>  p.FilteredY / 2.0).ToArray();
            double[] botYs  = pts.Select(p => -p.FilteredY / 2.0).ToArray();
            var scanCol = new ScottColor(255, 220, 0).WithAlpha(0.80f);
            var top = plot.Add.Scatter(xs, topYs); top.Color = scanCol; top.LineWidth = 1; top.MarkerSize = 0;
            var bot = plot.Add.Scatter(xs, botYs); bot.Color = scanCol; bot.LineWidth = 1; bot.MarkerSize = 0;
        }

        var segments = input.Segments;
        var zones    = input.NozzleZones;
        var nozzles  = input.Nozzles;

        // In modalità compensato il PDF mostra il profilo compensato con gradiente densità.
        bool pdfUseComp = input.UseCompensatedView && segments.Any(s => s.HasCompensation);
        var baseNodes   = pdfUseComp ? GetCompNodes(segments) : input.DesignNodes.OrderBy(n => n.X).ToList();

        double pdfLabelYMin = 0, pdfLabelYMax = 0;

        if (baseNodes.Count >= 2)
        {
            var sorted     = baseNodes;
            double[] xs    = sorted.Select(n => n.X).ToArray();
            double[] topYs = sorted.Select(n =>  n.Y / 2.0).ToArray();
            double[] botYs = sorted.Select(n => -n.Y / 2.0).ToArray();
            // "Design line color is always derived from M1" — same rule as MainWindow.DesignColor.
            var dc = nozzles.Count > 0 && TryParseHexColor(nozzles[0].ColorHex, out var m1Col)
                ? m1Col : new ScottColor(220, 50, 50);

            if (pdfUseComp)
            {
                // Materiali reali già baked nei segmenti — mai ri-quantizzati qui, altrimenti il
                // grafico potrebbe mostrare meno materiali di quelli realmente usati nella
                // geometria/tabella dello stesso PDF.
                var compSegs = segments.OrderBy(s => s.StartCm)
                    .Where(s => s.HasCompensation).ToList();
                double[] qDens  = compSegs.SelectMany(s => s.CompSliceDensities)
                    .Where(d => d > 0).Distinct().OrderBy(d => d).ToArray();
                double   minDens = qDens.Length > 0 ? qDens[0] : 0;
                double   maxDens = qDens.Length > 0 ? qDens[^1] : 1;
                double   densRng = Math.Max(maxDens - minDens, 1e-9);
                double NearestQ(double d) => qDens.Length == 0 ? d : qDens.MinBy(c => Math.Abs(c - d));

                foreach (var seg in compSegs)
                {
                    int    ns   = seg.CompSliceXsCm.Length;
                    if (ns == 0) continue;
                    double half = ns > 1 ? (seg.CompSliceXsCm[1] - seg.CompSliceXsCm[0]) / 2.0
                                         : seg.LengthCm / 2.0;
                    for (int i = 0; i < ns; i++)
                    {
                        double xAbs = seg.StartCm + seg.CompSliceXsCm[i];
                        double x0   = xAbs - half, x1 = xAbs + half;
                        double d0   = i > 0    ? (seg.CompSliceDiamsMm[i-1] + seg.CompSliceDiamsMm[i])   / 2.0 : seg.CompSliceDiamsMm[i];
                        double d1   = i < ns-1 ? (seg.CompSliceDiamsMm[i]   + seg.CompSliceDiamsMm[i+1]) / 2.0 : seg.CompSliceDiamsMm[i];
                        // Zona reale: colore scelto dall'utente. Nessuna zona: gradiente densità
                        // quantizzato a max 4 livelli per uso produttivo.
                        double qd = NearestQ(seg.CompSliceDensities[i]);
                        var sliceColor = GetSliceColor(xAbs, qd, minDens, densRng, zones, nozzles);
                        DrawLineFill(plot,
                            new[] { x0, x1 },
                            new[] { d0 / 2.0, d1 / 2.0 },
                            new[] { -d0 / 2.0, -d1 / 2.0 },
                            sliceColor, solid: true);
                    }
                }
            }
            else if (zones.Count == 0)
            {
                DrawLineFill(plot, xs, topYs, botYs, dc, solid: true);
            }
            else
            {
                DrawLineFill(plot, xs, topYs, botYs, dc, solid: true);
                foreach (var zone in zones)
                {
                    if (zone.EndCm <= zone.StartCm) continue;
                    if (zone.NozzleIndex < 0 || zone.NozzleIndex >= nozzles.Count) continue;
                    if (!TryParseHexColor(nozzles[zone.NozzleIndex].ColorHex, out var zoneColor)) continue;
                    var pts2 = new List<(double X, double Y)>
                        { (zone.StartCm, InterpolateProfileY(sorted, zone.StartCm)) };
                    foreach (var n in sorted.Where(n => n.X > zone.StartCm && n.X < zone.EndCm))
                        pts2.Add(n);
                    pts2.Add((zone.EndCm, InterpolateProfileY(sorted, zone.EndCm)));
                    double[] sxs  = pts2.Select(p => p.X).ToArray();
                    double[] stop = pts2.Select(p =>  p.Y / 2.0).ToArray();
                    double[] sbot = pts2.Select(p => -p.Y / 2.0).ToArray();
                    DrawLineFill(plot, sxs, stop, sbot, zoneColor, solid: true);
                }
            }

            var tl = plot.Add.Scatter(xs, topYs); tl.Color = dc; tl.LineWidth = 1.2f; tl.MarkerSize = 0;
            var bl = plot.Add.Scatter(xs, botYs); bl.Color = dc; bl.LineWidth = 1.2f; bl.MarkerSize = 0;

            // Node labels + leaders.  True collision avoidance: each label's
            // bounding box (estimated from its text) is tested against every box
            // already placed; slots are tried below, above, then further out
            // until a free spot is found.
            var leaderColor = new ScottColor(100, 100, 100);
            double xSpan    = sorted[^1].X - sorted[0].X;
            double maxDiam  = sorted.Max(n => n.Y);
            double rowGap   = maxDiam * 0.40;   // uniform row height regardless of local diameter
            // Approximate label box size in data units (chart rendered ~3200×600 px)
            const int pdfLblSize = 17;
            double dataPerPxX = (xSpan * 1.15) / 3000.0;
            double yEstSpan   = maxDiam + 2 * rowGap * (1.6 + 3 * 1.2);
            double dataPerPxY = yEstSpan / 480.0;
            // Comp mode: every manufacturing checkpoint (material AND taper-shape changes), not
            // every fine ~1cm node — a loaded C snapshot has hundreds of those, which would turn
            // this into hundreds of Ø/position callouts.
            var labelNodes = (pdfUseComp && sorted.Count > 1)
                ? GetManufacturingCheckpoints(segments, sorted)
                : sorted;

            var placedBoxes = new List<(double X1, double Y1, double X2, double Y2)>();
            for (int ni = 0; ni < labelNodes.Count; ni++)
            {
                var node         = labelNodes[ni];
                double chartYTop =  node.Y / 2.0;
                double chartYBot = -node.Y / 2.0;
                string text      = $"Ø {node.Y:0.00}  {node.X:0.0} cm";
                double boxW      = text.Length * pdfLblSize * 0.62 * dataPerPxX;
                double boxH      = (pdfLblSize * 1.5 + 8) * dataPerPxY;
                double defaultLX = node.X;
                double defaultLY = chartYBot - rowGap;
                // Try slots: below row0, above row0, below row1, above row1, …
                for (int slot = 0; slot < 8; slot++)
                {
                    bool above = slot % 2 == 1;
                    int  row   = slot / 2;
                    // Above-labels start further out so they clear the S1/S2 segment labels
                    double tryY = above
                        ? chartYTop + rowGap * (1.6 + row * 1.2)
                        : chartYBot - rowGap * (1.0 + row * 1.2);
                    // Anchor is LowerCenter when above, UpperCenter when below
                    double y1 = above ? tryY : tryY - boxH;
                    double y2 = above ? tryY + boxH : tryY;
                    bool collides = placedBoxes.Any(b =>
                        node.X - boxW / 2 < b.X2 && node.X + boxW / 2 > b.X1 &&
                        y1 < b.Y2 && y2 > b.Y1);
                    defaultLY = tryY;
                    if (!collides) break;
                }
                double lx = defaultLX, ly = defaultLY;
                // Manually dragged positions win, but only while they stay clear
                // of labels already placed — stale offsets fall back to auto.
                if (input.NodeLabelOffsets.TryGetValue(node.X, out var saved))
                {
                    double sy1 = saved.LY > 0 ? saved.LY : saved.LY - boxH;
                    double sy2 = saved.LY > 0 ? saved.LY + boxH : saved.LY;
                    bool savedCollides = placedBoxes.Any(b =>
                        saved.LX - boxW / 2 < b.X2 && saved.LX + boxW / 2 > b.X1 &&
                        sy1 < b.Y2 && sy2 > b.Y1);
                    if (!savedCollides) (lx, ly) = saved;
                }
                placedBoxes.Add((lx - boxW / 2, ly > 0 ? ly : ly - boxH,
                                 lx + boxW / 2, ly > 0 ? ly + boxH : ly));
                pdfLabelYMin = Math.Min(pdfLabelYMin, ly > 0 ? ly : ly - boxH);
                pdfLabelYMax = Math.Max(pdfLabelYMax, ly > 0 ? ly + boxH : ly);

                var leader = plot.Add.Scatter(
                    new double[] { node.X, lx },
                    new double[] { (ly > 0 ? chartYTop : chartYBot) * 0.85, ly });
                leader.Color      = leaderColor;
                leader.LineWidth  = 1.0f;
                leader.MarkerSize = 0;

                var lbl = plot.Add.Text($"Ø {node.Y:0.00}  {node.X:0.0} cm", lx, ly);
                lbl.LabelFontSize        = pdfLblSize;
                lbl.LabelBold            = true;
                lbl.LabelFontColor       = new ScottColor(50, 50, 50);
                lbl.LabelAlignment       = ly > 0 ? ScottPlot.Alignment.LowerCenter : ScottPlot.Alignment.UpperCenter;
                lbl.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.95f);
                lbl.LabelBorderColor     = leaderColor;
                lbl.LabelBorderWidth     = 1f;
                lbl.LabelPadding         = 5;
                lbl.OffsetX              = 0;
                lbl.OffsetY              = 0;
            }

            // Vertical divider lines at each node for PDF
            var dividerColor = new ScottColor(80, 80, 80);
            foreach (var node in labelNodes)
            {
                double topY =  node.Y / 2.0;
                double botY = -node.Y / 2.0;
                var div = plot.Add.Scatter(
                    new double[] { node.X, node.X },
                    new double[] { topY, botY });
                div.Color      = dividerColor;
                div.LineWidth  = 1.0f;
                div.MarkerSize = 0;
            }

            // Segment labels S1, S2… for PDF — SEMPRE e SOLO i tapers fisici reali, mai i cambi di
            // materiale (stesso principio mandatorio del grafico a schermo). Per un file C caricato
            // segments ha un "segmento" trivial per ogni ~1cm slice — molti consecutivi condividono
            // lo stesso Name (vengono dallo stesso taper originale) — quindi si raggruppano con
            // GetTaperShapeBoundaries invece di iterarli uno a uno, altrimenti la stessa etichetta
            // "S1" verrebbe disegnata decine di volte, leggermente sfalsata.
            var segLabelColor = new ScottColor(40, 40, 40);
            if (pdfUseComp)
            {
                var taperBounds = GetTaperShapeBoundaries(segments, sorted);
                for (int si = 0; si < taperBounds.Count - 1; si++)
                {
                    double cx      = (taperBounds[si].X + taperBounds[si + 1].X) / 2.0;
                    double topAtCx = InterpolateProfileY(sorted, cx) / 2.0;
                    double gap     = InterpolateProfileY(sorted, cx) * 0.08;
                    var atCx = segments.OrderBy(s => s.StartCm)
                        .FirstOrDefault(s => cx >= s.StartCm && cx <= s.EndCm);
                    string lname = !string.IsNullOrWhiteSpace(atCx?.Name) ? atCx!.Name : $"S{si + 1}";
                    var sl = plot.Add.Text(lname, cx, topAtCx + gap);
                    sl.LabelFontSize        = pdfLblSize;
                    sl.LabelBold            = false;
                    sl.LabelFontColor       = segLabelColor;
                    sl.LabelAlignment       = ScottPlot.Alignment.LowerCenter;
                    sl.LabelBackgroundColor = ScottPlot.Colors.Transparent;
                    sl.LabelBorderWidth     = 0;
                    sl.LabelPadding         = 2;
                    sl.OffsetX              = 0;
                    sl.OffsetY              = 0;
                }

                // Etichette "M{n}" per ogni zona di materiale — mai "S", stesso standard del
                // grafico a schermo: un badge col colore reale dell'ugello, sul bordo del profilo.
                foreach (var span in GetMaterialZoneSpans(segments, sorted))
                {
                    double midX     = (span.StartX + span.EndX) / 2.0;
                    double topAtMid = InterpolateProfileY(sorted, midX) / 2.0;
                    var (matLabel, matColor) = GetMaterialTag(midX, span.Density, zones, nozzles);
                    double luminance = (0.299 * matColor.R + 0.587 * matColor.G + 0.114 * matColor.B) / 255.0;
                    var textColor = luminance > 0.6 ? new ScottColor(20, 20, 20) : ScottPlot.Colors.White;

                    var ml = plot.Add.Text(matLabel, midX, topAtMid);
                    ml.LabelFontSize        = pdfLblSize - 3;
                    ml.LabelBold            = true;
                    ml.LabelFontColor       = textColor;
                    ml.LabelBackgroundColor = matColor;
                    ml.LabelBorderColor     = new ScottColor(30, 30, 30);
                    ml.LabelBorderWidth     = 0.8f;
                    ml.LabelAlignment       = ScottPlot.Alignment.LowerCenter;
                    ml.LabelPadding         = 3;
                    ml.OffsetX = 0; ml.OffsetY = 4;
                }
            }
            else
            {
                for (int si = 0; si < sorted.Count - 1; si++)
                {
                    double cx      = (sorted[si].X + sorted[si + 1].X) / 2.0;
                    double topAtCx = InterpolateProfileY(sorted, cx) / 2.0;
                    double gap     = InterpolateProfileY(sorted, cx) * 0.08;
                    var sl = plot.Add.Text($"S{si + 1}", cx, topAtCx + gap);
                    sl.LabelFontSize        = pdfLblSize;
                    sl.LabelBold            = false;
                    sl.LabelFontColor       = segLabelColor;
                    sl.LabelAlignment       = ScottPlot.Alignment.LowerCenter;
                    sl.LabelBackgroundColor = ScottPlot.Colors.Transparent;
                    sl.LabelBorderWidth     = 0;
                    sl.LabelPadding         = 2;
                    sl.OffsetX              = 0;
                    sl.OffsetY              = 0;
                }
            }
        }

        plot.XLabel("Length (cm)");
        plot.YLabel("Diameter (mm)");
        plot.Axes.Bottom.TickLabelStyle.FontSize = 15;
        plot.Axes.Left.TickLabelStyle.FontSize   = 15;
        plot.Axes.Bottom.Label.FontSize           = 16;
        plot.Axes.Left.Label.FontSize             = 16;
        plot.Axes.AutoScale();
        var yRange = plot.Axes.GetLimits().Rect.Height;
        var lim    = plot.Axes.GetLimits();
        // Make room for the staggered node labels above and below the profile
        // (label anchors tracked while placing them; extra margin for text height)
        double yBot = Math.Min(lim.Bottom, pdfLabelYMin - yRange * 0.10);
        double yTop = Math.Max(lim.Top,    pdfLabelYMax + yRange * 0.10);
        plot.Axes.SetLimitsY(yBot - yRange * 0.05, yTop + yRange * 0.05);

        return plot.GetImage(3200, 600).GetImageBytes(ScottPlot.ImageFormat.Png);
    }
}
