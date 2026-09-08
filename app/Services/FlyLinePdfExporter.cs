using DiametroLineaDesktop.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfColor         = QuestPDF.Infrastructure.Color;
using ScottImageFormat = ScottPlot.ImageFormat;
using ScottPlot;
using System.Collections.Generic;
using System.Linq;

namespace DiametroLineaDesktop.Services;

/// <summary>
/// Generates a production design-sheet PDF that matches the RazorBlade-style
/// technical spec sheet format.  The profile chart is rendered directly from
/// the live ScottPlot plot at high resolution.
/// </summary>
public static class FlyLinePdfExporter
{
    private static PdfColor C(string hex) => PdfColor.FromHex(hex);

    /// <summary>
    /// Renders a horizontal colour swatch with a simple linear gradient (bright left, darker right)
    /// using N thin vertical strips at high resolution.
    /// </summary>
    private static void DrawLambertSwatch(IContainer container, PdfColor baseColor,
                                          float totalWidth, float height, int strips = 10)
    {
        container.Width(totalWidth).Height(height).Column(col =>
        {
            float sh = height / strips;
            for (int i = 0; i < strips; i++)
            {
                double shade = 0.95 - 0.45 * ((double)i / (strips - 1));  // 0.95 top → 0.50 bottom
                byte r  = (byte)Math.Min(255, (int)(baseColor.Red   * shade));
                byte g  = (byte)Math.Min(255, (int)(baseColor.Green * shade));
                byte bl = (byte)Math.Min(255, (int)(baseColor.Blue  * shade));
                col.Item().Width(totalWidth).Height(sh).Background(PdfColor.FromRGB(r, g, bl));
            }
        });
    }

    private static double[] Quantize1D(IEnumerable<double> values, int k = 4)
    {
        var arr = values.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (arr.Length == 0) return Array.Empty<double>();

        // k-means with k fixed at 4 splits data that only has fewer real distinct densities into
        // that many clusters anyway — e.g. a 2-material line would come back as 4 "materials", two
        // of them near-duplicates of each other, printed as phantom cards nothing in the file
        // actually uses. Cap k at the number of genuinely distinct values (grouped within a small
        // tolerance) actually present, so the legend never claims more materials than are real.
        const double distinctTol = 0.005;
        int distinctCount = 1;
        for (int i = 1; i < arr.Length; i++)
            if (arr[i] - arr[i - 1] > distinctTol) distinctCount++;
        k = Math.Min(k, distinctCount);

        if (k == 1) return new[] { arr.Average() };
        double[] c = new double[k];
        for (int i = 0; i < k; i++)
            c[i] = arr[(int)((double)i / (k - 1) * (arr.Length - 1))];
        for (int iter = 0; iter < 50; iter++)
        {
            double[] sums = new double[k]; int[] counts = new int[k];
            foreach (var v in arr)
            {
                int best = 0; double bestD = Math.Abs(v - c[0]);
                for (int j = 1; j < k; j++) { double d = Math.Abs(v - c[j]); if (d < bestD) { bestD = d; best = j; } }
                sums[best] += v; counts[best]++;
            }
            bool changed = false;
            for (int j = 0; j < k; j++)
                if (counts[j] > 0) { double nc = sums[j] / counts[j]; if (Math.Abs(nc - c[j]) > 1e-9) changed = true; c[j] = nc; }
            if (!changed) break;
        }
        return c.OrderBy(v => v).ToArray();
    }

    private static (byte r, byte g, byte b) DensityColorRgb(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r, g, b;
        if      (t < 0.25) { double s = t / 0.25;        r = 0; g = s;   b = 1;   }
        else if (t < 0.50) { double s = (t - 0.25)/0.25; r = 0; g = 1;   b = 1-s; }
        else if (t < 0.75) { double s = (t - 0.50)/0.25; r = s; g = 1;   b = 0;   }
        else               { double s = (t - 0.75)/0.25; r = 1; g = 1-s; b = 0;   }
        return ((byte)(r*255), (byte)(g*255), (byte)(b*255));
    }

    // Palette — clean professional white-background document
    private static readonly PdfColor BgPage      = C("FFFFFF");
    private static readonly PdfColor BgTblHead   = C("EEEFF2");
    private static readonly PdfColor BgTblAlt    = C("F7F8FA");
    private static readonly PdfColor BgTblTotal  = C("E8EBF0");
    private static readonly PdfColor ColText     = C("1A1A2E");
    private static readonly PdfColor ColMuted    = C("888899");
    private static readonly PdfColor ColAccent   = C("0F6B50");   // dark teal
    private static readonly PdfColor ColAccent2  = C("B87D20");   // amber
    private static readonly PdfColor ColBorder   = C("C8CBD4");
    private static readonly PdfColor ColRed      = C("C0392B");
    private static readonly PdfColor ColBlue     = C("1A5276");

    /// <summary>
    /// Centre of mass and radius of gyration of a segment set, both as % of its
    /// total length measured from the front (lowest StartCm).
    /// Mass inside each segment is distributed proportionally to diameter², so
    /// tapers weigh more toward their thick end; per-segment densities are
    /// honoured because each segment's own MassG is what gets distributed.
    /// Returns (-1, -1) when no mass is defined.
    /// </summary>
    public static (double ComPct, double RgPct, double ComCm) ComputeMassCentroid(List<ProjectSegment> segs)
    {
        var withMass = segs.Where(s => s.MassG > 0 && s.EndCm > s.StartCm).ToList();
        if (withMass.Count == 0) return (-1, -1, -1);

        double x0 = withMass.Min(s => s.StartCm);
        double x1 = withMass.Max(s => s.EndCm);
        double len = x1 - x0;
        if (len <= 0) return (-1, -1, -1);

        const int SlicesPerSegment = 100;
        double m = 0, mx = 0, mxx = 0;
        foreach (var seg in withMass)
        {
            double dx = (seg.EndCm - seg.StartCm) / SlicesPerSegment;
            // raw d² weights of each slice, then scale so they sum to the segment's mass
            double wSum = 0;
            var w  = new double[SlicesPerSegment];
            var xc = new double[SlicesPerSegment];
            for (int i = 0; i < SlicesPerSegment; i++)
            {
                double t = (i + 0.5) / SlicesPerSegment;
                double d = seg.StartDiameterMm + t * (seg.EndDiameterMm - seg.StartDiameterMm);
                w[i]  = d * d;
                xc[i] = seg.StartCm + (i + 0.5) * dx;
                wSum += w[i];
            }
            if (wSum <= 0) continue;
            double scale = seg.MassG / wSum;
            for (int i = 0; i < SlicesPerSegment; i++)
            {
                double mi = w[i] * scale;
                m   += mi;
                mx  += mi * xc[i];
                mxx += mi * xc[i] * xc[i];
            }
        }
        if (m <= 0) return (-1, -1, -1);

        double com = mx / m;
        double var = Math.Max(0, mxx / m - com * com);
        double rg  = Math.Sqrt(var);
        return ((com - x0) / len * 100.0, rg / len * 100.0, com);
    }

