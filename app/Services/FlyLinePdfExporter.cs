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
        List<LineColorSection>? colorSections = null,
        string designColorHex = "DC3232")
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // ── Chart image passed in pre-rendered ──────────────────────────────
        byte[] chartBytes = chartImageBytes;

        const double GramsToGrains = 15.4324;
        const double CmToFt        = 1.0 / 30.48;

        // Pre-compute summary values
        var headSegs     = isFullLine ? segments.Where(s => s.IsHead).ToList() : segments;
        double totalMassG   = segments.Sum(s => s.MassG);
        double totalMassGr  = totalMassG * GramsToGrains;
        double headMassGr   = headSegs.Sum(s => s.MassG) * GramsToGrains;
        double totalLenMm   = segments.Count > 0
            ? (segments[^1].EndCm - segments[0].StartCm) * 10.0 : 0;
        double headLenMm    = headSegs.Count > 0
            ? (headSegs[^1].EndCm - headSegs[0].StartCm) * 10.0 : 0;

        // Density range of head segments
        var headDensities = headSegs.Where(s => s.SpecWeightGCm3 > 0)
                                    .Select(s => s.SpecWeightGCm3).ToList();
        string densityRange = headDensities.Count > 0
            ? $"{headDensities.Min():0.00} – {headDensities.Max():0.00} g/cm³"
            : "—";

        string lineType   = isSinking  ? "Sinking"    : "Floating";
        string lineFormat = isFullLine ? "Full Line"  : "Shooting Head";
        string water      = isSalt     ? "Salt water" : "Fresh water";

        // Build key notes (mimicking the RazorBlade notes block)
        string weightNote = headMassGr > 0
            ? $"With diameters indicated and target weight close to {headMassGr:0} gr " +
              $"the density of the head is {densityRange}."
            : $"Head density: {densityRange}.";
        const string dimNote    = "All dimensions are in millimeters.";
        const string changeNote = "Do not change length and diameters of segments. " +
                                  "For any adjustment please change specific weight only.";

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
                page.Margin(18);
                page.PageColor(BgPage);
                page.DefaultTextStyle(x => x.FontFamily("Arial").FontColor(ColText).FontSize(7.5f));

                page.Content().Column(col =>
                {
                    col.Spacing(7);

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
                        row.ConstantItem(150).AlignMiddle().Column(c =>
                        {
                            if (logoBytes != null)
                                c.Item().Image(logoBytes).FitWidth();
                            else
                                c.Item().Text("FlyLine Profiler")
                                    .FontSize(7).FontColor(ColMuted).Italic();
                        });

                        // Centre: project title
                        row.RelativeItem().AlignMiddle().AlignCenter()
                            .Text(projectName)
                            .FontSize(18).Bold().FontColor(ColText);

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
                        .Padding(6).Row(nr =>
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
                                c.Item().Text(label).FontSize(6.5f).FontColor(ColMuted);
                                c.Item().Text(value).FontSize(8.5f).Bold().FontColor(valColor);
                            });
                        }
                        SpecBlock("Type",          lineType,      ColText);
                        SpecBlock("Format",        lineFormat,    ColText);
                        SpecBlock("Head density",  densityRange,  ColAccent);
                        SpecBlock("Total length",  $"{totalLenMm / 10.0:0.0} cm  ({totalLenMm / 304.8:0.0} ft)", ColText);
                        SpecBlock("Head length",   headLenMm > 0 ? $"{headLenMm / 10.0:0.0} cm  ({headLenMm / 304.8:0.0} ft)" : "—", ColText);
                        SpecBlock("Head weight",   headMassGr > 0 ? $"{headMassGr:0.0} gr" : "—", ColAccent2);
                        SpecBlock("Total weight",  totalMassGr > 0 ? $"{totalMassGr:0.0} gr" : "—", ColText);
                        SpecBlock("AFFTA",         afftaBadge,    ColAccent2);
                    });

                    col.Item().LineHorizontal(0.5f).LineColor(ColBorder);

                    // ── Profile chart ──────────────────────────────────────
                    col.Item().Image(chartBytes).FitWidth();

                    // ── Colour legend — always present ────────────────────
                    // Build list of entries: sections if defined, else single design-colour entry
                    var legendEntries = new List<(string Hex, string Label, string Range)>();
                    if (colorSections != null && colorSections.Count > 0)
                    {
                        foreach (var cs in colorSections)
                        {
                            string range = $"{cs.StartCm:0.0}–{cs.EndCm:0.0} cm";
                            legendEntries.Add((cs.ColorHex.TrimStart('#'), cs.Label ?? "", range));
                        }
                    }
                    else
                    {
                        legendEntries.Add((designColorHex.TrimStart('#'), "Line colour", ""));
                    }

                    col.Item().Background(C("F7F8FA"))
                        .Border(0.5f).BorderColor(ColBorder)
                        .Padding(5).Column(legCol =>
                    {
                        legCol.Item().Text("Colour")
                            .FontSize(7).Bold().FontColor(ColMuted);
                        legCol.Item().Height(3);
                        legCol.Item().Row(legRow =>
                        {
                            foreach (var (hex, label, range) in legendEntries)
                            {
                                if (hex.Length < 6) continue;
                                try
                                {
                                    byte r = System.Convert.ToByte(hex[0..2], 16);
                                    byte g = System.Convert.ToByte(hex[2..4], 16);
                                    byte b = System.Convert.ToByte(hex[4..6], 16);
                                    var swatchColor = PdfColor.FromRGB(r, g, b);
                                    legRow.AutoItem().Column(sCol =>
                                    {
                                        // Coloured swatch
                                        sCol.Item().Width(52).Height(12)
                                            .Background(swatchColor)
                                            .Border(0.5f).BorderColor(ColBorder);
                                        sCol.Item().Height(2);
                                        // Hex code always shown
                                        sCol.Item().Width(52).Text($"#{hex.ToUpperInvariant()}")
                                            .FontSize(6.5f).Bold().FontColor(ColText).AlignCenter();
                                        // Label / range (if any)
                                        if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(range))
                                        {
                                            string sub = string.IsNullOrWhiteSpace(label) ? range
                                                       : string.IsNullOrWhiteSpace(range)  ? label
                                                       : $"{label}  {range}";
                                            sCol.Item().Width(52).Text(sub)
                                                .FontSize(6).FontColor(ColMuted).AlignCenter();
                                        }
                                    });
                                    legRow.ConstantItem(8);
                                }
                                catch { /* ignore bad hex */ }
                            }
                        });
                    });

                    // ── Colour note (if defined) ───────────────────────────
                    if (!string.IsNullOrWhiteSpace(colorNote))
                    {
                        col.Item().Background(C("FFF8E8"))
                            .Border(0.5f).BorderColor(C("E8C060"))
                            .Padding(5).Text(t =>
                            {
                                t.Span("Color:  ").FontSize(7.5f).FontColor(ColMuted).Bold();
                                t.Span(colorNote).FontSize(8.5f).Bold().FontColor(C("B87D20"));
                            });
                    }

                    col.Item().LineHorizontal(0.5f).LineColor(ColBorder);

                    // ── Segment data table ─────────────────────────────────
                    col.Item().Extend().Table(table =>
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
                             .Padding(3);

                        var hdrs = new[]
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
                            var seg    = segments[i];
                            bool isHd  = !isFullLine || seg.IsHead;
                            var  bg    = i % 2 == 0 ? BgPage : BgTblAlt;

                            void Cell(string text, PdfColor? fc = null, bool bold = false)
                            {
                                var cell = table.Cell().Background(bg)
                                    .BorderBottom(0.3f).BorderColor(ColBorder)
                                    .PaddingVertical(1).PaddingHorizontal(2);
                                var t = cell.Text(text).FontSize(7);
                                if (bold) t.Bold();
                                if (fc.HasValue) t.FontColor(fc.Value);
                            }

                            double grPerFt = seg.MassG > 0 && seg.LengthCm > 0
                                ? (seg.MassG * GramsToGrains) / (seg.LengthCm * CmToFt) : 0;

                            Cell(seg.Index.ToString());
                            Cell(seg.Name, isHd ? ColAccent : ColBlue, true);
                            Cell($"{seg.StartCm  * 10:0}");
                            Cell($"{seg.EndCm    * 10:0}");
                            Cell($"{seg.LengthCm * 10:0}");
                            Cell($"{seg.StartDiameterMm:0.000}");
                            Cell($"{seg.EndDiameterMm:0.000}");
                            Cell(seg.IsCylinder ? "—" : $"{seg.TaperMmPerMeter:+0.00;-0.00}");
                            Cell(seg.SpecWeightGCm3 > 0 ? $"{seg.SpecWeightGCm3:0.000}" : "—");
                            Cell(seg.MassG > 0 ? $"{seg.MassG:0.000}" : "—");
                            Cell(seg.MassG > 0 ? $"{seg.MassG * GramsToGrains:0.0}" : "—");
                            Cell(grPerFt > 0 ? $"{grPerFt:0.0}" : "—");
                            Cell(seg.SinkSpeedText);
                            Cell(isHd ? "HEAD" : "RUN", isHd ? ColAccent : ColMuted);
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

                        TotCell("∑"); TotCell("TOTAL");
                        TotCell(""); TotCell(""); TotCell("");
                        TotCell(""); TotCell(""); TotCell("");
                        TotCell(segments.Where(s => s.SpecWeightGCm3 > 0).Any() ? densityRange : "—");
                        TotCell($"{totalMassG:0.000}", true);
                        TotCell($"{totalMassGr:0.0}", true);
                        TotCell(""); TotCell(""); TotCell("");
                    });

                    // ── Footer ─────────────────────────────────────────────
                    col.Item().LineHorizontal(0.5f).LineColor(ColBorder);
                    col.Item().Row(row =>
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
