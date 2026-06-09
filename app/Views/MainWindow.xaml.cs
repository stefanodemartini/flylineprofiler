using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WpfColor    = System.Windows.Media.Color;
using ScottColor  = ScottPlot.Color;
using Microsoft.Win32;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Services;
using DiametroLineaDesktop.ViewModels;
using ScottPlot;

namespace DiametroLineaDesktop.Views;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm = new();
    private bool _plotInitialized = false;
    private bool _autoFitEnabled = true;
    private bool _chartControlsInitialized = false;
    private readonly List<ImportedSeries> _importedSeries = new();
    private string _lastImportedFile = "-";
    private string _uiStatus = "Ready";

    // Layer visibility
    private bool _showScanLayer    = true;
    private bool _showDesignLayer  = true;
    private bool _showSinkSpeedMap = false;
    private bool _showCompProfile  = false;

    // Segment drawing — node.Y stores FULL DIAMETER in mm (not radius)
    private readonly List<(double X, double Y)> _segmentNodes = new();
    private readonly Stack<double> _segmentUndoStack = new();
    private bool _segmentDrawMode = false;

    // Segment table (bound to Project DataGrid in XAML)
    public ObservableCollection<ProjectSegment> ProjectSegments { get; } = new();

    // Node table — editable DataGrid in Project panel (also bound in XAML)
    public ObservableCollection<DesignNode> DesignNodes { get; } = new();
    private bool _syncingNodes = false;

    // Colour sections — coloured bands painted over the profile, independent of nodes
    public ObservableCollection<LineColorSectionVm> ColorSections { get; } = new();
    private string _colorNote = string.Empty;
    public string ColorNote
    {
        get => _colorNote;
        set { _colorNote = value; OnPropertyChanged(nameof(ColorNote)); MarkDirty(); }
    }

    // Node drag state
    private double? _draggingNodeX = null;
    private const double DragHitRadiusPx = 12.0;

    // Label drag state — allows repositioning labels like Excel chart labels
    private double?                _draggingLabelNodeX   = null;
    private System.Windows.Point   _labelDragStartMouse;
    private (double LX, double LY) _labelDragStartOffset;
    private const double           LabelHitRadiusPx = 26.0;
    // Per-node label positions in DATA coordinates (keyed by node X); persisted in project file
    private readonly Dictionary<double, (double LX, double LY)> _nodeLabelOffsets = new();

    // Project state
    private string? _currentProjectPath = null;
    private bool    _isDirty            = false;
    private string  _projectName        = "Untitled";
    private DateTime _projectCreatedAt  = DateTime.UtcNow;
    private bool    _designMode         = true;
    // User-selectable design profile color
    private ScottColor _designLineColor = new ScottColor(220, 50, 50);
    private static readonly ScottColor[] DesignColorPresets =
    {
        new ScottColor(220,  50,  50),   // red (default)
        new ScottColor( 50, 180, 255),   // blue
        new ScottColor( 50, 210, 120),   // green
        new ScottColor(255, 180,  30),   // amber
        new ScottColor(200,  80, 220),   // purple
        new ScottColor(255, 255, 255),   // white
    };

    // Persists user-edited segment names and specific weights across RefreshSegmentTable() calls.
    // Key: (StartCm, EndCm) — survives as long as the segment boundaries don't move.
    private readonly Dictionary<(double, double), (string Name, double SpecWeight, bool IsHead)> _segmentMetadata = new();

    private bool   _useSharedDensity = true;
    private double _sharedDensity    = 0.0;

    private bool   _waterIsSalt = false;
    private double _waterTempC  = 20.0;
    private double _compTargetSpeedIns = 1.0; // desired compensation speed in in/s
    private bool   _isSinking  = false;
    private bool   _isFullLine = false;
    private string _afftaBadge = "AFFTA: —";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string UiStatus
    {
        get => _uiStatus;
        set { _uiStatus = value; OnPropertyChanged(nameof(UiStatus)); }
    }

    public string ImportedSeriesStatus   => $"Serie importate: {_importedSeries.Count}";
    public string LastImportedFileStatus => $"Ultimo CSV: {_lastImportedFile}";
    public string AutoFitStatus          => $"Auto-fit: {(_autoFitEnabled ? "ON" : "OFF")}";
    public string SmoothingAlphaStatus   => $"Alpha: {_vm.Settings.Chart.SmoothingAlpha:0.00}";
    public string LineWidthStatus        => $"Spessore: {_vm.Settings.Chart.LineWidth}";
    public string FilteredOpacityStatus  => $"Filtro: {_vm.Settings.Chart.FilteredOpacity:0.00}";
    public string RawOpacityStatus       => $"Raw: {_vm.Settings.Chart.RawOpacity:0.00}";
    public string DrawModeStatus         => _segmentDrawMode ? "DISEGNO SEGMENTI ATTIVO" : string.Empty;
    public string SegmentNodesStatus     => _segmentNodes.Count > 0 ? $"Nodi: {_segmentNodes.Count}" : string.Empty;

    public bool IsSinking
    {
        get => _isSinking;
        set { _isSinking = value; OnPropertyChanged(nameof(IsSinking)); UpdateLineTypeUI(); }
    }
    public bool IsFullLine
    {
        get => _isFullLine;
        set { _isFullLine = value; OnPropertyChanged(nameof(IsFullLine)); UpdateLineTypeUI(); RefreshSegmentTable(); RefreshPlot(); }
    }
    public string AfftaBadge
    {
        get => _afftaBadge;
        private set { _afftaBadge = value; OnPropertyChanged(nameof(AfftaBadge)); }
    }

    private string _hoverCoordsStatus = string.Empty;
    public  string HoverCoordsStatus
    {
        get => _hoverCoordsStatus;
        set { _hoverCoordsStatus = value; OnPropertyChanged(nameof(HoverCoordsStatus)); }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        InitializeChartControls();

        _vm.OnConnected += async () =>
        {
            _vm.Points.CollectionChanged -= Points_CollectionChanged;
            await _vm.LoadHistoryAsync();
            _vm.Points.CollectionChanged += Points_CollectionChanged;
            Dispatcher.Invoke(RefreshPlot);
        };

        _vm.OnDataCleared += () => Dispatcher.Invoke(RefreshPlot);

        Loaded += async (_, _) =>
        {
            ApplyDesignMode();   // apply immediately so UI never flashes in scan mode
            SetupPlot();
            UpdateLineTypeUI();
            _autoFitEnabled = _vm.Settings.Chart.AutoFit;
            _vm.Points.CollectionChanged += Points_CollectionChanged;
            _vm.PropertyChanged += (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshStatusBar();
                    if (e.PropertyName == nameof(MainViewModel.ScanReceiving))
                        RefreshPlot();
                    if (e.PropertyName == nameof(MainViewModel.ConnectionStatus))
                    {
                        bool connected = _vm.ConnectionStatus.Contains("Connesso", StringComparison.OrdinalIgnoreCase)
                                      || _vm.ConnectionStatus.Contains("OK",       StringComparison.OrdinalIgnoreCase);
                        ConnLed.Fill = connected
                            ? new System.Windows.Media.SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString("#28C996"))
                            : new System.Windows.Media.SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString("#E85454"));
                    }
                });
            };
            await _vm.InitializeAsync();
            RefreshPlot();
            RefreshStatusBar();
            UpdateProjectTitle();
            ApplyCompTarget(); // populate cm/s label with initial value
        };

        Closing += MainWindow_Closing;
    }

    private void InitializeChartControls()
    {
        SmoothingToggle.IsChecked = _vm.SmoothingEnabled;
        SmoothingAlphaSlider.Value = Math.Clamp(_vm.Settings.Chart.SmoothingAlpha, SmoothingAlphaSlider.Minimum, SmoothingAlphaSlider.Maximum);
        LineWidthSlider.Value = Math.Clamp(_vm.Settings.Chart.LineWidth, (int)LineWidthSlider.Minimum, (int)LineWidthSlider.Maximum);
        FilteredOpacitySlider.Value = Math.Clamp(_vm.Settings.Chart.FilteredOpacity, FilteredOpacitySlider.Minimum, FilteredOpacitySlider.Maximum);
        RawOpacitySlider.Value = Math.Clamp(_vm.Settings.Chart.RawOpacity, RawOpacitySlider.Minimum, RawOpacitySlider.Maximum);
        _chartControlsInitialized = true;
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshStatusBar()
    {
        OnPropertyChanged(nameof(ImportedSeriesStatus));
        OnPropertyChanged(nameof(LastImportedFileStatus));
        OnPropertyChanged(nameof(AutoFitStatus));
        OnPropertyChanged(nameof(SmoothingAlphaStatus));
        OnPropertyChanged(nameof(LineWidthStatus));
        OnPropertyChanged(nameof(FilteredOpacityStatus));
        OnPropertyChanged(nameof(RawOpacityStatus));
        OnPropertyChanged(nameof(DrawModeStatus));
        OnPropertyChanged(nameof(SegmentNodesStatus));
    }

    private void SetupPlot()
    {
        if (_plotInitialized) return;
        var plot = PlotControl.Plot;
        plot.Clear();
        plot.XLabel("Length (cm)");
        plot.YLabel("Diameter (mm)");
        plot.ShowLegend();

        PlotControl.PreviewMouseLeftButtonDown  += PlotControl_PreviewMouseLeftButtonDown;
        PlotControl.PreviewMouseRightButtonDown += PlotControl_PreviewMouseRightButtonDown;
        PlotControl.PreviewMouseMove            += PlotControl_PreviewMouseMove;
        PlotControl.PreviewMouseLeftButtonUp    += PlotControl_PreviewMouseLeftButtonUp;
        PlotControl.PreviewMouseDoubleClick     += PlotControl_PreviewMouseDoubleClick;
        PlotControl.MouseLeave                  += (_, _) => HoverCoordsStatus = string.Empty;

        _plotInitialized = true;
        ColorSections.CollectionChanged += (_, _) => { RefreshPlot(); MarkDirty(); };
        PlotControl.Refresh();

        // Analysis chart setup
        var ap = AnalysisPlotControl.Plot;
        ap.Title("Mass Distribution");
        ap.XLabel("Position (cm)");
        ap.YLabel("gr/ft");
        AnalysisPlotControl.Refresh();
    }

    private void Points_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshPlot();
            if (e.Action == NotifyCollectionChangedAction.Add)
                MarkDirty();
        });
    }

    private void RefreshPlot()
    {
        if (!_plotInitialized) return;
        try { RefreshPlotCore(); }
        catch (Exception ex)
        {
            UiStatus = $"Chart error: {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[RefreshPlot] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void RefreshPlotCore()
    {
        var plot = PlotControl.Plot;
        plot.Clear();

        var pts = _vm.Points.OrderBy(p => p.X).ToList();
        var displayedYs = GetDisplayedSeries(pts);  // full diameters

        // Design layer first so scan can render on top of it.
        if (_showDesignLayer && _designMode)
            RenderSegmentOverlay(plot);

        // Scan + imported series (always on top in design mode so they're not buried).
        if (_showScanLayer && pts.Count > 0)
        {
            double[] xs = pts.Select(p => p.X).ToArray();

            if (_vm.Settings.Chart.ShowFilteredSeries)
            {
                var alpha = (float)Math.Clamp(_vm.Settings.Chart.FilteredOpacity, 0.0, 1.0);
                // In design mode: bright yellow outline so it reads over the blue fill.
                var col = _designMode
                    ? new ScottColor(255, 220, 0).WithAlpha(0.95f)
                    : Colors.Blue.WithAlpha(alpha);
                int lw = _designMode ? 2 : _vm.Settings.Chart.LineWidth;

                double[] topYs = displayedYs.Select(y =>  y / 2.0).ToArray();
                double[] botYs = displayedYs.Select(y => -y / 2.0).ToArray();

                if (!_designMode)
                    DrawLineFill(plot, xs, topYs, botYs, col);

                var top = plot.Add.Scatter(xs, topYs);
                top.LegendText = "Scan";
                top.LineWidth  = lw;
                top.MarkerSize = 0;
                top.Color      = col;

                var bot = plot.Add.Scatter(xs, botYs);
                bot.LineWidth  = lw;
                bot.MarkerSize = 0;
                bot.Color      = col;
            }

            if (_vm.Settings.Chart.ShowRawSeries)
            {
                var rawAlpha = (float)Math.Clamp(_vm.Settings.Chart.RawOpacity, 0.0, 1.0);
                var rawCol   = Colors.Orange.WithAlpha(rawAlpha);

                var rawTop = plot.Add.Scatter(xs, pts.Select(p =>  p.RawY / 2.0).ToArray());
                rawTop.LegendText = "Raw";
                rawTop.LineWidth  = 1;
                rawTop.MarkerSize = 0;
                rawTop.Color      = rawCol;

                var rawBot = plot.Add.Scatter(xs, pts.Select(p => -p.RawY / 2.0).ToArray());
                rawBot.LineWidth  = 1;
                rawBot.MarkerSize = 0;
                rawBot.Color      = rawCol;
            }

            var hline = plot.Add.HorizontalLine(0);
            hline.Color     = Colors.Gray.WithAlpha(0.4f);
            hline.LineWidth = 0.8f;

            if (_vm.ScanReceiving && displayedYs.Length > 0)
            {
                double lastX    = pts[^1].X;
                double lastDiam = displayedYs[^1];
                var ann = plot.Add.Text($"Ø {lastDiam:0.00} mm\n{lastX:0.0} cm",
                                        lastX, lastDiam / 2.0);
                ann.LabelFontSize        = 11;
                ann.LabelBold            = true;
                ann.LabelFontColor       = Colors.DarkRed;
                ann.LabelBackgroundColor = Colors.White.WithAlpha(0.90f);
                ann.LabelBorderColor     = Colors.DarkRed;
                ann.LabelBorderWidth     = 1f;
                ann.LabelPadding         = 4;
                ann.OffsetX              = 10;
                ann.OffsetY              = -10;
            }
        }

        // Imported comparison series (gated by _showScanLayer)
        if (_showScanLayer)
        foreach (var series in _importedSeries)
        {
            double[] halfYs = series.Ys.Select(y =>  y / 2.0).ToArray();
            double[] negYs  = series.Ys.Select(y => -y / 2.0).ToArray();

            if (!_designMode)
                DrawLineFill(plot, series.Xs, halfYs, negYs, series.Color);

            var top = plot.Add.Scatter(series.Xs, halfYs);
            top.LegendText = series.Name;
            top.LineWidth  = 2;
            top.MarkerSize = 0;
            top.Color      = series.Color;

            var bot = plot.Add.Scatter(series.Xs, negYs);
            bot.LineWidth  = 2;
            bot.MarkerSize = 0;
            bot.Color      = series.Color;
        }

        // Orientation labels — always show in design mode so the user knows which end is which
        if (_designMode && _segmentNodes.Count >= 2)
        {
            var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
            double xMin = sorted[0].X;
            double xMax = sorted[^1].X;
            double yTop = sorted.Max(n => n.Y) / 2.0;

            var tipLbl = plot.Add.Text("◀ FLY TIP", xMin, yTop);
            tipLbl.LabelFontSize        = 10;
            tipLbl.LabelBold            = true;
            tipLbl.LabelFontColor       = new ScottColor(80, 200, 255);
            tipLbl.LabelBackgroundColor = ScottPlot.Colors.Transparent;
            tipLbl.LabelBorderColor     = ScottPlot.Colors.Transparent;
            tipLbl.LabelAlignment       = Alignment.UpperLeft;

            var reelLbl = plot.Add.Text("REEL ▶", xMax, yTop);
            reelLbl.LabelFontSize        = 10;
            reelLbl.LabelBold            = true;
            reelLbl.LabelFontColor       = new ScottColor(80, 200, 255);
            reelLbl.LabelBackgroundColor = ScottPlot.Colors.Transparent;
            reelLbl.LabelBorderColor     = ScottPlot.Colors.Transparent;
            reelLbl.LabelAlignment       = Alignment.UpperRight;
        }

        plot.XLabel("Length (cm)");
        plot.YLabel("Diameter (mm)");
        plot.ShowLegend();

        if (_autoFitEnabled)
        {
            plot.Axes.AutoScale();
            // Add extra bottom margin so node labels don't clip against the canvas edge
            var yRange = plot.Axes.GetLimits().Rect.Height;
            var limits = plot.Axes.GetLimits();
            plot.Axes.SetLimitsY(limits.Bottom - yRange * 0.12, limits.Top);
        }
        PlotControl.Refresh();
        RefreshStatusBar();
    }

    // ScottPlot 5 WPF: Refresh() dispatches async via InvalidateVisual, so AutoScale()
    // called inside RefreshPlot doesn't always compute correct bounds before the render.
    // Call this after RefreshPlot() in operations that must guarantee the view fits the data.
    // Note: always forces a fit regardless of _autoFitEnabled (that flag only gates live-scan updates).
    private void FitAfterRefresh()
    {
        PlotControl.Plot.Axes.AutoScale();
        PlotControl.Refresh();
    }

    // ── Analysis chart: mass/unit-length + cumulative grain weight ────────────
    private void RefreshAnalysisPlot()
    {
        if (!_plotInitialized || !_designMode) return;
        var segs = ProjectSegments;
        var ap   = AnalysisPlotControl.Plot;
        ap.Clear();

        if (segs.Count == 0) { AnalysisPlotControl.Refresh(); return; }

        const double GramsToGrains = 15.4324;
        const double CmToFt        = 1.0 / 30.48;
        const double AfftaBoundCm  = 914.4;   // 30 ft

        // ── Compute gr/ft per segment ──────────────────────────────────────
        var grPerFtList = new List<double>();
        foreach (var seg in segs)
        {
            double v = (seg.MassG > 0 && seg.LengthCm > 0)
                ? (seg.MassG * GramsToGrains) / (seg.LengthCm * CmToFt)
                : 0;
            grPerFtList.Add(v);
        }
        double maxGrPerFt = grPerFtList.Max();
        if (maxGrPerFt <= 0) maxGrPerFt = 1;

        // ── Compute cumulative grains ──────────────────────────────────────
        var cumXs = new List<double>();
        var cumRaw = new List<double>(); // actual grains
        double running = 0;
        cumXs.Add(segs[0].StartCm); cumRaw.Add(0);
        foreach (var seg in segs)
        {
            running += seg.MassG * GramsToGrains;
            cumXs.Add(seg.EndCm);
            cumRaw.Add(running);
        }
        double totalGrains = running;
        // Scale cumulative line to 90% of the bar height range so it fits on same axis
        double scale = totalGrains > 0 ? (maxGrPerFt * 0.9) / totalGrains : 1;
        double[] cumScaled = cumRaw.Select(v => v * scale).ToArray();

        // ── Per-segment gr/ft bars + per-segment CoM dots ─────────────────
        double totalMassWeightedX = 0;
        double totalMassForCoM    = 0;

        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            bool isHead = !_isFullLine || seg.IsHead;

            if (grPerFtList[i] > 0)
            {
                var rect = ap.Add.Rectangle(seg.StartCm, seg.EndCm, 0, grPerFtList[i]);
                rect.FillColor = isHead
                    ? new ScottColor(220, 80, 80).WithAlpha(0.55f)
                    : new ScottColor(80, 140, 220).WithAlpha(0.45f);
                rect.LineWidth = 0;
            }

            // Per-segment center of mass (exact frustum formula)
            if (seg.MassG > 0 && seg.LengthCm > 0)
            {
                double r1 = seg.StartDiameterMm / 2.0;
                double r2 = seg.EndDiameterMm   / 2.0;
                double denom = r1 * r1 + r1 * r2 + r2 * r2;
                double f = denom > 0
                    ? (r1 * r1 + 2 * r1 * r2 + 3 * r2 * r2) / (4.0 * denom)
                    : 0.5;
                double comX = seg.StartCm + f * seg.LengthCm;

                // Accumulate for total CoM — head segments only
                if (isHead)
                {
                    totalMassWeightedX += seg.MassG * comX;
                    totalMassForCoM    += seg.MassG;
                }

                // Draw a small tick at the CoM position at bar height
                if (grPerFtList[i] > 0)
                {
                    double barH = grPerFtList[i];
                    var tick = ap.Add.Scatter(new[] { comX }, new[] { barH });
                    tick.MarkerSize  = 7;
                    tick.MarkerShape = ScottPlot.MarkerShape.FilledDiamond;
                    tick.Color       = isHead
                        ? new ScottColor(255, 120, 120)
                        : new ScottColor(120, 180, 255);
                    tick.LineWidth   = 0;
                }
            }
        }

        // ── Cumulative line (scaled, same axis) ────────────────────────────
        var cumLine = ap.Add.Scatter(cumXs.ToArray(), cumScaled);
        cumLine.LegendText = $"Cumul. (total {totalGrains:0} gr)";
        cumLine.LineWidth  = 2;
        cumLine.MarkerSize = 0;
        cumLine.Color      = new ScottColor(80, 220, 160);

        // ── Total center of mass vertical line ─────────────────────────────
        if (totalMassForCoM > 0)
        {
            double totalComX = totalMassWeightedX / totalMassForCoM;
            var headSegs  = segs.Where(s => !_isFullLine || s.IsHead).ToList();
            double headStart = headSegs.Count > 0 ? headSegs[0].StartCm  : segs[0].StartCm;
            double headEnd   = headSegs.Count > 0 ? headSegs[^1].EndCm   : segs[^1].EndCm;
            double comPct    = (totalComX - headStart) / (headEnd - headStart) * 100.0;

            var (comChar, comColor) = comPct switch
            {
                < 40  => ("Very tip-heavy — nymphing/streamer", new ScottColor(255, 100, 100)),
                < 47  => ("Slightly front — versatile/presentation", new ScottColor(100, 220, 100)),
                < 53  => ("Neutral — distance/loop efficiency", new ScottColor(100, 200, 255)),
                < 58  => ("Slightly rear — distance oriented", new ScottColor(255, 180, 50)),
                _     => ("Very rear-heavy — max distance", new ScottColor(255, 80, 80)),
            };

            var comLine = ap.Add.VerticalLine(totalComX);
            comLine.Color       = comColor.WithAlpha(0.9f);
            comLine.LineWidth   = 2f;
            comLine.LinePattern = ScottPlot.LinePattern.Dashed;

            var comLbl = ap.Add.Text($"CoM {comPct:0.0}%  {comChar}", totalComX, maxGrPerFt);
            comLbl.LabelFontSize        = 9;
            comLbl.LabelBold            = true;
            comLbl.LabelFontColor       = comColor;
            comLbl.LabelBackgroundColor = new ScottColor(20, 20, 30).WithAlpha(0.75f);
            comLbl.LabelBorderColor     = ScottPlot.Colors.Transparent;
            comLbl.LabelAlignment       = Alignment.UpperCenter;
        }

        // ── AFFTA 30 ft boundary ───────────────────────────────────────────
        var vline = ap.Add.VerticalLine(AfftaBoundCm);
        vline.Color       = new ScottColor(255, 200, 50).WithAlpha(0.85f);
        vline.LineWidth   = 1.5f;
        vline.LinePattern = ScottPlot.LinePattern.Dashed;

        // Use the same frustum-volume formula as ComputeAfftaBadge so the two numbers always agree
        double grAt30   = 0;
        double covered30 = 0;
        foreach (var seg in segs)
        {
            if (covered30 >= AfftaBoundCm || seg.StartCm >= AfftaBoundCm) break;
            double segLen  = seg.LengthCm;
            double usedLen = Math.Min(segLen, AfftaBoundCm - covered30);
            if (usedLen <= 0 || seg.SpecWeightGCm3 <= 0) { covered30 += usedLen; continue; }
            double frac   = usedLen / segLen;
            double r1     = seg.StartDiameterMm / 2.0;
            double r2p    = r1 + (seg.EndDiameterMm / 2.0 - r1) * frac;
            double lenMm  = usedLen * 10.0;
            double volMm3 = Math.PI * lenMm / 3.0 * (r1*r1 + r1*r2p + r2p*r2p);
            grAt30   += volMm3 / 1000.0 * seg.SpecWeightGCm3 * GramsToGrains;
            covered30 += usedLen;
        }
        var lbl = ap.Add.Text($"30ft: {grAt30:0} gr", AfftaBoundCm, 0);
        lbl.LabelFontSize        = 9;
        lbl.LabelFontColor       = new ScottColor(255, 200, 50);
        lbl.LabelBackgroundColor = ScottPlot.Colors.Transparent;
        lbl.LabelBorderColor     = ScottPlot.Colors.Transparent;
        lbl.LabelAlignment       = Alignment.LowerLeft;
        lbl.OffsetX              = 4;

        ap.Title("Mass Distribution  |  red = head  blue = running  ◆ = segment CoM  — = total CoM");
        ap.XLabel("Position (cm)");
        ap.YLabel("gr / ft");
        ap.ShowLegend();
        ap.Axes.AutoScale();
        AnalysisPlotControl.Refresh();
    }

    // ── Project helpers ──────────────────────────────────────────────────────

    private void UpdateProjectTitle()
    {
        var star = _isDirty ? " *" : string.Empty;
        Title = $"FlyLine Profiler — {_projectName}{star}";
        if (ProjectNameText != null)
            ProjectNameText.Text = $"{_projectName}{star}";
    }

    private void MarkDirty()
    {
        _isDirty = true;
        UpdateProjectTitle();
    }

    private bool ConfirmDiscardIfDirty()
    {
        if (!_isDirty) return true;
        var r = MessageBox.Show(
            $"Project \"{_projectName}\" has unsaved changes.\nDiscard and continue?",
            "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    private void ClearProjectState()
    {
        _vm.Points.CollectionChanged -= Points_CollectionChanged;
        _vm.ClearAllData();
        _importedSeries.Clear();
        _segmentNodes.Clear();
        _segmentUndoStack.Clear();
        _nodeLabelOffsets.Clear();
        ColorSections.Clear();
        _colorNote = string.Empty;
        OnPropertyChanged(nameof(ColorNote));
        // Reset profile colour field to default red (no side-effects during clear)
        _designLineColor = new ScottColor(220, 50, 50);
        if (ProfileColorBtn != null)
            ProfileColorBtn.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(220, 50, 50));
        foreach (var seg in ProjectSegments) seg.PropertyChanged -= OnSegmentPropertyChanged;
        _segmentMetadata.Clear();
        ProjectSegments.Clear();
        DesignNodes.Clear();
        TotalVolumeText   = string.Empty;
        _lastImportedFile = "-";
        _useSharedDensity = true;
        _sharedDensity    = 0.0;
        _waterIsSalt      = false;

        // Clear analysis chart
        if (_plotInitialized)
        {
            AnalysisPlotControl.Plot.Clear();
            AnalysisPlotControl.Refresh();
        }
        _waterTempC       = 20.0;
        OnPropertyChanged(nameof(UseSharedDensity));
        OnPropertyChanged(nameof(SharedDensity));
        OnPropertyChanged(nameof(WaterIsSalt));
        OnPropertyChanged(nameof(WaterTempC));
        _vm.Points.CollectionChanged += Points_CollectionChanged;
    }

    private void SaveProjectToFile(string path)
    {
        var project = new FlyLineProject
        {
            Name               = _projectName,
            CreatedAt          = _projectCreatedAt,
            UseSharedDensity   = _useSharedDensity,
            SharedDensityGCm3  = _sharedDensity,
            IsSinking          = _isSinking,
            IsFullLine         = _isFullLine,
            WaterType          = _waterIsSalt ? "salt" : "fresh",
            WaterTempC         = _waterTempC,
            ScanPoints = _vm.Points
                           .OrderBy(p => p.X)
                           .Select(p => new MeasurementPoint { X = p.X, RawY = p.RawY, FilteredY = p.FilteredY })
                           .ToList(),
            ImportedSeries = _importedSeries
                               .Select(s => new ProjectImportedSeries
                               {
                                   Name     = s.Name,
                                   Xs       = s.Xs,
                                   Ys       = s.Ys,
                                   ColorHex = $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}"
                               })
                               .ToList(),
            DesignNodes = _segmentNodes
                            .OrderBy(n => n.X)
                            .Select(n => new ProjectDesignNode { X = n.X, Y = n.Y })
                            .ToList(),
            NodeLabelOffsets = _nodeLabelOffsets
                            .Select(kv => new NodeLabelOffset { NodeX = kv.Key, LX = kv.Value.LX, LY = kv.Value.LY })
                            .ToList(),
            DesignLineColorHex = $"{_designLineColor.R:X2}{_designLineColor.G:X2}{_designLineColor.B:X2}",
            ColorNote     = _colorNote,
            ColorSections = ColorSections.Select(s => new LineColorSection
                            {
                                StartCm  = s.StartCm,
                                EndCm    = s.EndCm,
                                ColorHex = s.ColorHex,
                                Label    = s.Label
                            }).ToList(),
            SegmentMetadata = ProjectSegments
                                .Select(s => new ProjectSegmentMeta
                                {
                                    StartCm    = s.StartCm,
                                    EndCm      = s.EndCm,
                                    Name       = s.Name,
                                    SpecWeight = s.SpecWeightGCm3,
                                    IsHead     = s.IsHead
                               })
                               .ToList(),
        };

        ProjectService.Save(project, path);
        _currentProjectPath = path;
        _isDirty            = false;
        UpdateProjectTitle();
        UiStatus = $"Project saved: {Path.GetFileName(path)}";
    }

    private void LoadProjectFromFile(string path)
    {
        var project = ProjectService.Load(path);

        ClearProjectState();

        _vm.Points.CollectionChanged -= Points_CollectionChanged;
        foreach (var pt in project.ScanPoints)
            _vm.Points.Add(new MeasurementPoint { X = pt.X, RawY = pt.RawY, FilteredY = pt.FilteredY });

        foreach (var s in project.ImportedSeries)
        {
            _importedSeries.Add(new ImportedSeries
            {
                Name  = s.Name,
                Xs    = s.Xs,
                Ys    = s.Ys,
                Color = ParseColorHex(s.ColorHex, _importedSeries.Count)
            });
        }

        foreach (var n in project.DesignNodes)
            _segmentNodes.Add((n.X, n.Y));

        // Restore label offsets — skip stale entries (LX==0 && LY==0 means old-format data)
        _nodeLabelOffsets.Clear();
        foreach (var lo in project.NodeLabelOffsets)
            if (lo.LX != 0 || lo.LY != 0)
                _nodeLabelOffsets[lo.NodeX] = (lo.LX, lo.LY);

        // Restore profile line colour
        if (!string.IsNullOrWhiteSpace(project.DesignLineColorHex))
            ApplyDesignColor(project.DesignLineColorHex);

        // Restore colour sections
        ColorSections.Clear();
        foreach (var cs in project.ColorSections)
            ColorSections.Add(new LineColorSectionVm
            {
                StartCm  = cs.StartCm,
                EndCm    = cs.EndCm,
                ColorHex = cs.ColorHex,
                Label    = cs.Label
            });
        _colorNote = project.ColorNote ?? string.Empty;
        OnPropertyChanged(nameof(ColorNote));

        // Restore segment metadata (names, spec weights, head flag)
        _segmentMetadata.Clear();
        foreach (var m in project.SegmentMetadata)
            _segmentMetadata[(m.StartCm, m.EndCm)] = (m.Name, m.SpecWeight, m.IsHead);

        _useSharedDensity = project.UseSharedDensity;
        _sharedDensity    = project.SharedDensityGCm3;
        _isSinking        = project.IsSinking;
        _isFullLine       = project.IsFullLine;
        _waterIsSalt      = project.WaterType == "salt";
        _waterTempC       = project.WaterTempC;
        OnPropertyChanged(nameof(UseSharedDensity));
        OnPropertyChanged(nameof(SharedDensity));
        OnPropertyChanged(nameof(IsSinking));
        OnPropertyChanged(nameof(IsFullLine));
        OnPropertyChanged(nameof(WaterIsSalt));
        OnPropertyChanged(nameof(WaterTempC));

        _projectName        = project.Name;
        _projectCreatedAt   = project.CreatedAt;
        _currentProjectPath = path;
        _isDirty            = false;
        _lastImportedFile   = _importedSeries.Count > 0 ? _importedSeries[^1].Name : "-";

        _vm.Points.CollectionChanged += Points_CollectionChanged;

        RefreshPlot();
        FitAfterRefresh();
        RefreshSegmentTable();
        SpWeightColumn.IsReadOnly = _useSharedDensity;
        RefreshStatusBar();
        UpdateProjectTitle();
        UiStatus = $"Project loaded: {project.Name}  ({project.ScanPoints.Count} scan pts, {project.ImportedSeries.Count} series, {project.DesignNodes.Count} nodes)";
    }

    private static ScottColor ParseColorHex(string hex, int fallbackIndex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return new ScottColor(r, g, b);
            }
        }
        catch { /* fall through to default */ }
        return PickImportColor(fallbackIndex);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isDirty)
        {
            var r = MessageBox.Show(
                $"Project \"{_projectName}\" has unsaved changes.\nSave before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (r == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (r == MessageBoxResult.Yes)
                SaveProject_Click(this, new RoutedEventArgs());
        }
    }

    // ── Project button handlers ───────────────────────────────────────────────

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;

        // Ask for project name
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            "Enter a name for the new project:",
            "New Project",
            "Untitled");
        if (string.IsNullOrWhiteSpace(name)) return;

        ClearProjectState();
        _projectName        = name.Trim();
        _projectCreatedAt   = DateTime.UtcNow;
        _currentProjectPath = null;
        _isDirty            = false;

        RefreshPlot();
        RefreshStatusBar();
        UpdateProjectTitle();
        UiStatus = $"New project: {_projectName}";
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;

        var dlg = new OpenFileDialog
        {
            Filter      = ProjectService.FileFilter,
            Title       = "Open FlyLine Project",
            InitialDirectory = ProjectService.DefaultProjectFolder
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            LoadProjectFromFile(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open project:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectPath != null)
        {
            try { SaveProjectToFile(_currentProjectPath); }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            SaveProjectAs_Click(sender, e);
        }
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;

        ClearProjectState();
        _projectName        = "Untitled";
        _projectCreatedAt   = DateTime.UtcNow;
        _currentProjectPath = null;
        _isDirty            = false;

        RefreshPlot();
        RefreshStatusBar();
        UpdateProjectTitle();
        UiStatus = "Project closed";
    }

    private void DesignModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _designMode = DesignModeToggle.IsChecked == true;
        ApplyDesignMode();
    }

    private void ApplyDesignMode()
    {
        var scanVis   = _designMode ? Visibility.Collapsed : Visibility.Visible;
        var designVis = _designMode ? Visibility.Visible   : Visibility.Collapsed;

        // Scan-only elements
        ScanButtonsPanel.Visibility    = scanVis;
        ConnectionPanel.Visibility     = scanVis;
        MetricsStrip.Visibility        = scanVis;
        LogExpander.Visibility         = scanVis;

        // Keep the metrics row height at 0 when hidden so it doesn't reserve space
        MainGrid.RowDefinitions[2].Height = _designMode
            ? new GridLength(0)
            : new GridLength(72);

        // Design-only elements
        DesignToolsPanel.Visibility          = designVis;
        DesignProjectExpander.Visibility     = designVis;
        AnalysisChartPanel.Visibility        = designVis;
        MainGrid.RowDefinitions[4].Height    = _designMode ? new GridLength(180) : new GridLength(0);

        // Hide scan layer by default when entering design mode; restore when leaving
        if (_designMode)
        {
            SetShowScanLayer(false, updateToggle: true);
        }
        else
        {
            SetShowScanLayer(true, updateToggle: true);
        }

        RefreshPlot();
        RefreshAnalysisPlot();
    }

    private void SetShowScanLayer(bool show, bool updateToggle)
    {
        _showScanLayer = show;
        if (updateToggle && ShowScanBtn != null)
            ShowScanBtn.Content = show ? "Hide Scan" : "Show Scan";
    }

    private string GetChartTitle()
    {
        string mode;
        if (_designMode)
            mode = _showCompProfile ? "Compensated Profile" : "Design";
        else
            mode = "Scan";
        return $"{_projectName}  —  {mode}";
    }

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ProjectService.DefaultProjectFolder);

        var dlg = new SaveFileDialog
        {
            Filter           = ProjectService.FileFilter,
            DefaultExt       = ProjectService.FileExtension,
            FileName         = _projectName,
            InitialDirectory = ProjectService.DefaultProjectFolder,
            Title            = "Save FlyLine Project As"
        };
        if (dlg.ShowDialog() != true) return;

        // Use the file name (without extension) as the project name if untitled
        if (_projectName == "Untitled")
            _projectName = Path.GetFileNameWithoutExtension(dlg.FileName);

        try { SaveProjectToFile(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Draws a filled band between topYs and botYs.
    /// When <paramref name="solid"/> is true (design profile) renders N concentric bands
    /// outer-to-inner with Lambert cylindrical shading: dark edges → rich body → specular
    /// centre, creating a convincing solid round cross-section.
    /// When false (scan / imported series) uses a lightweight 3-layer transparent blend.
    /// </summary>
    private static void DrawLineFill(ScottPlot.Plot plot,
                                     double[] xs, double[] topYs, double[] botYs,
                                     ScottPlot.Color bodyColor,
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
                band.LineColor = Colors.Transparent;
            }
        }
        else
        {
            // ── Lightweight 3-layer blend for scan / imported series ──────
            var body = plot.Add.Polygon(Band(1.0));
            body.FillColor = bodyColor.WithAlpha(0.55f);
            body.LineWidth = 0;
            body.LineColor = Colors.Transparent;

            var mid = plot.Add.Polygon(Band(0.60));
            mid.FillColor = Colors.White.WithAlpha(0.14f);
            mid.LineWidth = 0;
            mid.LineColor = Colors.Transparent;

            var hi = plot.Add.Polygon(Band(0.25));
            hi.FillColor = Colors.White.WithAlpha(0.22f);
            hi.LineWidth = 0;
            hi.LineColor = Colors.Transparent;
        }
    }

    private double[] GetDisplayedSeries(IReadOnlyList<MeasurementPoint> points)
    {
        if (points.Count == 0)
            return Array.Empty<double>();

        if (!_vm.SmoothingEnabled)
            return points.Select(p => p.FilteredY).ToArray();

        double alpha = Math.Clamp(_vm.Settings.Chart.SmoothingAlpha, 0.0, 1.0);
        double? ema = null;
        var values = new double[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            double sample = points[i].FilteredY;
            ema = ema is null ? sample : alpha * sample + (1 - alpha) * ema.Value;
            values[i] = Math.Round(ema.Value, 3);
        }

        return values;
    }

    // ── Scan layer renderer ─────────────────────────────────────────────────
    // In scan mode: filled gradient band (same as before).
    // In design mode: outline only on top of the design fill so the scan stays visible.
    private void RenderScanLayer(Plot plot, List<MeasurementPoint> pts, double[] displayedYs)
    {
        if (!_showScanLayer || pts.Count == 0) return;

        double[] xs    = pts.Select(p => p.X).ToArray();
        bool     inDesign = _designMode;

        if (_vm.Settings.Chart.ShowFilteredSeries)
        {
            var alpha = (float)Math.Clamp(_vm.Settings.Chart.FilteredOpacity, 0.0, 1.0);
            // In design mode use a bright contrasting colour (yellow-green) so the scan outline
            // reads clearly against the blue design fill.
            var col = inDesign
                ? new ScottColor(180, 255, 80).WithAlpha(0.90f)
                : Colors.Blue.WithAlpha(alpha);
            int lw = inDesign ? 2 : _vm.Settings.Chart.LineWidth;

            double[] topYs = displayedYs.Select(y =>  y / 2.0).ToArray();
            double[] botYs = displayedYs.Select(y => -y / 2.0).ToArray();

            if (!inDesign)
                DrawLineFill(plot, xs, topYs, botYs, col);

            var top = plot.Add.Scatter(xs, topYs);
            top.LegendText = "Scan";
            top.LineWidth  = lw;
            top.MarkerSize = 0;
            top.Color      = col;

            var bot = plot.Add.Scatter(xs, botYs);
            bot.LineWidth  = lw;
            bot.MarkerSize = 0;
            bot.Color      = col;
        }

        if (_vm.Settings.Chart.ShowRawSeries)
        {
            var rawAlpha = (float)Math.Clamp(_vm.Settings.Chart.RawOpacity, 0.0, 1.0);
            var rawCol   = Colors.Orange.WithAlpha(rawAlpha);

            var rawTop = plot.Add.Scatter(xs, pts.Select(p =>  p.RawY / 2.0).ToArray());
            rawTop.LegendText = "Raw";
            rawTop.LineWidth  = 1;
            rawTop.MarkerSize = 0;
            rawTop.Color      = rawCol;

            var rawBot = plot.Add.Scatter(xs, pts.Select(p => -p.RawY / 2.0).ToArray());
            rawBot.LineWidth  = 1;
            rawBot.MarkerSize = 0;
            rawBot.Color      = rawCol;
        }

        // Centre axis
        var hline = plot.Add.HorizontalLine(0);
        hline.Color     = Colors.Gray.WithAlpha(0.4f);
        hline.LineWidth = 0.8f;

        // Live scan annotation
        if (_vm.ScanReceiving && displayedYs.Length > 0)
        {
            double lastX    = pts[^1].X;
            double lastDiam = displayedYs[^1];
            var ann = plot.Add.Text($"Ø {lastDiam:0.00} mm\n{lastX:0.0} cm",
                                    lastX, lastDiam / 2.0);
            ann.LabelFontSize        = 11;
            ann.LabelBold            = true;
            ann.LabelFontColor       = Colors.DarkRed;
            ann.LabelBackgroundColor = Colors.White.WithAlpha(0.90f);
            ann.LabelBorderColor     = Colors.DarkRed;
            ann.LabelBorderWidth     = 1f;
            ann.LabelPadding         = 4;
            ann.OffsetX              = 10;
            ann.OffsetY              = -10;
        }

        // Imported comparison series
        foreach (var series in _importedSeries)
        {
            double[] halfYs = series.Ys.Select(y =>  y / 2.0).ToArray();
            double[] negYs  = series.Ys.Select(y => -y / 2.0).ToArray();

            if (!inDesign)
                DrawLineFill(plot, series.Xs, halfYs, negYs, series.Color);

            var top = plot.Add.Scatter(series.Xs, halfYs);
            top.LegendText = series.Name;
            top.LineWidth  = 2;
            top.MarkerSize = 0;
            top.Color      = series.Color;

            var bot = plot.Add.Scatter(series.Xs, negYs);
            bot.LineWidth  = 2;
            bot.MarkerSize = 0;
            bot.Color      = series.Color;
        }
    }

    // ── Sink-speed heat-map overlay ──────────────────────────────────────────
    // Each ~12 cm slice is drawn as a colour-coded trapezoid, inserted between
    // the gradient fill and the profile outline so it's always visible.
    // Colour = individual cylinder terminal speed for that slice's diameter:
    //   blue (slow/floating) → cyan → green → yellow → red (fast sinking)
    private void RenderSinkSpeedOverlay(Plot plot, List<(double X, double Y)> sorted)
    {
        // ── Pass 1: collect all slice speeds for colour normalisation ──────
        var slices = new List<(double xStart, double xEnd, double dStart, double dEnd, double speed)>();

        for (int si = 0; si < sorted.Count - 1; si++)
        {
            double x0 = sorted[si].X, x1 = sorted[si + 1].X;
            double d0 = sorted[si].Y, d1 = sorted[si + 1].Y;
            double lenCm = x1 - x0;
            if (lenCm <= 0) continue;

            var seg = ProjectSegments.FirstOrDefault(
                s => Math.Abs(s.StartCm - x0) < 0.05 && Math.Abs(s.EndCm - x1) < 0.05);
            if (seg == null || seg.SpecWeightGCm3 <= 0) continue;

            int n = Math.Max(1, (int)Math.Ceiling(lenCm / 12.0));
            for (int i = 0; i < n; i++)
            {
                double t0 = (double)i / n, t1 = (i + 1.0) / n, tm = (t0 + t1) / 2.0;
                double xs = x0 + t0 * lenCm,     xe = x0 + t1 * lenCm;
                double ds = d0 + t0 * (d1 - d0), de = d0 + t1 * (d1 - d0);
                double dm = d0 + tm * (d1 - d0);

                double v = SinkingSpeedCalc.CylinderSinkSpeed(
                    _waterIsSalt, _waterTempC, dm, seg.SpecWeightGCm3);
                if (!double.IsNaN(v))
                    slices.Add((xs, xe, ds, de, v));
            }
        }

        if (slices.Count == 0) return;

        double minV  = slices.Min(s => s.speed);
        double maxV  = slices.Max(s => s.speed);
        double range = Math.Max(maxV - minV, 1e-10);

        // ── Pass 2: draw trapezoids ────────────────────────────────────────
        foreach (var (xs, xe, ds, de, v) in slices)
        {
            double t   = (v - minV) / range;
            var    fill = SpeedColor(t).WithAlpha(0.70f);

            var poly = plot.Add.Polygon(new ScottPlot.Coordinates[]
            {
                new(xs,  ds / 2.0),
                new(xe,  de / 2.0),
                new(xe, -de / 2.0),
                new(xs, -ds / 2.0),
            });
            poly.FillColor = fill;
            poly.LineWidth = 0;
            poly.LineColor = Colors.Transparent;
        }

        // ── 4-stop gradient legend (matches density map style) ────────────
        double minIns = minV * 39.3701, maxIns = maxV * 39.3701;
        for (int stop = 0; stop < 4; stop++)
        {
            double t     = stop / 3.0;
            double speed = minIns + t * (maxIns - minIns);
            var entry    = plot.Add.Scatter(new double[] {}, new double[] {});
            entry.Color      = SpeedColor(t);
            entry.LineWidth  = 8;
            entry.LegendText = $"{speed:+0.000;-0.000} in/s";
        }
    }

    /// <summary>Maps t ∈ [0,1] to a blue→cyan→green→yellow→red colour ramp.</summary>
    private static ScottColor SpeedColor(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte r, g, b;
        if (t < 0.25)      { double s = t / 0.25;        r = 0;           g = (byte)(s * 255);       b = 255; }
        else if (t < 0.50) { double s = (t - 0.25)/0.25; r = 0;           g = 255;                   b = (byte)((1-s)*255); }
        else if (t < 0.75) { double s = (t - 0.50)/0.25; r = (byte)(s*255); g = 255;                 b = 0; }
        else               { double s = (t - 0.75)/0.25; r = 255;          g = (byte)((1-s)*255);    b = 0; }
        return new ScottColor(r, g, b);
    }

    /// <summary>Linear interpolation of the design profile diameter at an arbitrary X position.</summary>
    private static double InterpolateProfileY(List<(double X, double Y)> sorted, double x)
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

    /// <summary>
    /// Paints coloured bands over the profile for each defined colour section.
    /// Each section is rendered with its own full Lambert cylindrical shading so the
    /// dark-edge / bright-centre gradient is just as dramatic as the rest of the profile.
    /// </summary>
    private void RenderColorSections(Plot plot, List<(double X, double Y)> sorted)
    {
        foreach (var sec in ColorSections)
        {
            if (sec.EndCm <= sec.StartCm) continue;
            if (!TryParseHexColor(sec.ColorHex, out var secColor)) continue;

            var pts = new List<(double X, double Y)>
                { (sec.StartCm, InterpolateProfileY(sorted, sec.StartCm)) };
            foreach (var n in sorted.Where(n => n.X > sec.StartCm && n.X < sec.EndCm))
                pts.Add(n);
            pts.Add((sec.EndCm, InterpolateProfileY(sorted, sec.EndCm)));

            double[] sxs  = pts.Select(p => p.X).ToArray();
            double[] stop = pts.Select(p =>  p.Y / 2.0).ToArray();
            double[] sbot = pts.Select(p => -p.Y / 2.0).ToArray();

            // Full Lambert cylindrical shading with the section colour — paints over
            // the design-colour base in this band, preserving the full gradient.
            DrawLineFill(plot, sxs, stop, sbot, secColor, solid: true);

            // Thin coloured outline on top and bottom edges
            var tl = plot.Add.Scatter(sxs, stop); tl.Color = secColor; tl.LineWidth = 1.5f; tl.MarkerSize = 0;
            var bl = plot.Add.Scatter(sxs, sbot); bl.Color = secColor; bl.LineWidth = 1.5f; bl.MarkerSize = 0;

            // Section label — centred above the section if the Label field is set
            if (!string.IsNullOrWhiteSpace(sec.Label))
            {
                double cx      = (sec.StartCm + sec.EndCm) / 2.0;
                double topAtCx = InterpolateProfileY(sorted, cx) / 2.0;
                double gap     = InterpolateProfileY(sorted, cx) * 0.18; // 18 % of diameter
                var lbl = plot.Add.Text(sec.Label, cx, topAtCx + gap);
                lbl.LabelFontSize        = 11;
                lbl.LabelBold            = true;
                lbl.LabelFontColor       = secColor;
                lbl.LabelAlignment       = Alignment.LowerCenter;
                lbl.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.75f);
                lbl.LabelBorderColor     = secColor;
                lbl.LabelBorderWidth     = 1f;
                lbl.LabelPadding         = 3;
                lbl.OffsetX              = 0;
                lbl.OffsetY              = 0;
            }
        }
    }

    private static bool TryParseHexColor(string hex6, out ScottColor color)
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

    // Segment overlay rendering
    // node.Y stores full diameter in mm; chart Y axis is radius (diameter/2)
    private void RenderSegmentOverlay(Plot plot)
    {
        if (_segmentNodes.Count == 0) return;

        var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
        double[] xs        = sorted.Select(n => n.X).ToArray();
        double[] halfYs    = sorted.Select(n =>  n.Y / 2.0).ToArray();
        double[] negHalfYs = sorted.Select(n => -n.Y / 2.0).ToArray();

        var designColor = _designLineColor;
        bool compActive = _showCompProfile && ProjectSegments.Any(s => s.HasCompensation);

        if (sorted.Count >= 2)
        {
            if (!compActive)
            {
                // 1. Base fill: always use the design colour Lambert shading
                DrawLineFill(plot, xs, halfYs, negHalfYs, designColor, solid: true);

                // 1b. Colour-band overlays (flat polygons, painted over the base fill)
                if (ColorSections.Count > 0)
                    RenderColorSections(plot, sorted);

                // 2. Sink-speed heat-map (drawn over the fill)
                if (_showSinkSpeedMap)
                    RenderSinkSpeedOverlay(plot, sorted);

                // 3. Profile outline — crisp, slightly thicker than before
                var topLine = plot.Add.Scatter(xs, halfYs);
                topLine.LegendText = $"Design ({sorted.Count} nodes)";
                topLine.Color      = designColor;
                topLine.LineWidth  = 2.5f;
                topLine.MarkerSize = 0;

                var botLine = plot.Add.Scatter(xs, negHalfYs);
                botLine.Color      = designColor;
                botLine.LineWidth  = 2.5f;
                botLine.MarkerSize = 0;

                // 4. Subtle segment-boundary ticks (hairline, not dashed — less noise)
                foreach (var node in sorted)
                {
                    var tick = plot.Add.Scatter(
                        new[] { node.X, node.X },
                        new[] { node.Y / 2.0, -node.Y / 2.0 });
                    tick.Color       = designColor.WithAlpha(0.25f);
                    tick.LineWidth   = 0.8f;
                    tick.MarkerSize  = 0;
                }
            }
            else
            {
                // When comp profile is shown: draw original as faint ghost only
                var ghostColor = designColor.WithAlpha(0.18f);
                var topGhost = plot.Add.Scatter(xs, halfYs);
                topGhost.Color      = ghostColor;
                topGhost.LineWidth  = 1;
                topGhost.MarkerSize = 0;
                var botGhost = plot.Add.Scatter(xs, negHalfYs);
                botGhost.Color      = ghostColor;
                botGhost.LineWidth  = 1;
                botGhost.MarkerSize = 0;
            }

            // Compensated profile (drawn last so it's on top)
            RenderCompensatedOverlay(plot, sorted);
        }

        if (!compActive)
        {
            // Node markers — filled circle with white ring for a clean look
            var markers = plot.Add.Scatter(xs, halfYs);
            markers.Color        = designColor;
            markers.LineWidth    = 0;
            markers.MarkerSize   = 9;
            markers.MarkerShape  = MarkerShape.OpenCircle;

            var markersInner = plot.Add.Scatter(xs, halfYs);
            markersInner.Color       = designColor.WithAlpha(0.85f);
            markersInner.LineWidth   = 0;
            markersInner.MarkerSize  = 5;
            markersInner.MarkerShape = MarkerShape.FilledCircle;

            // Node labels — positions stored in data coordinates so leader lines work
            for (int ni = 0; ni < sorted.Count; ni++)
            {
                var node = sorted[ni];
                double chartYBottom = -node.Y / 2.0;
                // Default: stagger below the profile by a fraction of the diameter
                double gap = node.Y * 0.40;
                double defaultLX = node.X;
                double defaultLY = chartYBottom - gap * (ni % 2 == 0 ? 1.0 : 2.2);

                var (lx, ly) = _nodeLabelOffsets.TryGetValue(node.X, out var saved)
                               ? saved : (defaultLX, defaultLY);

                // Leader line — fixed dark colour so it stays visible regardless of profile colour
                double leaderStartY = chartYBottom * 0.85;
                var leaderColor = new ScottColor(100, 100, 100);   // neutral dark grey
                var leader = plot.Add.Scatter(
                    new double[] { node.X, lx },
                    new double[] { leaderStartY, ly });
                leader.Color      = leaderColor;
                leader.LineWidth  = 1.2f;
                leader.MarkerSize = 0;

                // Label at computed data position
                var lbl = plot.Add.Text($"Ø {node.Y:0.000}  {node.X:0.0} cm", lx, ly);
                lbl.LabelFontSize        = 11;
                lbl.LabelBold            = true;
                lbl.LabelFontColor       = new ScottColor(50, 50, 50);
                lbl.LabelAlignment       = Alignment.UpperCenter;
                lbl.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.95f);
                lbl.LabelBorderColor     = leaderColor;
                lbl.LabelBorderWidth     = 1f;
                lbl.LabelPadding         = 3;
                lbl.OffsetX              = 0;
                lbl.OffsetY              = 0;
            }
        }
    }

    /// <summary>
    /// Draws the compensated profile as colored filled rectangles per slice.
    /// Each slice is drawn at its compensated diameter, filled with a color
    /// representing its required density (blue=low → red=high).
    /// An outline polyline traces the full staircase shape.
    /// </summary>
    private void RenderCompensatedOverlay(Plot plot, List<(double X, double Y)> sorted)
    {
        if (!_showCompProfile) return;
        if (ProjectSegments.Count == 0) return;
        bool anyComp = ProjectSegments.Any(s => s.HasCompensation);
        if (!anyComp) return;

        // Collect all slices to find density range for color normalization
        var allSlices = new List<(double x0, double x1, double r, double density)>();
        foreach (var seg in ProjectSegments.OrderBy(s => s.StartCm))
        {
            if (!seg.HasCompensation) continue;
            double[] xs   = seg.CompSliceXsCm;
            double[] diam = seg.CompSliceDiamsMm;
            double[] dens = seg.CompSliceDensities;
            int n = xs.Length;
            double half = n > 1 ? (xs[1] - xs[0]) / 2.0 : seg.LengthCm / 2.0;
            for (int i = 0; i < n; i++)
            {
                double xAbs = seg.StartCm + xs[i];
                allSlices.Add((xAbs - half, xAbs + half, diam[i] / 2.0, dens[i]));
            }
        }
        if (allSlices.Count == 0) return;

        double minDens = allSlices.Min(s => s.density);
        double maxDens = allSlices.Max(s => s.density);
        double range   = maxDens - minDens;
        if (range < 1e-9) range = 1.0;

        // Draw filled rectangles colored by density
        foreach (var (x0, x1, r, dens) in allSlices)
        {
            double t = (dens - minDens) / range;
            var fillColor = DensityColor(t).WithAlpha(0.55f);

            var poly = plot.Add.Polygon(new ScottPlot.Coordinates[]
            {
                new(x0,  r),
                new(x1,  r),
                new(x1, -r),
                new(x0, -r),
            });
            poly.FillColor  = fillColor;
            poly.LineWidth  = 0;
            poly.LineColor  = ScottPlot.Colors.Transparent;
        }

        // Draw outline staircase on top
        var topXs = new List<double>();
        var topYs = new List<double>();
        var botXs = new List<double>();
        var botYs = new List<double>();

        foreach (var (x0, x1, r, _) in allSlices)
        {
            if (topXs.Count == 0)
            {
                topXs.Add(x0); topYs.Add(0);
                topXs.Add(x0); topYs.Add(r);
                botXs.Add(x0); botYs.Add(0);
                botXs.Add(x0); botYs.Add(-r);
            }
            else
            {
                double prevR = topYs.Last();
                topXs.Add(x0); topYs.Add(prevR);
                topXs.Add(x0); topYs.Add(r);
                botXs.Add(x0); botYs.Add(-prevR);
                botXs.Add(x0); botYs.Add(-r);
            }
            topXs.Add(x1); topYs.Add(r);
            botXs.Add(x1); botYs.Add(-r);
        }
        topXs.Add(topXs.Last()); topYs.Add(0);
        botXs.Add(botXs.Last()); botYs.Add(0);

        var outlineColor = new ScottColor(255, 140, 0);
        var topLine = plot.Add.Scatter(topXs.ToArray(), topYs.ToArray());
        topLine.Color      = outlineColor;
        topLine.LineWidth  = 2;
        topLine.MarkerSize = 0;
        topLine.LegendText = $"Comp. {_compTargetSpeedIns:0.00} in/s";

        var botLine = plot.Add.Scatter(botXs.ToArray(), botYs.ToArray());
        botLine.Color      = outlineColor;
        botLine.LineWidth  = 2;
        botLine.MarkerSize = 0;

        // 4-stop gradient legend
        for (int stop = 0; stop < 4; stop++)
        {
            double t    = stop / 3.0;
            double dens = minDens + t * (maxDens - minDens);
            var entry   = plot.Add.Scatter(new double[] {}, new double[] {});
            entry.Color      = DensityColor(t);
            entry.LineWidth  = 8;
            entry.LegendText = $"ρ {dens:0.000} g/cm³";
        }

        // Node labels at each segment boundary
        var segsOrdered = ProjectSegments.OrderBy(s => s.StartCm).Where(s => s.HasCompensation).ToList();
        var labeledXs   = new HashSet<double>();
        foreach (var seg in segsOrdered)
        {
            // Start boundary
            if (!labeledXs.Contains(seg.StartCm))
            {
                labeledXs.Add(seg.StartCm);
                double d = seg.CompSliceDiamsMm[0];
                double r = d / 2.0;
                var lbl = plot.Add.Text($"Ø {d:0.000} mm\n{seg.StartCm:0.0} cm", seg.StartCm, r);
                lbl.LabelFontSize        = 10;
                lbl.LabelBold            = true;
                lbl.LabelFontColor       = ScottPlot.Colors.Orange;
                lbl.LabelAlignment       = Alignment.LowerLeft;
                lbl.LabelBackgroundColor = Colors.Black.WithAlpha(0.75f);
                lbl.LabelBorderColor     = ScottPlot.Colors.Orange;
                lbl.LabelBorderWidth     = 1.5f;
                lbl.LabelPadding         = 3;
                lbl.OffsetX              = 6;
                lbl.OffsetY              = -6;
            }
            // End boundary
            if (!labeledXs.Contains(seg.EndCm))
            {
                labeledXs.Add(seg.EndCm);
                double d = seg.CompSliceDiamsMm[seg.CompSliceDiamsMm.Length - 1];
                double r = d / 2.0;
                var lbl = plot.Add.Text($"Ø {d:0.000} mm\n{seg.EndCm:0.0} cm", seg.EndCm, r);
                lbl.LabelFontSize        = 10;
                lbl.LabelBold            = true;
                lbl.LabelFontColor       = ScottPlot.Colors.Orange;
                lbl.LabelAlignment       = Alignment.LowerLeft;
                lbl.LabelBackgroundColor = Colors.Black.WithAlpha(0.75f);
                lbl.LabelBorderColor     = ScottPlot.Colors.Orange;
                lbl.LabelBorderWidth     = 1.5f;
                lbl.LabelPadding         = 3;
                lbl.OffsetX              = 6;
                lbl.OffsetY              = -6;
            }
        }
    }

    /// <summary>Density color ramp: blue (low) → cyan → green → yellow → red (high).</summary>
    private static ScottColor DensityColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r, g, b;
        if (t < 0.25)      { double s = t / 0.25;       r = 0;         g = s;         b = 1; }
        else if (t < 0.5)  { double s = (t-0.25)/0.25;  r = 0;         g = 1;         b = 1-s; }
        else if (t < 0.75) { double s = (t-0.5)/0.25;   r = s;         g = 1;         b = 0; }
        else               { double s = (t-0.75)/0.25;  r = 1;         g = 1-s;       b = 0; }
        return new ScottColor((byte)(r*255), (byte)(g*255), (byte)(b*255));
    }

    /// <summary>
    /// Builds ProjectSegments from the current sorted node list and refreshes the
    /// bound DataGrid + the total-volume label.
    /// </summary>
    private void RefreshSegmentTable()
    {
        // Save current user edits (name, spec weight, head flag) and unsubscribe events
        foreach (var seg in ProjectSegments)
        {
            _segmentMetadata[(seg.StartCm, seg.EndCm)] = (seg.Name, seg.SpecWeightGCm3, seg.IsHead);
            seg.PropertyChanged -= OnSegmentPropertyChanged;
        }

        ProjectSegments.Clear();

        var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var seg = new ProjectSegment
            {
                Index           = i + 1,
                StartCm         = sorted[i].X,
                EndCm           = sorted[i + 1].X,
                StartDiameterMm = sorted[i].Y,
                EndDiameterMm   = sorted[i + 1].Y,
            };

            if (_segmentMetadata.TryGetValue((seg.StartCm, seg.EndCm), out var meta))
            {
                seg.Name           = meta.Name;
                seg.SpecWeightGCm3 = _useSharedDensity ? _sharedDensity : meta.SpecWeight;
                seg.IsHead         = meta.IsHead;
            }
            else
            {
                seg.Name           = $"S{i + 1}";
                seg.SpecWeightGCm3 = _useSharedDensity ? _sharedDensity : 0.0;
            }

            // Shooting head: all segments are head by definition
            if (!_isFullLine) seg.IsHead = true;

            seg.PropertyChanged += OnSegmentPropertyChanged;
            ProjectSegments.Add(seg);
        }

        RefreshTotals();
        UpdateSinkingSpeeds();
        RefreshAfftaBadge();
        RefreshAnalysisPlot();

        // Keep the editable node DataGrid in sync
        SyncDesignNodesToList();
    }

    private void OnSegmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectSegment.IsHead) or nameof(ProjectSegment.SpecWeightGCm3))
        {
            Dispatcher.BeginInvoke(() => { RefreshTotals(); UpdateSinkingSpeeds(); RefreshAfftaBadge(); RefreshAnalysisPlot(); });
            MarkDirty();
        }
    }
    private void SyncDesignNodesToList()
    {
        if (_syncingNodes) return;
        _syncingNodes = true;
        try
        {
            DesignNodes.Clear();
            foreach (var n in _segmentNodes.OrderBy(n => n.X))
                DesignNodes.Add(new DesignNode { PositionCm = n.X, DiameterMm = n.Y });
        }
        finally { _syncingNodes = false; }
    }

    /// <summary>Rebuild _segmentNodes from the DesignNodes DataGrid (after user edits a cell).</summary>
    private void SyncListFromDesignNodes()
    {
        _segmentNodes.Clear();
        foreach (var dn in DesignNodes)
            _segmentNodes.Add((Math.Round(dn.PositionCm, 1), Math.Round(dn.DiameterMm, 3)));
    }

    // Add node directly from table — appends a new row with sensible defaults
    // ── Colour sections ─────────────────────────────────────────────────────

    private LineColorSectionVm? _editingColorSection;

    private void SectionColorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn &&
            btn.Tag is LineColorSectionVm sec)
        {
            _editingColorSection = sec;
            SectionHexBox.Text       = sec.ColorHex.TrimStart('#');
            SectionHexBox.Background = System.Windows.Media.Brushes.Transparent;
            SectionColorPopup.IsOpen = true;
            SectionHexBox.Focus();
            SectionHexBox.SelectAll();
        }
    }

    private void SectionSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string hex)
            ApplySectionColor(hex.TrimStart('#'));
    }

    private void ApplySectionHexColor_Click(object sender, RoutedEventArgs e)
        => ApplySectionHexInput();

    private void SectionHexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { ApplySectionHexInput(); e.Handled = true; }
        if (e.Key == Key.Escape) { SectionColorPopup.IsOpen = false; e.Handled = true; }
    }

    private void ApplySectionHexInput()
    {
        string hex = SectionHexBox.Text.Trim().TrimStart('#');
        if (hex.Length == 6 && ApplySectionColor(hex))
            SectionColorPopup.IsOpen = false;
        else
            SectionHexBox.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 220, 220));
    }

    private bool ApplySectionColor(string hex6)
    {
        if (_editingColorSection == null) return false;
        try
        {
            byte r = Convert.ToByte(hex6[0..2], 16);
            byte g = Convert.ToByte(hex6[2..4], 16);
            byte b = Convert.ToByte(hex6[4..6], 16);
            _ = r; _ = g; _ = b; // validate parse
            _editingColorSection.ColorHex = hex6.ToUpperInvariant();
            SectionColorPopup.IsOpen = false;
            // Force DataGrid to update swatch — refresh binding
            ColorSectionsDataGrid.Items.Refresh();
            RefreshPlot();
            MarkDirty();
            return true;
        }
        catch { return false; }
    }

    private void AddColorSection_Click(object sender, RoutedEventArgs e)
    {
        double start = ColorSections.Count > 0 ? ColorSections.Max(s => s.EndCm) : 0.0;
        double end   = start + (_segmentNodes.Count > 0
                       ? (_segmentNodes.Max(n => n.X) - _segmentNodes.Min(n => n.X)) / 3.0
                       : 500.0);
        string[] defaults = { "DC3232", "F5F5F5", "28A428", "3296FF", "FFB41E" };
        string color = defaults[ColorSections.Count % defaults.Length];
        ColorSections.Add(new LineColorSectionVm
        {
            StartCm  = Math.Round(start, 1),
            EndCm    = Math.Round(end,   1),
            ColorHex = color,
            Label    = string.Empty
        });
        MarkDirty();
    }

    private void ColorSectionsDataGrid_CellEditEnding(object sender,
        DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.InvokeAsync(() => { RefreshPlot(); MarkDirty(); });
    }

    private void ColorSectionsDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete &&
            sender is DataGrid dg &&
            dg.SelectedItem is LineColorSectionVm sec)
        {
            ColorSections.Remove(sec);
            RefreshPlot();
            MarkDirty();
            e.Handled = true;
        }
    }

    private void AddNode_Click(object sender, RoutedEventArgs e)
    {
        // Default X = last node position + 100 cm, Y = last node diameter or 1.0 mm
        double newX = DesignNodes.Count > 0
            ? DesignNodes.Max(n => n.PositionCm) + 100.0
            : 0.0;
        double newY = DesignNodes.Count > 0
            ? DesignNodes.OrderByDescending(n => n.PositionCm).First().DiameterMm
            : 1.0;

        var node = new DesignNode { PositionCm = Math.Round(newX, 1), DiameterMm = Math.Round(newY, 3) };
        DesignNodes.Add(node);
        SyncListFromDesignNodes();
        RefreshPlot();
        RefreshSegmentTable();
        MarkDirty();

        // Scroll to and begin editing the new row
        NodesDataGrid.ScrollIntoView(node);
        NodesDataGrid.SelectedItem = node;
        NodesDataGrid.CurrentCell  = new DataGridCellInfo(node, NodesDataGrid.Columns[0]);
        NodesDataGrid.BeginEdit();
    }

    // DataGrid event handlers for the editable nodes grid
    private void NodesDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) return;
        // Defer so DataGrid can commit the edited value before we read it
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SyncListFromDesignNodes();
            RefreshPlot();
            RefreshSegmentTable();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void NodesDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (NodesDataGrid.SelectedItem is not DesignNode dn) return;
        DesignNodes.Remove(dn);
        SyncListFromDesignNodes();
        _segmentUndoStack.Clear(); // undo stack no longer valid after direct delete
        RefreshPlot();
        RefreshSegmentTable();
        e.Handled = true;
    }

    private string _totalVolumeText = string.Empty;
    public string TotalVolumeText
    {
        get => _totalVolumeText;
        set { _totalVolumeText = value; OnPropertyChanged(nameof(TotalVolumeText)); }
    }

    public bool UseSharedDensity
    {
        get => _useSharedDensity;
        set
        {
            _useSharedDensity = value;
            OnPropertyChanged(nameof(UseSharedDensity));
            // Update the column read-only state
            SpWeightColumn.IsReadOnly = value;
            if (value) ApplySharedDensity();
            MarkDirty();
        }
    }

    public double SharedDensity
    {
        get => _sharedDensity;
        set
        {
            _sharedDensity = value;
            OnPropertyChanged(nameof(SharedDensity));
            if (_useSharedDensity) ApplySharedDensity();
            MarkDirty();
        }
    }

    private void ApplySharedDensity()
    {
        foreach (var seg in ProjectSegments)
            seg.SpecWeightGCm3 = _sharedDensity;
        RefreshTotals();
        UpdateSinkingSpeeds();
    }

    // ── Water / sinking speed ────────────────────────────────────────────────

    public bool WaterIsSalt
    {
        get => _waterIsSalt;
        set
        {
            _waterIsSalt = value;
            OnPropertyChanged(nameof(WaterIsSalt));
            UpdateSinkingSpeeds();
            MarkDirty();
        }
    }

    public double WaterTempC
    {
        get => _waterTempC;
        set
        {
            _waterTempC = Math.Clamp(value, 0.0, 40.0);
            OnPropertyChanged(nameof(WaterTempC));
            UpdateSinkingSpeeds();
            MarkDirty();
        }
    }

    private void UpdateSinkingSpeeds()
    {
        foreach (var seg in ProjectSegments)
        {
            if (seg.SpecWeightGCm3 <= 0)
            {
                seg.SinkSpeedMs = double.NaN;
                continue;
            }
            seg.SinkSpeedMs = SinkingSpeedCalc.TaperedSegmentSinkSpeed(
                _waterIsSalt, _waterTempC,
                seg.StartDiameterMm, seg.EndDiameterMm,
                seg.LengthCm,
                seg.SpecWeightGCm3);
        }
    }

    private void ComputeCompensation()
    {
        double targetMs = _compTargetSpeedIns / 39.3701;

        foreach (var seg in ProjectSegments)
        {
            if (seg.SpecWeightGCm3 <= 0 || seg.LengthCm <= 0)
            {
                seg.ClearCompensation();
                continue;
            }

            var (sliceXs, sliceDiams, sliceDens) = SinkingSpeedCalc.CompensateProfile(
                _waterIsSalt, _waterTempC,
                seg.StartDiameterMm, seg.EndDiameterMm,
                seg.LengthCm,
                seg.SpecWeightGCm3,
                targetMs);

            seg.SetCompensation(seg.StartCm, sliceXs, sliceDiams, sliceDens, targetMs);
        }
        RefreshPlot();
        UiStatus = $"Compensation computed for {_compTargetSpeedIns:0.000} in/s";
    }

    // ── Line type / format ────────────────────────────────────────────────────


    private void UpdateLineTypeUI()
    {
        if (!IsLoaded) return;
        // Sinking-only tools visibility
        var sinkVis = _isSinking ? Visibility.Visible : Visibility.Collapsed;
        SinkingToolsPanel.Visibility = sinkVis;
        // Head column visibility (only meaningful for full line)
        HeadColumn.Visibility = _isFullLine ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAfftaBadge() => AfftaBadge = ComputeAfftaBadge();

    private string ComputeAfftaBadge()
    {
        const double target30FtCm  = 914.4;   // 30 ft in cm
        const double gramsToGrains = 15.4324;

        var segs = ProjectSegments.OrderBy(s => s.StartCm).ToList();
        if (segs.Count == 0 || segs.All(s => s.SpecWeightGCm3 <= 0))
            return "AFFTA: —";

        double totalMassG = 0;
        double covered    = 0;
        foreach (var seg in segs)
        {
            if (covered >= target30FtCm || seg.StartCm >= target30FtCm) break;
            double segLen  = seg.LengthCm;
            double usedLen = Math.Min(segLen, target30FtCm - covered);
            double frac    = usedLen / segLen;
            if (seg.SpecWeightGCm3 <= 0) { covered += usedLen; continue; }
            double r1Mm     = seg.StartDiameterMm / 2.0;
            double r2Mm     = seg.EndDiameterMm   / 2.0;
            double r2pMm    = r1Mm + (r2Mm - r1Mm) * frac;
            double lenMm    = usedLen * 10.0;
            double volMm3   = Math.PI * lenMm / 3.0 * (r1Mm*r1Mm + r1Mm*r2pMm + r2pMm*r2pMm);
            totalMassG     += volMm3 / 1000.0 * seg.SpecWeightGCm3;
            covered        += usedLen;
        }

        if (totalMassG <= 0) return "AFFTA: —";

        double grains = totalMassG * gramsToGrains;
        (int lw, double gr)[] targets =
        {
            (1,60),(2,80),(3,100),(4,120),(5,140),(6,160),(7,185),
            (8,210),(9,240),(10,280),(11,330),(12,380),(13,450),(14,500)
        };
        var best  = targets.OrderBy(t => Math.Abs(t.gr - grains)).First();
        bool ok   = Math.Abs(best.gr - grains) <= 6.0;
        return $"AFFTA  LW {best.lw}   {grains:0.0} gr   {(ok ? "✓" : "✗ (target " + best.gr + " gr)")}";
    }

    private void ReverseNodes_Click(object sender, RoutedEventArgs e)
    {
        if (_segmentNodes.Count < 2) return;

        // Capture current metadata before reversing
        var segData = ProjectSegments.OrderBy(s => s.StartCm).ToList();
        double totalLen = _segmentNodes.Max(n => n.X);

        // Mirror node positions, keep diameters
        var mirrored = _segmentNodes
            .Select(n => (Math.Round(totalLen - n.X, 1), n.Y))
            .OrderBy(n => n.Item1)
            .ToList();
        _segmentNodes.Clear();
        foreach (var n in mirrored) _segmentNodes.Add(n);

        // Remap metadata to new (mirrored) segment boundaries
        _segmentMetadata.Clear();
        foreach (var seg in segData)
        {
            double newStart = Math.Round(totalLen - seg.EndCm,   1);
            double newEnd   = Math.Round(totalLen - seg.StartCm, 1);
            _segmentMetadata[(newStart, newEnd)] = (seg.Name, seg.SpecWeightGCm3, seg.IsHead);
        }

        RefreshSegmentTable();
        RefreshPlot();
        MarkDirty();
        UiStatus = "Profile reversed — position 0 is now the fly tip";
    }

    // ── Label hit-test helper ────────────────────────────────────────────────
    /// <summary>Returns the node X whose label screen centre is within LabelHitRadiusPx of pos.</summary>
    private double? FindLabelAt(System.Windows.Point pos)
    {
        if (!_designMode || _segmentNodes.Count == 0) return null;
        var sortedNodes = _segmentNodes.OrderBy(n => n.X).ToList();
        for (int ni = 0; ni < sortedNodes.Count; ni++)
        {
            var node = sortedNodes[ni];
            double gap = node.Y * 0.40;
            double defaultLX = node.X;
            double defaultLY = -node.Y / 2.0 - gap * (ni % 2 == 0 ? 1.0 : 2.2);
            var (lx, ly) = _nodeLabelOffsets.TryGetValue(node.X, out var saved)
                           ? saved : (defaultLX, defaultLY);
            try
            {
                var px = PlotControl.Plot.GetPixel(new ScottPlot.Coordinates(lx, ly));
                double dx = pos.X - px.X;
                double dy = pos.Y - px.Y;
                if (Math.Sqrt(dx*dx + dy*dy) <= LabelHitRadiusPx)
                    return node.X;
            }
            catch { }
        }
        return null;
    }

    // Segment drawing mouse handlers
    private void PlotControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // ── Label drag (design mode, any sub-mode) ──────────────────────────
        if (_designMode)
        {
            var labelHit = FindLabelAt(e.GetPosition(PlotControl));
            if (labelHit.HasValue)
            {
                _draggingLabelNodeX  = labelHit.Value;
                _labelDragStartMouse = e.GetPosition(PlotControl);
                var sn   = _segmentNodes.OrderBy(n => n.X).ToList();
                int sni  = sn.FindIndex(n => n.X == labelHit.Value);
                var snod = sn[Math.Max(0,sni)];
                double sg = snod.Y * 0.40;
                _labelDragStartOffset = _nodeLabelOffsets.TryGetValue(labelHit.Value, out var cur)
                    ? cur
                    : (snod.X, -snod.Y/2.0 - sg*(sni%2==0 ? 1.0 : 2.2));
                PlotControl.CaptureMouse();
                UiStatus   = $"Drag label for node at {labelHit.Value:0} cm";
                e.Handled  = true;
                return;
            }
        }

        if (!_segmentDrawMode) return;

        var pos    = e.GetPosition(PlotControl);
        var coords = PlotControl.Plot.GetCoordinates(new Pixel((float)pos.X, (float)pos.Y));

        // Check if click is within DragHitRadiusPx of an existing node → start drag
        var hit = FindNearestNode(pos);
        if (hit.HasValue)
        {
            _draggingNodeX = hit.Value.X;
            PlotControl.CaptureMouse();
            UiStatus = $"Drag node at {hit.Value.X:0} cm";
            e.Handled = true;
            return;
        }

        // No nearby node → add new node
        // coords.Y is in radius (chart Y = diameter/2), so diameter = coords.Y * 2
        double snappedX  = Math.Round(coords.X);
        double diameter;

        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
        {
            // Shift: lock diameter to nearest existing node → level (cylinder) segment
            var anchor = _segmentNodes
                .OrderBy(n => Math.Abs(n.X - snappedX))
                .FirstOrDefault();
            diameter = anchor == default
                ? Math.Round(Math.Abs(coords.Y) * 2.0, 3)
                : anchor.Y;
        }
        else
        {
            diameter = Math.Round(Math.Abs(coords.Y) * 2.0, 3);
        }

        _segmentNodes.RemoveAll(n => n.X == snappedX);
        _segmentNodes.Add((snappedX, diameter));
        _segmentUndoStack.Push(snappedX);

        RefreshPlot();
        FitAfterRefresh();
        RefreshSegmentTable();
        MarkDirty();
        var hint = (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                   ? "  [SHIFT — level]" : string.Empty;
        UiStatus = $"Node added: {snappedX:0} cm  Ø {diameter:0.000} mm  ({_segmentNodes.Count} nodes){hint}";
        e.Handled = true;
    }

    private void PlotControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var pos    = e.GetPosition(PlotControl);
        var coords = PlotControl.Plot.GetCoordinates(new Pixel((float)pos.X, (float)pos.Y));

        // Always update hover readout from nearest raw data point
        UpdateHoverCoords(coords.X);

        // ── Label drag ──────────────────────────────────────────────────────
        if (_draggingLabelNodeX.HasValue)
        {
            var curPos = e.GetPosition(PlotControl);
            try
            {
                var startData = PlotControl.Plot.GetCoordinates(
                    new ScottPlot.Pixel((float)_labelDragStartMouse.X, (float)_labelDragStartMouse.Y));
                var curData = PlotControl.Plot.GetCoordinates(
                    new ScottPlot.Pixel((float)curPos.X, (float)curPos.Y));
                double dx = curData.X - startData.X;
                double dy = curData.Y - startData.Y;
                _nodeLabelOffsets[_draggingLabelNodeX.Value] =
                    (_labelDragStartOffset.LX + dx, _labelDragStartOffset.LY + dy);
                RefreshPlot();
            }
            catch { }
            e.Handled = true;
            return;
        }

        if (!_segmentDrawMode || _draggingNodeX == null) return;

        double newX        = Math.Round(coords.X);
        double newDiameter = Math.Round(Math.Abs(coords.Y) * 2.0, 3);

        // Update the dragged node in-place
        _segmentNodes.RemoveAll(n => n.X == _draggingNodeX.Value);
        _segmentNodes.RemoveAll(n => n.X == newX);
        _segmentNodes.Add((newX, newDiameter));
        _draggingNodeX = newX;

        RefreshPlot();
        FitAfterRefresh();
        RefreshSegmentTable();
        UiStatus = $"Node: {newX:0} cm  Ø {newDiameter:0.000} mm";
        e.Handled = true;
    }

    private void UpdateHoverCoords(double plotX)
    {
        var pts = _vm.Points;
        if (pts.Count == 0) { HoverCoordsStatus = string.Empty; return; }

        var orderedPoints = pts.OrderBy(p => p.X).ToList();
        var displayedYs = GetDisplayedSeries(orderedPoints);
        int nearestIndex = orderedPoints
            .Select((point, index) => new { point, index })
            .OrderBy(item => Math.Abs(item.point.X - plotX))
            .First()
            .index;

        HoverCoordsStatus = $"Ø {displayedYs[nearestIndex]:0.000} mm  @  {orderedPoints[nearestIndex].X:0.0} cm";
    }

    private void PlotControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // ── End label drag ──────────────────────────────────────────────────
        if (_draggingLabelNodeX.HasValue)
        {
            PlotControl.ReleaseMouseCapture();
            _draggingLabelNodeX = null;
            MarkDirty();
            e.Handled = true;
            return;
        }

        if (_draggingNodeX == null) return;
        PlotControl.ReleaseMouseCapture();
        RefreshSegmentTable();
        MarkDirty();
        UiStatus = $"Node placed at {_draggingNodeX.Value:0} cm  ({_segmentNodes.Count} nodes total)";
        _draggingNodeX = null;
        e.Handled = true;
    }

    private void PlotControl_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_segmentDrawMode) return;
        if (_segmentNodes.Count == 0) return;

        var pos    = e.GetPosition(PlotControl);
        var coords = PlotControl.Plot.GetCoordinates(new Pixel((float)pos.X, (float)pos.Y));

        var closest = _segmentNodes.OrderBy(n => Math.Abs(n.X - coords.X)).First();
        _segmentNodes.Remove(closest);

        RefreshPlot();
        RefreshSegmentTable();
        MarkDirty();
        UiStatus = $"Node removed at {closest.X:0} cm  ({_segmentNodes.Count} remaining)";
        e.Handled = true;
    }

    // Segment menu handlers
    private void ToggleDrawMode_Click(object sender, RoutedEventArgs e)
    {
        _segmentDrawMode = MenuDrawSegments.IsChecked ?? false;
        PlotControl.Cursor = _segmentDrawMode ? Cursors.Cross : Cursors.Arrow;
        UiStatus = _segmentDrawMode
            ? "Draw mode ON  —  left click = add node,  right click = remove node,  SHIFT = level segment"
            : "Draw mode OFF";
        RefreshStatusBar();
    }

    private void UndoLastNode_Click(object sender, RoutedEventArgs e)
    {
        if (_segmentUndoStack.Count == 0) return;
        double lastX = _segmentUndoStack.Pop();
        _segmentNodes.RemoveAll(n => n.X == lastX);
        RefreshPlot();
        RefreshSegmentTable();
        MarkDirty();
        UiStatus = $"Undone node at {lastX:0} cm  ({_segmentNodes.Count} remaining)";
    }

    private void ClearSegments_Click(object sender, RoutedEventArgs e)
    {
        _segmentNodes.Clear();
        _segmentUndoStack.Clear();
        ProjectSegments.Clear();
        DesignNodes.Clear();
        TotalVolumeText = string.Empty;
        RefreshPlot();
        MarkDirty();
        UiStatus = "Design cleared";
    }

    // ── Profile colour picker ────────────────────────────────────────────────
    private void ProfileColorBtn_Click(object sender, RoutedEventArgs e)
    {
        // Pre-fill hex box with current colour
        HexColorBox.Text = $"{_designLineColor.R:X2}{_designLineColor.G:X2}{_designLineColor.B:X2}";
        ColorPickerPopup.IsOpen = true;
        HexColorBox.Focus();
        HexColorBox.SelectAll();
    }

    private void ApplyHexColor_Click(object sender, RoutedEventArgs e) => ApplyHexInput();

    private void HexColorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyHexInput(); e.Handled = true; }
        if (e.Key == Key.Escape) { ColorPickerPopup.IsOpen = false; e.Handled = true; }
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string hex)
        {
            ApplyDesignColor(hex.TrimStart('#'));
            ColorPickerPopup.IsOpen = false;
        }
    }

    private void ApplyHexInput()
    {
        string hex = HexColorBox.Text.Trim().TrimStart('#');
        if (hex.Length == 6 && ApplyDesignColor(hex))
            ColorPickerPopup.IsOpen = false;
        else
            HexColorBox.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 220, 220));
    }

    private bool ApplyDesignColor(string hex6)
    {
        try
        {
            byte r = Convert.ToByte(hex6[0..2], 16);
            byte g = Convert.ToByte(hex6[2..4], 16);
            byte b = Convert.ToByte(hex6[4..6], 16);
            _designLineColor = new ScottColor(r, g, b);
            ProfileColorBtn.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(r, g, b));
            HexColorBox.Background = System.Windows.Media.Brushes.Transparent;
            RefreshPlot();
            MarkDirty();
            return true;
        }
        catch { return false; }
    }

    // ── PDF export ──────────────────────────────────────────────────────────
    /// <summary>
    /// Renders a clean chart image for PDF: thinner profile lines, no legend box.
    /// </summary>
    private byte[] RenderPdfChart()
    {
        var plot = new ScottPlot.Plot();
        plot.FigureBackground.Color = ScottPlot.Colors.White;
        plot.DataBackground.Color   = ScottPlot.Colors.White;
        plot.Axes.Color(new ScottColor(80, 80, 80));

        var pts = _vm.Points.OrderBy(p => p.X).ToList();

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

        // Design overlay — thinner lines for print
        if (_segmentNodes.Count >= 2)
        {
            var sorted     = _segmentNodes.OrderBy(n => n.X).ToList();
            double[] xs    = sorted.Select(n => n.X).ToArray();
            double[] topYs = sorted.Select(n =>  n.Y / 2.0).ToArray();
            double[] botYs = sorted.Select(n => -n.Y / 2.0).ToArray();
            var dc = _designLineColor;

            if (ColorSections.Count == 0)
            {
                // No colour sections: use the normal 3D shaded fill
                DrawLineFill(plot, xs, topYs, botYs, dc, solid: true);
            }
            else
            {
                // Design-colour Lambert base (whole profile), then each section
                // gets its own full Lambert shading drawn on top.
                DrawLineFill(plot, xs, topYs, botYs, dc, solid: true);
                foreach (var sec in ColorSections)
                {
                    if (sec.EndCm <= sec.StartCm) continue;
                    if (!TryParseHexColor(sec.ColorHex, out var secColor)) continue;
                    var pts2 = new List<(double X, double Y)>
                        { (sec.StartCm, InterpolateProfileY(sorted, sec.StartCm)) };
                    foreach (var n in sorted.Where(n => n.X > sec.StartCm && n.X < sec.EndCm))
                        pts2.Add(n);
                    pts2.Add((sec.EndCm, InterpolateProfileY(sorted, sec.EndCm)));
                    double[] sxs  = pts2.Select(p => p.X).ToArray();
                    double[] stop = pts2.Select(p =>  p.Y / 2.0).ToArray();
                    double[] sbot = pts2.Select(p => -p.Y / 2.0).ToArray();
                    DrawLineFill(plot, sxs, stop, sbot, secColor, solid: true);
                }
            }

            var tl = plot.Add.Scatter(xs, topYs); tl.Color = dc; tl.LineWidth = 1.2f; tl.MarkerSize = 0;
            var bl = plot.Add.Scatter(xs, botYs); bl.Color = dc; bl.LineWidth = 1.2f; bl.MarkerSize = 0;

            // Node labels + leaders (same logic as live chart)
            var leaderColor = new ScottColor(100, 100, 100);
            for (int ni = 0; ni < sorted.Count; ni++)
            {
                var node         = sorted[ni];
                double chartYBot = -node.Y / 2.0;
                double gap       = node.Y * 0.40;
                double defaultLX = node.X;
                double defaultLY = chartYBot - gap * (ni % 2 == 0 ? 1.0 : 2.2);
                var (lx, ly)     = _nodeLabelOffsets.TryGetValue(node.X, out var saved)
                                   ? saved : (defaultLX, defaultLY);

                var leader = plot.Add.Scatter(
                    new double[] { node.X, lx },
                    new double[] { chartYBot * 0.85, ly });
                leader.Color      = leaderColor;
                leader.LineWidth  = 1.0f;
                leader.MarkerSize = 0;

                var lbl = plot.Add.Text($"Ø {node.Y:0.000}  {node.X:0.0} cm", lx, ly);
                lbl.LabelFontSize        = 11;
                lbl.LabelBold            = true;
                lbl.LabelFontColor       = new ScottColor(50, 50, 50);
                lbl.LabelAlignment       = Alignment.UpperCenter;
                lbl.LabelBackgroundColor = ScottPlot.Colors.White.WithAlpha(0.95f);
                lbl.LabelBorderColor     = leaderColor;
                lbl.LabelBorderWidth     = 1f;
                lbl.LabelPadding         = 3;
                lbl.OffsetX              = 0;
                lbl.OffsetY              = 0;
            }
        }

        plot.XLabel("Length (cm)");
        plot.YLabel("Diameter (mm)");
        plot.Axes.AutoScale();
        var yRange = plot.Axes.GetLimits().Rect.Height;
        var lim    = plot.Axes.GetLimits();
        plot.Axes.SetLimitsY(lim.Bottom - yRange * 0.12, lim.Top);

        return plot.GetImage(1600, 420).GetImageBytes(ScottPlot.ImageFormat.Png);
    }

    private void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectSegments.Count == 0)
        {
            MessageBox.Show("No design segments to export.", "Export PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter      = "PDF files (*.pdf)|*.pdf",
            DefaultExt  = ".pdf",
            FileName    = $"{_projectName}_design.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var pdfSections = ColorSections
                .Select(s => new LineColorSection
                    { StartCm = s.StartCm, EndCm = s.EndCm, ColorHex = s.ColorHex, Label = s.Label })
                .ToList();
            FlyLinePdfExporter.Export(dlg.FileName, _projectName, RenderPdfChart(), ProjectSegments.ToList(),
                _isSinking, _isFullLine, _waterIsSalt, _waterTempC, AfftaBadge, _colorNote, pdfSections);
            UiStatus = $"PDF exported: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PDF export failed:\n{ex.Message}", "Export PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Export segments: full segment table with geometry
    private void ExportSegmentsCsv_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectSegments.Count == 0)
        {
            MessageBox.Show("No segments to export. Add at least two nodes.",
                            "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter     = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName   = "flyline_design.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Segment,Start cm,End cm,Length cm,Start dia mm,End dia mm,Shape,Taper mm/m,Volume cm3");
        foreach (var seg in ProjectSegments)
            sb.AppendLine(FormattableString.Invariant(
                $"{seg.Index},{seg.StartCm:0.0},{seg.EndCm:0.0},{seg.LengthCm:0.0},{seg.StartDiameterMm:0.000},{seg.EndDiameterMm:0.000},{seg.Shape},{seg.TaperMmPerMeter:0.000},{seg.VolumeMm3 / 1000.0:0.00}"));

        double totalCm3 = ProjectSegments.Sum(s => s.VolumeMm3) / 1000.0;
        sb.AppendLine(FormattableString.Invariant($"TOTAL,,,,,,,,{totalCm3:0.00}"));

        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        UiStatus = $"Design exported: {ProjectSegments.Count} segments";
        MessageBox.Show($"Exported {ProjectSegments.Count} segments.\n{dlg.FileName}", "Export Design");
    }

    private void SaveNodes_Click(object sender, RoutedEventArgs e)
    {
        if (_segmentNodes.Count == 0)
        {
            MessageBox.Show("No nodes to save.", "Warning",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter     = "Node files (*.nodes.csv)|*.nodes.csv|CSV files (*.csv)|*.csv",
            DefaultExt = ".nodes.csv",
            FileName   = "flyline_nodes.nodes.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Position cm,Diameter mm");
        foreach (var node in sorted)
            sb.AppendLine(FormattableString.Invariant($"{node.X:0.0},{node.Y:0.000}"));

        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        UiStatus = $"Nodes saved ({sorted.Count})";
    }

    private void LoadNodes_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Node files (*.nodes.csv;*.csv)|*.nodes.csv;*.csv",
            Title  = "Load design nodes"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8)
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .Skip(1)  // skip header
                            .ToList();

            var loaded = new List<(double X, double Y)>();

            foreach (var line in lines)
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    loaded.Add((x, y));
            }

            if (loaded.Count == 0)
            {
                MessageBox.Show("No valid nodes found in file.", "Warning",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_segmentNodes.Count > 0)
            {
                var res = MessageBox.Show(
                    $"Replace the {_segmentNodes.Count} existing nodes with the {loaded.Count} loaded nodes?",
                    "Load Nodes", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }

            _segmentNodes.Clear();
            _segmentUndoStack.Clear();
            _segmentNodes.AddRange(loaded);

            RefreshPlot();
            FitAfterRefresh();
            RefreshSegmentTable();
            MarkDirty();
            UiStatus = $"Loaded {loaded.Count} nodes from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error reading file:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private (double X, double Y)? FindNearestNode(System.Windows.Point screenPos)
    {
        (double X, double Y)? best = null;
        double bestDist = DragHitRadiusPx;

        foreach (var node in _segmentNodes)
        {
            // node.Y is diameter; chart plots it at radius = Y/2
            Pixel nodePx = PlotControl.Plot.GetPixel(new Coordinates(node.X, node.Y / 2.0));
            double dx = nodePx.X - screenPos.X;
            double dy = nodePx.Y - screenPos.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }
        return best;
    }

    // Double-click a node to edit its coordinates directly
    private void PlotControl_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var pos = e.GetPosition(PlotControl);
        var hit = FindNearestNode(pos);
        if (!hit.HasValue) return;

        // Cancel any drag that started on the first click of the double-click
        if (_draggingNodeX != null)
        {
            PlotControl.ReleaseMouseCapture();
            _draggingNodeX = null;
        }

        var result = ShowNodeEditDialog(hit.Value.X, hit.Value.Y);
        if (result == null) { e.Handled = true; return; }

        _segmentNodes.RemoveAll(n => n.X == hit.Value.X);
        _segmentNodes.RemoveAll(n => n.X == result.Value.cm);
        _segmentNodes.Add((result.Value.cm, result.Value.mm));

        RefreshPlot();
        RefreshSegmentTable();
        UiStatus = $"Node edited: {result.Value.cm:0.0} cm  Ø {result.Value.mm:0.000} mm  ({_segmentNodes.Count} nodes)";
        e.Handled = true;
    }

    // Small modal dialog for editing a node's coordinates
    private (double cm, double mm)? ShowNodeEditDialog(double currentCm, double currentMm)
    {
        var win = new Window
        {
            Title  = "Edit Node",
            Width  = 260,
            Height = 145,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner       = this,
            ResizeMode  = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var addLabel = (string text, int row) =>
        {
            var lbl = new System.Windows.Controls.Label
            {
                Content = text,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Padding = new Thickness(0, 2, 8, 2)
            };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);
        };
        var addBox = (string text, int row) =>
        {
            var tb = new TextBox
            {
                Text = text,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
            return tb;
        };

        addLabel("Position (cm):", 0);
        var txtCm = addBox(currentCm.ToString("0.0", CultureInfo.InvariantCulture), 0);

        addLabel("Diameter (mm):", 1);
        var txtMm = addBox(currentMm.ToString("0.000", CultureInfo.InvariantCulture), 1);

        var btnPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var btnOk     = new Button { Content = "OK",     Width = 64, IsDefault = true,
                                     Margin = new Thickness(0, 0, 6, 0) };
        var btnCancel = new Button { Content = "Cancel", Width = 64, IsCancel = true };
        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        Grid.SetRow(btnPanel, 3); Grid.SetColumnSpan(btnPanel, 2);
        grid.Children.Add(btnPanel);

        win.Content = grid;

        bool confirmed = false;
        btnOk.Click     += (_, _) => { confirmed = true; win.Close(); };
        btnCancel.Click += (_, _) => win.Close();

        win.Loaded += (_, _) => { txtCm.Focus(); txtCm.SelectAll(); };
        win.ShowDialog();

        if (!confirmed) return null;

        if (!double.TryParse(txtCm.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cm) ||
            !double.TryParse(txtMm.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mm))
        {
            MessageBox.Show("Invalid values.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        return (Math.Round(cm, 1), Math.Round(mm, 3));
    }

    private static double InterpolateSegment(List<(double X, double Y)> nodes, double x)
    {
        if (x <= nodes[0].X)  return nodes[0].Y;
        if (x >= nodes[^1].X) return nodes[^1].Y;
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            if (x >= nodes[i].X && x <= nodes[i + 1].X)
            {
                double t = (x - nodes[i].X) / (nodes[i + 1].X - nodes[i].X);
                return nodes[i].Y + t * (nodes[i + 1].Y - nodes[i].Y);
            }
        }
        return nodes[^1].Y;
    }

    private async Task Send(string cmd) => await _vm.SendCommandAsync(cmd);

    // Connection
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        UiStatus = "Connecting...";
        await _vm.ConnectAsync(initiatedByUser: true);
        UiStatus = "Connection requested";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.DisconnectAsync();
        UiStatus = "Disconnected";
    }

    // Motor / scan commands
    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor scan");
        await Send("scan_on");
        UiStatus = "SCAN sent";
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor stop");
        await Send("scan_off");
        UiStatus = "STOP sent";
    }

    private async void ScanOn_Click(object sender, RoutedEventArgs e)
    {
        await Send("scan_on");
        UiStatus = "Receiving ON";
    }

    private async void ScanOff_Click(object sender, RoutedEventArgs e)
    {
        await Send("scan_off");
        UiStatus = "Receiving OFF";
    }

    private async void FastS_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor fast_s");
        UiStatus = "FAST same direction";
    }

    private async void FastO_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor fast_o");
        UiStatus = "FAST opposite direction";
    }

    private async void MotorStatus_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor status");
        UiStatus = "Motor status requested";
    }

    // Tools
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        await Send("reset");
        _vm.ClearAllData();
        MarkDirty();
        UiStatus = "Position zeroed and chart cleared";
    }

    private async void ReadRaw_Click(object sender, RoutedEventArgs e)
    {
        await Send("readraw");
        _vm.AppendLog("Note: readraw response appears on the device serial console");
        UiStatus = "Raw read requested";
    }

    private async void SetDisplayZero_Click(object sender, RoutedEventArgs e)
    {
        await Send("setdisplayzero");
        UiStatus = "Display zero set";
    }

    private async void ResetOffset_Click(object sender, RoutedEventArgs e)
    {
        await Send("resetoffset");
        UiStatus = "Offset reset";
    }

    private async void SetOffset_Click(object sender, RoutedEventArgs e)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            "Enter offset value in mm (use dot as decimal separator):",
            "Set Offset",
            "0.00");

        if (string.IsNullOrWhiteSpace(input)) return;

        if (TryParseDouble(input, out var offset))
        {
            await Send($"setoffset {offset.ToString("0.000", CultureInfo.InvariantCulture)}");
            UiStatus = $"Offset set: {offset:0.000} mm";
        }
        else
        {
            MessageBox.Show("Invalid value. Use dot as decimal separator (e.g. 0.25).",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // GOTOPOS
    private async void Goto_Click(object sender, RoutedEventArgs e)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            "Enter target position in cm:",
            "Go To Position",
            "150");

        if (TryParseDouble(input, out var cm) && cm >= 0)
        {
            await Send($"goto {cm.ToString("0.0", CultureInfo.InvariantCulture)}");
            UiStatus = $"Goto requested: {cm:0.0} cm";
        }
    }

    // CSV import / export
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter      = "CSV files (*.csv)|*.csv",
            DefaultExt  = ".csv",
            FileName    = "flyline_scan_export.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Lunghezza cm,Diametro mm");
        var orderedPoints = _vm.Points.OrderBy(p => p.X).ToList();
        var displayedYs = GetDisplayedSeries(orderedPoints);
        for (int i = 0; i < orderedPoints.Count; i++)
        {
            var p = orderedPoints[i];
            sb.AppendLine(FormattableString.Invariant($"{p.X:0.0},{displayedYs[i]:0.000}"));
        }

        File.WriteAllText(dlg.FileName, sb.ToString());
        UiStatus = "CSV exported";
        MessageBox.Show($"CSV exported to:\n{dlg.FileName}");
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter     = "PNG files (*.png)|*.png",
            DefaultExt = ".png",
            FileName   = "flyline_plot.png"
        };
        if (dlg.ShowDialog() != true) return;

        PlotControl.Plot.SavePng(dlg.FileName, 1400, 800);
        UiStatus = "PNG exported";
        MessageBox.Show($"PNG exported to:\n{dlg.FileName}");
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter      = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = false,
            Title       = "Import comparison CSV"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var series = LoadImportedSeries(dlg.FileName);
            _importedSeries.Add(series);
            _lastImportedFile = Path.GetFileName(dlg.FileName);
            UiStatus = $"CSV imported: {series.Name} ({series.Xs.Length} points)";
            RefreshPlot();
            FitAfterRefresh();
            MarkDirty();
            MessageBox.Show($"CSV imported: {series.Name} ({series.Xs.Length} points)", "Import CSV");
        }
        catch (Exception ex)
        {
            UiStatus = "CSV import error";
            MessageBox.Show("CSV import error:\n" + ex.Message, "Error");
        }
    }

    // Chart controls
    private void FitPlot_Click(object sender, RoutedEventArgs e)
    {
        PlotControl.Plot.Axes.AutoScale();
        PlotControl.Refresh();
        UiStatus = "Axes fitted";
    }

    private void ClearImported_Click(object sender, RoutedEventArgs e)
    {
        _importedSeries.Clear();
        _lastImportedFile = "-";
        RefreshPlot();
        MarkDirty();
        UiStatus = "Imported series cleared";
    }

    private void ShowScanBtn_Click(object sender, RoutedEventArgs e)
    {
        SetShowScanLayer(!_showScanLayer, updateToggle: true);
        RefreshPlot();
        UiStatus = _showScanLayer ? "Scan layer visible" : "Scan layer hidden";
    }

    private void ShowDesignLayer_Click(object sender, RoutedEventArgs e)
    {
        _showDesignLayer = ShowDesignToggle.IsChecked ?? true;
        RefreshPlot();
        UiStatus = _showDesignLayer ? "Design layer visible" : "Design layer hidden";
    }

    private void ShowSinkMap_Click(object sender, RoutedEventArgs e)
    {
        _showSinkSpeedMap = SinkMapToggle.IsChecked ?? false;
        RefreshPlot();
        UiStatus = _showSinkSpeedMap ? "Sink speed map ON" : "Sink speed map OFF";
    }

    private void ShowCompProfile_Click(object sender, RoutedEventArgs e)
    {
        _showCompProfile = CompProfileToggle.IsChecked ?? false;
        RefreshPlot();
        UiStatus = _showCompProfile ? "Compensated profile ON" : "Compensated profile OFF";
    }

    private void CompTargetBox_LostFocus(object sender, RoutedEventArgs e) => ApplyCompTarget();
    private void CompTargetBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyCompTarget();
    }
    private void ApplyCompTarget()
    {
        if (double.TryParse(CompTargetBox.Text,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out double v) && v > 0)
        {
            _compTargetSpeedIns = v;
            CompTargetBox.BorderBrush = null; // restore default
            double cms = v * 2.54;
            string cls = v switch
            {
                < 1.25 => "very slow",
                < 2.00 => "Class I",
                < 3.00 => "Class II",
                < 3.50 => "Class III",
                < 4.50 => "Class IV",
                < 6.00 => "Class V",
                < 8.00 => "Class VI",
                _      => "Class VII"
            };
            CompTargetCmsLabel.Text = $"≈ {cms:0.0} cm/s  ({cls})";
        }
        else
        {
            CompTargetBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E85454"));
            CompTargetBox.Text = _compTargetSpeedIns.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void Compensate_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectSegments.Count == 0)
        {
            UiStatus = "No segments to compensate — draw nodes first";
            return;
        }
        if (ProjectSegments.All(s => s.SpecWeightGCm3 <= 0))
        {
            UiStatus = "Set density (g/cm³) before computing compensation";
            return;
        }
        ApplyCompTarget();
        ComputeCompensation();
        MarkDirty();
    }

    private void AutoFitToggle_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = AutoFitToggle.IsChecked ?? true;
        _autoFitEnabled = isChecked;
        _vm.Settings.Chart.AutoFit = _autoFitEnabled;
        _vm.SaveSettings();
        RefreshPlot();
        UiStatus = _autoFitEnabled ? "Auto-fit ON" : "Auto-fit OFF";
    }

    private void SmoothingToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = SmoothingToggle.IsChecked ?? true;
        _vm.SmoothingEnabled = enabled;
        RefreshPlot();
        UiStatus = enabled ? "Smoothing EMA ON" : "Smoothing EMA OFF";
    }

    private void SmoothingAlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_chartControlsInitialized || !IsLoaded)
            return;

        double alpha = Math.Round(e.NewValue, 2);
        if (Math.Abs(_vm.Settings.Chart.SmoothingAlpha - alpha) < 0.0001)
            return;

        _vm.Settings.Chart.SmoothingAlpha = alpha;
        _vm.SaveSettings();
        RefreshStatusBar();
        RefreshPlot();
        UiStatus = $"Smoothing alpha = {alpha:0.00}";
    }

    private void ResetSmoothingAlpha_Click(object sender, RoutedEventArgs e)
    {
        SmoothingAlphaSlider.Value = 0.10;
        UiStatus = "Smoothing alpha reset to 0.10";
    }

    private void LineWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_chartControlsInitialized || !IsLoaded)
            return;

        int lineWidth = (int)Math.Round(e.NewValue);
        if (_vm.Settings.Chart.LineWidth == lineWidth)
            return;

        _vm.Settings.Chart.LineWidth = lineWidth;
        _vm.SaveSettings();
        RefreshStatusBar();
        RefreshPlot();
        UiStatus = $"Line width = {lineWidth}";
    }

    private void FilteredOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_chartControlsInitialized || !IsLoaded)
            return;

        double opacity = Math.Round(e.NewValue, 2);
        if (Math.Abs(_vm.Settings.Chart.FilteredOpacity - opacity) < 0.0001)
            return;

        _vm.Settings.Chart.FilteredOpacity = opacity;
        _vm.SaveSettings();
        RefreshStatusBar();
        RefreshPlot();
        UiStatus = $"Filtered opacity = {opacity:0.00}";
    }

    private void RawOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_chartControlsInitialized || !IsLoaded)
            return;

        double opacity = Math.Round(e.NewValue, 2);
        if (Math.Abs(_vm.Settings.Chart.RawOpacity - opacity) < 0.0001)
            return;

        _vm.Settings.Chart.RawOpacity = opacity;
        _vm.SaveSettings();
        RefreshStatusBar();
        RefreshPlot();
        UiStatus = $"Raw opacity = {opacity:0.00}";
    }

    // Settings
    private void FitView_Click(object sender, RoutedEventArgs e)
    {
        FitAfterRefresh();
    }

    private void SegmentsDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not ProjectSegment seg) return;
        if (e.EditingElement is not TextBox tb) return;

        string text    = tb.Text.Trim();
        int    colIdx  = SegmentsDataGrid.Columns.IndexOf(e.Column);

        // Col 2 = Name, Col 9 = Sp.W. — handled by direct binding, no node sync needed
        if (colIdx == 2 || colIdx == 9)
        {
            // When not in shared-density mode and Sp.W. was edited, apply to just this segment
            if (colIdx == 9 && !_useSharedDensity && TryParseDouble(text, out double spw) && spw >= 0)
                seg.SpecWeightGCm3 = spw;
            Dispatcher.BeginInvoke(RefreshTotals);
            Dispatcher.BeginInvoke((Action)MarkDirty);
            return;
        }

        // Col 3 = Start Ø, Col 4 = End Ø, Col 5 = Length
        if (colIdx < 3 || colIdx > 5) return;
        if (!TryParseDouble(text, out double newVal) || newVal <= 0) return;

        Dispatcher.BeginInvoke(() =>
        {
            switch (colIdx)
            {
                case 3: // Start Ø — change the Y of the start node
                {
                    int idx = _segmentNodes.FindIndex(n => Math.Abs(n.X - seg.StartCm) < 0.05);
                    if (idx >= 0) _segmentNodes[idx] = (_segmentNodes[idx].X, newVal);
                    break;
                }
                case 4: // End Ø — change the Y of the end node (shared with next segment's start)
                {
                    int idx = _segmentNodes.FindIndex(n => Math.Abs(n.X - seg.EndCm) < 0.05);
                    if (idx >= 0) _segmentNodes[idx] = (_segmentNodes[idx].X, newVal);
                    break;
                }
                case 5: // Length — move the end node's X position
                {
                    double newEndCm = seg.StartCm + newVal;
                    int idx = _segmentNodes.FindIndex(n => Math.Abs(n.X - seg.EndCm) < 0.05);
                    if (idx >= 0) _segmentNodes[idx] = (Math.Round(newEndCm, 1), _segmentNodes[idx].Y);
                    break;
                }
            }

            RefreshPlot();
            RefreshSegmentTable();
            MarkDirty();
        });
    }

    private void RefreshTotals()
    {
        if (ProjectSegments.Count == 0) { TotalVolumeText = string.Empty; return; }

        double totalVol  = ProjectSegments.Sum(s => s.VolumeCm3);
        bool   hasMass   = ProjectSegments.All(s => s.SpecWeightGCm3 > 0);
        double totalMass = hasMass ? ProjectSegments.Sum(s => s.MassG) : 0;

        var headSegs  = ProjectSegments.Where(s => s.IsHead).ToList();
        bool hasHeads = headSegs.Count > 0;
        double headVol  = hasHeads ? headSegs.Sum(s => s.VolumeCm3) : 0;
        double headMass = hasMass && hasHeads ? headSegs.Sum(s => s.MassG) : 0;

        var sb = new System.Text.StringBuilder();
        sb.Append($"Total: {totalVol:0.00} cm³");
        if (hasMass) sb.Append($"  |  {totalMass:0.00} g");
        if (hasHeads)
        {
            sb.Append($"  |  Head: {headVol:0.00} cm³");
            if (hasMass) sb.Append($" / {headMass:0.00} g");
        }
        sb.Append($"  |  {ProjectSegments.Count} segments");

        TotalVolumeText = sb.ToString();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var copy = _vm.CloneSettings();
        var win  = new SettingsWindow(copy) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _vm.ApplySettings(win.EditableSettings);
            _autoFitEnabled = _vm.Settings.Chart.AutoFit;
            RefreshPlot();
            UiStatus = "Settings saved";
            MessageBox.Show("Settings saved.", "Settings");
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // Log
    private void TxtLog_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChkAutoScroll.IsChecked == true)
            TxtLog.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.LogText = string.Empty;
    }

    // CSV helpers
    private ImportedSeries LoadImportedSeries(string fileName)
    {
        var xs = new List<double>();
        var ys = new List<double>();

        string[] rawLines = File.ReadAllLines(fileName);

        // Detect column layout from the first non-empty line
        // Supported formats:
        //   Lunghezza cm,Diametro mm                     → xCol=0, yCol=1
        //   Dataset,Lunghezza cm,Diametro mm,Display mm  → xCol=1, yCol=2
        //   ,x,y,...  (multi-dataset, data rows)         → xCol=1, yCol=2
        int xCol = 0, yCol = 1, startRow = 0;
        for (int i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var lower = line.ToLowerInvariant();
            if (lower.StartsWith("dataset") || lower.StartsWith(","))
            {
                xCol = 1; yCol = 2;
                startRow = i + 1;
            }
            else if (char.IsLetter(line[0]))
            {
                xCol = 0; yCol = 1;
                startRow = i + 1;
            }
            // else: no header, start from row i (startRow stays 0)
            break;
        }

        foreach (var rawLine in rawLines.Skip(startRow))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var parts = rawLine.Trim()
                               .Split(new[] { ';', ',', '\t' }, StringSplitOptions.None)
                               .Select(p => p.Trim().Trim('"')).ToArray();

            if (parts.Length <= Math.Max(xCol, yCol)) continue;
            if (!TryParseDouble(parts[xCol], out double x)) continue;
            if (!TryParseDouble(parts[yCol], out double y)) continue;

            xs.Add(x);
            ys.Add(y);
        }

        if (xs.Count == 0)
            throw new InvalidOperationException("Nessun dato valido trovato nel file.");

        return new ImportedSeries
        {
            Name  = Path.GetFileNameWithoutExtension(fileName),
            Xs    = xs.ToArray(),
            Ys    = ys.ToArray(),
            Color = PickImportColor(_importedSeries.Count)
        };
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("it-IT"), out value);

    private static ScottColor PickImportColor(int index)
    {
        ScottColor[] palette = { Colors.Green, Colors.Red, Colors.Purple, Colors.Brown, Colors.Cyan, Colors.Magenta };
        return palette[index % palette.Length];
    }

    private class ImportedSeries
    {
        public string     Name  { get; set; } = "";
        public double[]   Xs    { get; set; } = Array.Empty<double>();
        public double[]   Ys    { get; set; } = Array.Empty<double>();
        public ScottColor Color { get; set; } = Colors.Green;
    }
}