    /// <summary>Taper character from head CoM%: power ↔ distance spectrum.</summary>
    public static string ClassifyCom(double comPct) => comPct switch
    {
        < 0    => "",
        < 40   => "Front-loaded · power",
        < 47   => "Semi front-loaded",
        <= 53  => "Neutral · all-round",
        <= 60  => "Semi rear-loaded",
        _      => "Rear-loaded · distance",
    };

    /// <summary>
    /// Full plain-language character description for any (CoM%, Rg%) combination.
    /// Composed from a CoM clause (where the mass sits → energy release timing),
    /// an Rg clause (how concentrated → punchy vs smooth), and a combined verdict.
    /// </summary>
    public static string DescribeTaper(double comPct, double rgPct)
    {
        if (comPct < 0) return "";

        // ── CoM: where the punch is ────────────────────────────────────────
        string comTxt = comPct switch
        {
            < 40  => $"CoM {comPct:0.0}% — strongly front-loaded. The mass is concentrated toward the tip, " +
                     "so energy is released early and violently: turnover is guaranteed and forceful, " +
                     "ideal for heavy/bulky flies, sink tips and wind, at the cost of delicacy and distance.",
            < 47  => $"CoM {comPct:0.0}% — semi front-loaded. Mild forward bias: turnover is assured and " +
                     "slightly assertive without slapping down. Forgiving of an imperfect stroke.",
            <= 53 => $"CoM {comPct:0.0}% — neutral. Mass is centred, loops are stable and symmetric. " +
                     "Excellent control and roll-casting; turnover relies on the caster, not the line.",
            <= 60 => $"CoM {comPct:0.0}% — semi rear-loaded. The momentum reserve is held toward the back, " +
                     "so the loop accelerates late in flight: good carry and distance with a still-manageable stroke.",
            _     => $"CoM {comPct:0.0}% — strongly rear-loaded. Energy stays in the moving leg until the very " +
                     "end of the unroll: maximum distance and the softest landings, but it stalls into wind, " +
                     "wants light flies and demands a clean, well-timed stroke.",
        };

        // ── Rg: how punchy vs smooth ───────────────────────────────────────
        string rgTxt = rgPct switch
        {
            < 0    => "",
            < 20   => $"Rg {rgPct:0.0}% — mass packed into a compact lump: the head behaves like a projectile. " +
                      "Abrupt, kicky energy delivery; turnover hits hard and the feel is decidedly punchy.",
            < 25.5 => $"Rg {rgPct:0.0}% — moderately distributed mass: a balance of punch and smoothness, " +
                      "with a defined but not brutal kick at turnover.",
            _      => $"Rg {rgPct:0.0}% — mass spread along most of the head (a uniform line is ~29%): energy " +
                      "flows progressively through the loop. Smooth, stable carry and a gentle, even turnover.",
        };

        // ── Combined verdict ───────────────────────────────────────────────
        string verdict = (comPct, rgPct) switch
        {
            (< 40, < 20)        => "Verdict: Skagit-style — a compact front lump that muscles big flies and tips anywhere. Not a presentation or distance tool.",
            (< 40, _)           => "Verdict: power taper with a softened delivery — drives big flies but with a smoother feel than a pure Skagit.",
            (< 47, < 20)        => "Verdict: compact versatile head — quick-loading and punchy, suited to tight casts and streamers at short-medium range.",
            (< 47, _)           => "Verdict: classic all-rounder with reliable turnover — general-purpose WF character, forgiving and pleasant.",
            (<= 53, < 20)       => "Verdict: centred but concentrated — quick-loading head for compact strokes; punchy yet controllable.",
            (<= 53, _)          => "Verdict: true neutral — long-belly/DT character. Control, mends and roll casts above all.",
            (<= 60, < 20)       => "Verdict: rear lump — shooting-head logic with a hard late kick; long casts with an abrupt finish.",
            (<= 60, _)          => "Verdict: distance taper — Scandi-like progressive carry with late energy release and soft presentation.",
            (_, < 20)           => "Verdict: extreme rear lump — maximum launch for experts; unforgiving timing, brutal late kick.",
            _                   => "Verdict: long-range presentation head — the longest smooth carry, light flies and calm air only.",
        };

        return $"{comTxt}\n\n{rgTxt}\n\n{verdict}";
    }

    public static void Export(
        string outputPath,
        string projectName,
        byte[] chartImageBytes,
        List<ProjectSegment> segments,
        bool isSinking,
        bool isFullLine,
        bool isSalt,
        double tempC,
        string afftaBadge,
        string colorNote = "",
        List<NozzleDefinition>? nozzleDefinitions = null,
        List<NozzleZone>?       nozzleZones       = null,
        string designColorHex = "DC3232",
        string coreType = "",
        string laserMark = "",
        bool showCompensation = false,
        string compensationNote = "")
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // ── Chart image passed in pre-rendered ──────────────────────────────
        byte[] chartBytes = chartImageBytes;

        const double GramsToGrains = 15.4324;
        const double CmToFt        = 1.0 / 30.48;

        // A segment's own compensated slices are the source of truth for its real mass and real
        // sink speed whenever it has them — never a value passed in from outside. A caller-supplied
        // "target speed" field can go stale (a loaded C snapshot has nothing to restore it from —
        // see task 10) or simply not apply (a zone-derived profile has no single shared target), so
        // this document reads every number straight off the segments it's already printing.
        double EffectiveMassG(ProjectSegment s)
        {
            if (!(showCompensation && s.HasCompensation && s.CompSliceDiamsMm.Length > 0))
                return s.MassG;
            // A row whose density doesn't actually vary (every DisplaySegments zone row — one row
            // IS one constant-density material, by construction) is exactly the frustum s.MassG
            // already computes correctly (π/4·ρ·L/3·(r1²+r1r2+r2²)) — summing per-slice cylinders
            // both duplicates that work and gets it slightly wrong (a cylinder-per-slice sum isn't
            // the same integral as a single frustum). Only fall back to slice-summing when density
            // genuinely varies within the row (a live, not-yet-baked compensated segment whose
            // internal slices still span more than one material).
            if (s.CompSliceDensities.All(d => Math.Abs(d - s.CompSliceDensities[0]) < 1e-6))
                return s.MassG;
            double dl = s.LengthCm / Math.Max(1, s.CompSliceXsCm.Length);
            double m = 0;
            for (int si = 0; si < s.CompSliceDiamsMm.Length; si++)
            {
                double dCm = s.CompSliceDiamsMm[si] / 10.0;
                m += Math.PI / 4.0 * dCm * dCm * dl * s.CompSliceDensities[si];
            }
            return m;
        }

        // Pre-compute summary values
        var headSegs     = isFullLine ? segments.Where(s => s.IsHead).ToList() : segments;
        double totalMassG   = segments.Sum(EffectiveMassG);
        double totalMassGr  = totalMassG * GramsToGrains;
        double headMassGr   = headSegs.Sum(EffectiveMassG) * GramsToGrains;
        double totalLenMm   = segments.Count > 0
            ? (segments[^1].EndCm - segments[0].StartCm) * 10.0 : 0;
        double headLenMm    = headSegs.Count > 0
            ? (headSegs[^1].EndCm - headSegs[0].StartCm) * 10.0 : 0;

        string lineType   = isSinking  ? "Sinking"    : "Floating";
        string lineFormat = isFullLine ? "Full Line"  : "Shooting Head";
        string water      = isSalt     ? "Salt water" : "Fresh water";

        // Real per-segment compensated sink speeds — a uniform physics compensation gives every
        // segment the same value; a zone-derived profile gives each its own (see task 10).
        var compSpeedsIns = segments.Where(s => s.HasCompensation)
            .Select(s => s.CompensatedTargetSpeedMs * 39.3701)
            .Where(v => v > 0)
            .ToList();
        bool   uniformCompSpeed = compSpeedsIns.Count > 0 && (compSpeedsIns.Max() - compSpeedsIns.Min()) < 0.001;
        string targetSinkText   = compSpeedsIns.Count == 0 ? "—"
            : uniformCompSpeed  ? $"{compSpeedsIns[0]:0.000} in/s"
                                 : $"{compSpeedsIns.Min():0.000}–{compSpeedsIns.Max():0.000} in/s (per zone)";

        // Density range — use compensated slice densities when exporting a compensated profile
        string densityRange;
        bool hasClampedSection = false;
        if (showCompensation)
        {
            var compSegsAll = segments.Where(s => s.HasCompensation).ToList();
            var allDens = compSegsAll.SelectMany(s => s.CompSliceDensities).Where(d => d > 0).ToList();
            densityRange = allDens.Count > 0
                ? $"{allDens.Min():0.00} – {allDens.Max():0.00} g/cm³"
                : "—";
            hasClampedSection = compSegsAll.Any(s => s.HasClampedSlices);
        }
        else
        {
            var headDensities = headSegs.Where(s => s.SpecWeightGCm3 > 0)
                                        .Select(s => s.SpecWeightGCm3).ToList();
            densityRange = headDensities.Count > 0
                ? $"{headDensities.Min():0.00} – {headDensities.Max():0.00} g/cm³"
                : "—";
        }

        // Notes block
        string dimNote;
        string weightNote;
        string changeNote;
        if (showCompensation)
        {
            dimNote    = "All dimensions are in millimeters. Diameters shown are compensated values.";
            weightNote = uniformCompSpeed
                ? $"Compensated profile — uniform sink speed {compSpeedsIns[0]:0.00} in/s. " +
                  $"Density varies along the line ({densityRange}) — each zone must be produced at the exact specified density."
                : compSpeedsIns.Count > 0
                    ? $"Multi-material profile — each zone sinks at its own speed (see table). " +
                      $"Density varies along the line ({densityRange}) — each zone must be produced at the exact specified density."
                    : $"Compensated profile. Density varies along the line ({densityRange}).";
            changeNote = "Do not alter diameters. Each section must be manufactured at the exact density shown — the density is calculated and fixed.";
        }
        else
        {
            dimNote    = "All dimensions are in millimeters, unless otherwise indicated.";
            weightNote = headMassGr > 0
                ? $"With diameters indicated and target weight the density of the line is {densityRange}."
                : $"Line density: {densityRange}.";
            changeNote = "Do not change length and diameters of segments.";
        }

        // ── Logo — load from embedded assembly resource (always available) ──
        byte[]? logoBytes = null;
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string resName = asm.GetManifestResourceNames()
                            .FirstOrDefault(n => n.EndsWith("RazorBladeFlyLines.jpg",
                                                             StringComparison.OrdinalIgnoreCase))
                         ?? string.Empty;
        if (!string.IsNullOrEmpty(resName))
        {
            using var stream = asm.GetManifestResourceStream(resName)!;
            using var ms     = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            logoBytes = ms.ToArray();
        }

        // ── Build PDF ───────────────────────────────────────────────────────
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(14);
                page.PageColor(BgPage);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontColor(ColText).FontSize(7.5f));

                page.Content().Column(col =>
                {
                    col.Spacing(4);

                    // ── Confidentiality header ─────────────────────────────
                    col.Item().Text(
                        "This document and its contents are the property of the designer. " +
                        "All dimensions are confidential and must not be disclosed without written consent.")
                        .FontSize(6.5f).FontColor(ColMuted).Italic();

                    col.Item().LineHorizontal(0.5f).LineColor(ColBorder);

                    // ── Logo + main heading ────────────────────────────────
                    col.Item().Row(row =>
                    {
                        // Left: RazorBlade logo
                        row.ConstantItem(110).AlignMiddle().Column(c =>
                        {
                            if (logoBytes != null)
                                c.Item().PaddingBottom(8).Image(logoBytes).FitWidth();
                            else
                                c.Item().Text("FlyLine Profiler")
                                    .FontSize(7).FontColor(ColMuted).Italic();
                        });

                        // Centre: project title + profile mode
                        row.RelativeItem().AlignMiddle().AlignCenter().Column(tc =>
                        {
                            tc.Item().Text(projectName)
                                .FontSize(16).Bold().FontColor(ColText).AlignCenter();
                            tc.Item().Text(showCompensation ? "C  —  Compensated profile" : "NC  —  Original profile")
                                .FontSize(7.5f).FontColor(ColMuted).Italic().AlignCenter();
                        });

                        // Right: line specs only (AFFTA already in spec row below)
                        row.ConstantItem(200).AlignRight().Column(c =>
                        {
                            c.Item().Text($"{lineType}  ·  {lineFormat}")
                                .FontSize(9).Bold().FontColor(ColAccent).AlignRight();
                            c.Item().Text($"{water}  ·  {tempC:0}°C")
                                .FontSize(7.5f).FontColor(ColMuted).AlignRight();
                        });
                    });

                    // ── Notes block ────────────────────────────────────────
                    col.Item().Background(C("F3F4F7"))
                        .Border(0.5f).BorderColor(ColBorder)
                        .Padding(4).Row(nr =>
                    {
                        nr.RelativeItem().Text(weightNote).FontSize(7).FontColor(ColText);
                        nr.ConstantItem(8);
                        nr.RelativeItem().Text($"{changeNote}  {dimNote}").FontSize(6.5f).FontColor(ColMuted).Italic();
                    });

                    // ── Spec line ──────────────────────────────────────────
                    col.Item().Row(row =>
                    {
                        void SpecBlock(string label, string value, PdfColor valColor)
                        {
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text(label).FontSize(6f).FontColor(ColMuted);
                                c.Item().PaddingRight(3).Text(value).FontSize(7.5f).Bold().FontColor(valColor);
                            });
                        }
                        SpecBlock("Type",          lineType,      ColText);
                        SpecBlock("Format",        lineFormat,    ColText);
                        SpecBlock("Density",       densityRange,  ColAccent);
                        SpecBlock("Total length",  $"{totalLenMm / 10.0:0} cm ({totalLenMm / 304.8:0.0} ft)", ColText);
                        SpecBlock("Head length",   headLenMm > 0 ? $"{headLenMm / 10.0:0} cm ({headLenMm / 304.8:0.0} ft)" : "—", ColText);
                        if (!showCompensation)
                        {
                            SpecBlock("Head weight",  headMassGr > 0 ? $"{headMassGr:0.0} gr" : "—", ColAccent2);
                            SpecBlock("Total weight", totalMassGr > 0 ? $"{totalMassGr:0.0} gr" : "—", ColText);
                            var (comPct, rgPct, _) = ComputeMassCentroid(headSegs);
                            SpecBlock("CoM (head)", comPct >= 0 ? $"{comPct:0.0}% · Rg {rgPct:0.0}%" : "—", ColAccent);
                            SpecBlock("Character",  ClassifyCom(comPct), ColText);
                            string afftaValue = afftaBadge.StartsWith("AFFTA", StringComparison.OrdinalIgnoreCase)
                                ? afftaBadge.Substring(5).TrimStart()
                                : afftaBadge;
                            SpecBlock("AFFTA", afftaValue, ColAccent2);
                        }
                        else
                        {
                            SpecBlock("Target sink",  targetSinkText, ColAccent2);
                            SpecBlock("Water",        water,  ColText);
                            SpecBlock("Type",         uniformCompSpeed ? "Compensated — uniform sink" : "Multi-material — per-zone sink",  ColAccent);
                            if (hasClampedSection)
                                SpecBlock("Warning", "⚠ sections at ρ min (0.94)", ColRed);
                        }
                    });

                    col.Item().LineHorizontal(0.5f).LineColor(ColBorder);

                    // ── Profile chart ──────────────────────────────────────
                    col.Item().Image(chartBytes).FitWidth();

                    // ── Materials legend (NC: nozzles with zones; C: comp materials) ──
                    bool hasNozzles = nozzleDefinitions != null && nozzleDefinitions.Count > 0;
                    bool hasZones   = nozzleZones       != null && nozzleZones.Count   > 0;

                    if (!showCompensation)
                    {
                        // NC PDF — build entries from zones: one card per nozzle actually used.
                        // Entry = (original nozzle index, definition, zone-range text).
                        // If no zones defined, fall back to a single design-colour card.
                        var entries = new List<(int Idx, NozzleDefinition Def, string ZoneRange)>();

                        if (hasZones)
                        {
                            // M1 is the implicit base material everywhere no zone covers — it never
                            // appears as a NozzleIndex in nozzleZones itself, so it must be added
                            // unconditionally or the line's own "default" material goes unlisted.
                            var usedIndices = nozzleZones!
                                .Select(z => z.NozzleIndex).Append(0)
                                .Distinct().OrderBy(x => x).ToList();
                            foreach (int idx in usedIndices)
                            {
                                var def = (hasNozzles && idx < nozzleDefinitions!.Count)
                                    ? nozzleDefinitions![idx]
                                    : new NozzleDefinition { ColorHex = designColorHex.TrimStart('#'), DensityGCm3 = 0, Label = "" };
                                var zones = nozzleZones!.Where(z => z.NozzleIndex == idx)
                                    .OrderBy(z => z.StartCm).ToList();
                                string zoneRange = string.Join("  |  ",
                                    zones.Select(z => $"{z.StartCm:0.0} – {z.EndCm:0.0} cm"));
                                entries.Add((idx, def, zoneRange));
                            }
                        }

                        if (entries.Count == 0)
                        {
                            // No zones: single material — use design colour + shared density
                            var singleDef = hasNozzles
                                ? nozzleDefinitions![0]
                                : new NozzleDefinition { ColorHex = designColorHex.TrimStart('#'), DensityGCm3 = 0, Label = "" };
                            entries.Add((0, singleDef, "full line"));
                        }

                        col.Item().Background(C("F7F8FA"))
                            .Border(0.5f).BorderColor(ColBorder)
                            .PaddingVertical(3).PaddingHorizontal(5).Column(legCol =>
                        {
                            legCol.Item().AlignCenter()
                                .Text("Materials").FontSize(6.5f).Bold().FontColor(ColMuted);
                            legCol.Item().PaddingTop(3).Row(legRow =>
                            {
                                foreach (var (origIdx, nd, zoneRange) in entries)
                                {
                                    string hex = (nd.ColorHex ?? "DC3232").TrimStart('#');
                                    if (hex.Length < 6) continue;
                                    try
                                    {
                                        byte nr = System.Convert.ToByte(hex[0..2], 16);
                                        byte ng = System.Convert.ToByte(hex[2..4], 16);
                                        byte nb = System.Convert.ToByte(hex[4..6], 16);
                                        var swatchColor = PdfColor.FromRGB(nr, ng, nb);

                                        legRow.AutoItem().Column(sCol =>
                                        {
                                            sCol.Item().Text($"M{origIdx + 1}")
                                                .FontSize(6.5f).Bold().FontColor(ColText).AlignCenter();
                                            sCol.Item().Border(0.5f).BorderColor(ColBorder)
                                                .Element(e => DrawLambertSwatch(e, swatchColor, 44, 10));
                                            sCol.Item().Width(44).Text($"#{hex.ToUpper()}")
                                                .FontSize(5.5f).FontColor(ColMuted).AlignCenter();
                                            if (nd.DensityGCm3 > 0)
                                                sCol.Item().Width(44).Text($"ρ {nd.DensityGCm3:0.00} g/cm³")
                                                    .FontSize(6f).Bold().FontColor(ColText).AlignCenter();
                                            if (!string.IsNullOrWhiteSpace(nd.Label) && !nd.Label.StartsWith("ρ ") && nd.Label != "N/A")
                                                sCol.Item().Width(44).Text(nd.Label)
                                                    .FontSize(6f).FontColor(ColMuted).AlignCenter();
                                            if (!string.IsNullOrWhiteSpace(zoneRange))
                                                sCol.Item().Width(44).Text(zoneRange)
                                                    .FontSize(5.5f).FontColor(ColMuted).AlignCenter();
                                        });
                                        legRow.ConstantItem(10);
                                    }
                                    catch { /* ignore bad hex */ }
                                }

                                // Core material card — neutral gray, no colour swatch
                                if (!string.IsNullOrWhiteSpace(coreType))
                                {
                                    legRow.AutoItem()
                                        .Border(0.5f).BorderColor(ColBorder)
                                        .Background(C("E4E5EA"))
                                        .PaddingVertical(2).PaddingHorizontal(5).Column(sCol =>
                                    {
                                        sCol.Item().Text("Core")
                                            .FontSize(6f).Bold().FontColor(ColMuted).AlignCenter();
                                        sCol.Item().PaddingTop(2).Text(coreType)
                                            .FontSize(6.5f).Bold().FontColor(ColText).AlignCenter();
                                    });
                                }
                            });
                        });
                    }
                    else if (hasNozzles || !string.IsNullOrWhiteSpace(designColorHex))
                    {
                        // C PDF — show all comp-derived nozzle definitions
                        col.Item().Background(C("F7F8FA"))
                            .Border(0.5f).BorderColor(ColBorder)
                            .PaddingVertical(3).PaddingHorizontal(5).Column(legCol =>
                        {
                            legCol.Item().AlignCenter()
                                .Text("Materials").FontSize(6.5f).Bold().FontColor(ColMuted);
                            legCol.Item().PaddingTop(3).Row(legRow =>
                            {
                                // Only cards for nozzles actually in use — an empty M3/M4 slot
                                // (density 0, never baked into any slice) is not part of this file
                                // and must not be printed as if it were a real material.
                                var nozzlesToShow = hasNozzles
                                    ? nozzleDefinitions!
                                        .Select((nd, i) => (Idx: i, Def: nd))
                                        .Where(x => x.Def.DensityGCm3 > 0)
                                        .ToList()
                                    : new List<(int Idx, NozzleDefinition Def)>
                                        { (0, new NozzleDefinition { ColorHex = designColorHex.TrimStart('#'), DensityGCm3 = 0, Label = "" }) };

                                for (int nmi = 0; nmi < nozzlesToShow.Count; nmi++)
                                {
                                    var (origIdx, nd) = nozzlesToShow[nmi];
                                    string hex = (nd.ColorHex ?? "DC3232").TrimStart('#');
                                    if (hex.Length < 6) continue;
                                    try
                                    {
                                        byte nr = System.Convert.ToByte(hex[0..2], 16);
                                        byte ng = System.Convert.ToByte(hex[2..4], 16);
                                        byte nb = System.Convert.ToByte(hex[4..6], 16);
                                        var swatchColor = PdfColor.FromRGB(nr, ng, nb);

                                        legRow.AutoItem().Column(sCol =>
                                        {
                                            sCol.Item().Text($"M{origIdx + 1}")
                                                .FontSize(6.5f).Bold().FontColor(ColText).AlignCenter();
                                            sCol.Item().Border(0.5f).BorderColor(ColBorder)
                                                .Element(e => DrawLambertSwatch(e, swatchColor, 44, 10));
                                            sCol.Item().Width(44).Text($"#{hex.ToUpper()}")
                                                .FontSize(5.5f).FontColor(ColMuted).AlignCenter();
                                            if (nd.DensityGCm3 > 0)
                                                sCol.Item().Width(44).Text($"ρ {nd.DensityGCm3:0.00} g/cm³")
                                                    .FontSize(6f).Bold().FontColor(ColText).AlignCenter();
                                            if (!string.IsNullOrWhiteSpace(nd.Label) && !nd.Label.StartsWith("ρ ") && nd.Label != "N/A")
                                                sCol.Item().Width(44).Text(nd.Label)
                                                    .FontSize(6f).FontColor(ColMuted).AlignCenter();
                                        });
                                        legRow.ConstantItem(10);
                                    }
                                    catch { /* ignore bad hex */ }
                                }

                                // Core material card — neutral gray, no colour swatch
                                if (!string.IsNullOrWhiteSpace(coreType))
                                {
                                    legRow.AutoItem()
                                        .Border(0.5f).BorderColor(ColBorder)
                                        .Background(C("E4E5EA"))
                                        .PaddingVertical(2).PaddingHorizontal(5).Column(sCol =>
                                    {
                                        sCol.Item().Text("Core")
                                            .FontSize(6f).Bold().FontColor(ColMuted).AlignCenter();
                                        sCol.Item().PaddingTop(2).Text(coreType)
                                            .FontSize(6.5f).Bold().FontColor(ColText).AlignCenter();
                                    });
                                }
                            });
                        });
                    }

                    // ── Compensation note + density legend ────────────────
                    if (showCompensation && !string.IsNullOrWhiteSpace(compensationNote))
                    {
                        col.Item().Background(C("EDF7F2"))
                            .Border(0.5f).BorderColor(C("5BAD8A"))
                            .Padding(4).Text(t =>
                            {
                                t.Span("Compensated profile:  ").FontSize(7.5f).Bold().FontColor(C("0F6B50"));
                                t.Span(compensationNote).FontSize(7.5f).FontColor(ColText);
                            });
                    }

                    if (showCompensation)
                    {
                        var compSegs = segments.Where(s => s.HasCompensation).ToList();
                        if (compSegs.Count > 0)
                        {
                            // Quantize all slice densities to max 4 materials
                            double[] qDens = Quantize1D(compSegs.SelectMany(s => s.CompSliceDensities), 4);
                            double minD = qDens.Length > 0 ? qDens[0] : 0;
                            double maxD = qDens.Length > 0 ? qDens[^1] : 1;
                            double rng  = Math.Max(maxD - minD, 1e-9);

                            double NearestQ(double d) => qDens.Length == 0 ? d
                                : qDens.MinBy(c => Math.Abs(c - d));
                            int MatIdx(double d) => Array.IndexOf(qDens, NearestQ(d)) + 1;

                            // The real nozzle (M1-M4) nearest a given density, and its real colour —
                            // never the synthetic blue-to-red DensityColorRgb heat scale, which paints
                            // "Mat 1"/"Mat 2" in colours that don't exist anywhere on this line and
                            // contradict the real swatches the Materials card above already shows.
                            (string Label, byte R, byte G, byte B) RealMaterial(double dens)
                            {
                                if (hasNozzles)
                                {
                                    int bestIdx = -1; double bestDiff = double.MaxValue;
                                    for (int ni = 0; ni < nozzleDefinitions!.Count; ni++)
                                    {
                                        if (nozzleDefinitions[ni].DensityGCm3 <= 0) continue;
                                        double diff = Math.Abs(nozzleDefinitions[ni].DensityGCm3 - dens);
                                        if (diff < bestDiff) { bestDiff = diff; bestIdx = ni; }
                                    }
                                    if (bestIdx >= 0)
                                    {
                                        string hx = (nozzleDefinitions[bestIdx].ColorHex ?? "DC3232").TrimStart('#');
                                        if (hx.Length >= 6)
                                        {
                                            byte rr = System.Convert.ToByte(hx[0..2], 16);
                                            byte gg = System.Convert.ToByte(hx[2..4], 16);
                                            byte bb = System.Convert.ToByte(hx[4..6], 16);
                                            return ($"M{bestIdx + 1}", rr, gg, bb);
                                        }
                                    }
                                }
                                double t = Math.Clamp((dens - minD) / rng, 0, 1);
                                var (sr, sg, sb) = DensityColorRgb(t);
                                return ("Mat", sr, sg, sb);
                            }

                            // ── Density bands: contiguous position runs of same material ──
                            // Build ordered list of (positionMm, matIdx, quantizedDensity) per slice.
                            // PosMm is each slice's own CENTER (CompSliceXsCm stores slice-center
                            // offsets), not its edge — used directly as a band's start/end, it put
                            // every band a half-slice short at the true line ends and, at a material
                            // transition, printed each side's boundary from its own slice center
                            // instead of the shared point between them (a 0-3100mm / 3100-18500mm
                            // real split coming out as 5-3105 / 3105-18505 in the exported PDF).
                            var allSlices = compSegs
                                .SelectMany(seg => seg.CompSliceXsCm
                                    .Select((x, i) => (
                                        PosMm: (seg.StartCm + x) * 10.0,
                                        Mat:   MatIdx(seg.CompSliceDensities[i]),
                                        Dens:  NearestQ(seg.CompSliceDensities[i])
                                    )))
                                .OrderBy(s => s.PosMm)
                                .ToList();

                            // Group into contiguous runs of same material — anchored to the real
                            // line start/end, and to the true midpoint between two slice centers at
                            // every internal transition (same approach as GetMaterialZoneSpans on
                            // the on-screen chart, kept in sync so the two never disagree).
                            var bands = new List<(double StartMm, double EndMm, int Mat, double Dens)>();
                            if (allSlices.Count > 0)
                            {
                                double lineStartMm = compSegs.Min(s => s.StartCm) * 10.0;
                                double lineEndMm   = compSegs.Max(s => s.EndCm)   * 10.0;
                                double spanStart = lineStartMm;
                                for (int si = 0; si < allSlices.Count; si++)
                                {
                                    bool last = si == allSlices.Count - 1;
                                    if (!last && allSlices[si + 1].Mat == allSlices[si].Mat) continue;
                                    double x = last ? lineEndMm : (allSlices[si].PosMm + allSlices[si + 1].PosMm) / 2.0;
                                    bands.Add((spanStart, x, allSlices[si].Mat, allSlices[si].Dens));
                                    spanStart = x;
                                }
                            }

                            col.Item().Background(C("F7F8FA"))
                                .Border(0.5f).BorderColor(ColBorder)
                                .PaddingVertical(3).PaddingHorizontal(5).Row(dr =>
                            {
                                dr.AutoItem().AlignMiddle()
                                    .Text("Density zones  ").FontSize(6.5f).Bold().FontColor(ColMuted);
                                foreach (var (startMm, endMm, mat, dens) in bands)
                                {
                                    var (matLabel, sr2, sg2, sb2) = RealMaterial(dens);
                                    var sw2 = PdfColor.FromRGB(sr2, sg2, sb2);
                                    dr.AutoItem().Column(sc =>
                                    {
                                        sc.Item().Border(0.5f).BorderColor(ColBorder)
                                            .Element(e => DrawLambertSwatch(e, sw2, 56, 10));
                                        sc.Item().Width(56)
                                            .Text(matLabel)
                                            .FontSize(6.5f).Bold().FontColor(C("0F6B50")).AlignCenter();
                                        sc.Item().Width(56)
                                            .Text($"{dens:0.00} g/cm³")
                                            .FontSize(6.5f).FontColor(ColText).AlignCenter();
                                        sc.Item().Width(56)
                                            .Text($"{startMm:0}–{endMm:0} mm")
                                            .FontSize(6f).FontColor(ColMuted).AlignCenter();
                                    });
                                    dr.ConstantItem(6);
                                }
                            });
                        }
                    }

                    // ── Colour note / laser mark (if defined) ──────────────
                    if (!string.IsNullOrWhiteSpace(colorNote) || !string.IsNullOrWhiteSpace(laserMark))
                    {
                        col.Item().Row(noteRow =>
                        {
                            if (!string.IsNullOrWhiteSpace(colorNote))
                            {
                                noteRow.RelativeItem().Background(C("FFF8E8"))
                                    .Border(0.5f).BorderColor(C("E8C060"))
                                    .Padding(5).Text(t =>
                                    {
                                        t.Span("Color:  ").FontSize(7.5f).FontColor(ColMuted).Bold();
                                        t.Span(colorNote).FontSize(8.5f).Bold().FontColor(C("B87D20"));
                                    });
                                if (!string.IsNullOrWhiteSpace(laserMark))
                                    noteRow.ConstantItem(6);
                            }
                            if (!string.IsNullOrWhiteSpace(laserMark))
                            {
                                noteRow.RelativeItem().Background(C("EDF4FB"))
                                    .Border(0.5f).BorderColor(C("9DBEDC"))
                                    .Padding(5).Text(t =>
                                    {
                                        t.Span("Laser mark:  ").FontSize(7.5f).FontColor(ColMuted).Bold();
                                        t.Span(laserMark).FontSize(8.5f).Bold().FontColor(C("2A5E8C"));
                                    });
                            }
                        });
                    }

                    col.Item().LineHorizontal(0.5f).LineColor(ColBorder);

                    // ── Segment data table ─────────────────────────────────
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(18);  // #
                            c.ConstantColumn(46);  // Name
                            c.RelativeColumn();    // Start mm
                            c.RelativeColumn();    // End mm
                            c.RelativeColumn();    // Length mm
                            c.RelativeColumn();    // Ø1 mm
                            c.RelativeColumn();    // Ø2 mm
                            c.RelativeColumn();    // Taper mm/m
                            c.RelativeColumn();    // Density
                            c.RelativeColumn();    // Mass g
                            c.RelativeColumn();    // Mass gr
                            c.RelativeColumn();    // gr/ft
                            c.RelativeColumn();    // Sink
                            c.ConstantColumn(30);  // Type
                        });

                        // Header
                        static IContainer Hdr(IContainer c) =>
                            c.Background(PdfColor.FromHex("EEEFF2"))
                             .BorderBottom(0.8f).BorderColor(PdfColor.FromHex("C8CBD4"))
                             .PaddingVertical(2).PaddingHorizontal(2);

                        var hdrs = showCompensation
                            ? new[]
                            {
                                "#", "Name", "Start\nmm", "End\nmm", "Len\nmm",
                                "Ø start\nmm", "Ø end\nmm", "Taper\nmm/m",
                                "ρ avg\ng/cm³", "Mass\ng", "Mass\ngr",
                                "gr/ft", "Sink\nin/s", "Note"
                            }
                            : new[]
                            {
                                "#", "Name", "Start\nmm", "End\nmm", "Len\nmm",
                                "Ø1\nmm", "Ø2\nmm", "Taper\nmm/m",
                                "Density\ng/cm³", "Mass\ng", "Mass\ngr",
                                "gr/ft", "Sink\nin/s", "Type"
                            };

                        table.Header(hdr =>
                        {
                            foreach (var h in hdrs)
                                hdr.Cell().Element(Hdr)
                                   .Text(h).FontSize(6.5f).Bold().FontColor(ColAccent);
                        });

                        for (int i = 0; i < segments.Count; i++)
                        {
                            var seg   = segments[i];
                            bool isHd = !isFullLine || seg.IsHead;
                            var  bg   = i % 2 == 0 ? BgPage : BgTblAlt;

                            void Cell(string text, PdfColor? fc = null, bool bold = false)
                            {
                                var cell = table.Cell().Background(bg)
                                    .BorderBottom(0.3f).BorderColor(ColBorder)
                                    .PaddingVertical(0).PaddingHorizontal(2);
                                var t = cell.Text(text).FontSize(7);
                                if (bold) t.Bold();
                                if (fc.HasValue) t.FontColor(fc.Value);
                            }

                            if (showCompensation && seg.HasCompensation)
                            {
                                // ── Compensated row ────────────────────────
                                double d1 = seg.CompSliceDiamsMm.Length > 0 ? seg.CompSliceDiamsMm[0]  : seg.StartDiameterMm;
                                double d2 = seg.CompSliceDiamsMm.Length > 0 ? seg.CompSliceDiamsMm[^1] : seg.EndDiameterMm;
                                double avgRho = seg.CompSliceDensities.Length > 0 ? seg.CompSliceDensities.Average() : 0;
                                double compMassG = EffectiveMassG(seg);
                                double compTaper = Math.Abs(d2 - d1) < 0.001 ? 0 : (d2 - d1) / (seg.LengthCm / 100.0);
                                double grPerFt  = compMassG > 0 && seg.LengthCm > 0
                                    ? (compMassG * GramsToGrains) / (seg.LengthCm * CmToFt) : 0;
                                bool clamped = seg.HasClampedSlices;

                                Cell(seg.Index.ToString());
                                Cell(seg.Name, isHd ? ColAccent : ColBlue, true);
                                Cell($"{seg.StartCm  * 10:0}");
                                Cell($"{seg.EndCm    * 10:0}");
                                Cell($"{seg.LengthCm * 10:0}");
                                Cell($"{d1:0.00}");
                                Cell($"{d2:0.00}");
                                Cell(Math.Abs(compTaper) < 0.001 ? "—" : $"{compTaper:+0.00;-0.00}");
                                Cell(avgRho > 0 ? $"{avgRho:0.00}" : "—", clamped ? (PdfColor?)ColRed : null);
                                Cell(compMassG > 0 ? $"{compMassG:0.000}" : "—");
                                Cell(compMassG > 0 ? $"{compMassG * GramsToGrains:0.0}" : "—");
                                Cell(grPerFt > 0 ? $"{grPerFt:0.0}" : "—");
                                Cell(seg.CompSpeedText);
                                Cell(clamped ? "⚠ ρ min" : "OK", clamped ? ColRed : ColAccent);
                            }
                            else
                            {
                                // ── NC row (fallback or no comp data) ─────────
                                double grPerFt = seg.MassG > 0 && seg.LengthCm > 0
                                    ? (seg.MassG * GramsToGrains) / (seg.LengthCm * CmToFt) : 0;

                                Cell(seg.Index.ToString());
                                Cell(seg.Name, isHd ? ColAccent : ColBlue, true);
                                Cell($"{seg.StartCm  * 10:0}");
                                Cell($"{seg.EndCm    * 10:0}");
                                Cell($"{seg.LengthCm * 10:0}");
                                Cell($"{seg.StartDiameterMm:0.00}");
                                Cell($"{seg.EndDiameterMm:0.00}");
                                Cell(seg.IsCylinder ? "—" : $"{seg.TaperMmPerMeter:+0.00;-0.00}");
                                Cell(seg.EffectiveSpecWeightGCm3 > 0 ? $"{seg.EffectiveSpecWeightGCm3:0.00}" : "—");
                                Cell(seg.MassG > 0 ? $"{seg.MassG:0.000}" : "—");
                                Cell(seg.MassG > 0 ? $"{seg.MassG * GramsToGrains:0.0}" : "—");
                                Cell(grPerFt > 0 ? $"{grPerFt:0.0}" : "—");
                                Cell(seg.SinkSpeedText);
                                Cell(isHd ? "HEAD" : "RUN", isHd ? ColAccent : ColMuted);
                            }
                        }

                        // Totals row
                        void TotCell(string text, bool hi = false)
                        {
                            table.Cell()
                                .Background(BgTblTotal)
                                .BorderTop(0.8f).BorderColor(ColAccent)
                                .PaddingVertical(2).PaddingHorizontal(3)
                                .Text(text).FontSize(7).Bold()
                                .FontColor(hi ? ColAccent2 : ColText);
                        }

                        double sumMassG  = totalMassG; // already the compensated sum when showCompensation is true
                        double sumMassGr = sumMassG * GramsToGrains;
                        TotCell("∑"); TotCell("TOTAL");
                        TotCell(""); TotCell(""); TotCell("");
                        TotCell(""); TotCell(""); TotCell("");
                        TotCell(densityRange);
                        TotCell($"{sumMassG:0.000}", true);
                        TotCell($"{sumMassGr:0.0}", true);
                        TotCell(""); TotCell(""); TotCell("");
                    });

                });

                // ── Footer — pinned to page bottom, never in content flow ──
                page.Footer().Column(fc =>
                {
                    fc.Item().LineHorizontal(0.5f).LineColor(ColBorder);
                    fc.Item().PaddingTop(3).Row(row =>
                    {
                        row.RelativeItem().Text(t =>
                        {
                            t.Span("Head: ").FontColor(ColMuted);
                            t.Span($"{headMassGr:0.0} gr").Bold().FontColor(ColAccent2);
                            t.Span("   Total: ").FontColor(ColMuted);
                            t.Span($"{totalMassGr:0.0} gr").Bold().FontColor(ColText);
                            t.Span($"   {afftaBadge}").FontColor(ColAccent);
                        });
                        row.ConstantItem(260).AlignRight().Text(t =>
                        {
                            t.Span("Confidential  ·  ").FontColor(ColRed).Bold().FontSize(7);
                            t.Span("Generated by FlyLine Profiler  ·  RazorBlade Fly Lines").FontColor(ColMuted).FontSize(6.5f);
                        });
                    });
                });
            });
        }).GeneratePdf(outputPath);
    }
}
