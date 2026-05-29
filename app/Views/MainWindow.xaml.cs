using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    private bool _showScanLayer   = true;
    private bool _showDesignLayer = true;

    // Segment drawing — node.Y stores FULL DIAMETER in mm (not radius)
    private readonly List<(double X, double Y)> _segmentNodes = new();
    private readonly Stack<double> _segmentUndoStack = new();
    private bool _segmentDrawMode = false;

    // Segment table (bound to Project DataGrid in XAML)
    public ObservableCollection<ProjectSegment> ProjectSegments { get; } = new();

    // Node table — editable DataGrid in Project panel (also bound in XAML)
    public ObservableCollection<DesignNode> DesignNodes { get; } = new();
    private bool _syncingNodes = false;

    // Drag state — null when not dragging
    private double? _draggingNodeX = null;   // original X of the node being dragged
    private const double DragHitRadiusPx = 12.0;

    // Project state
    private string? _currentProjectPath = null;
    private bool    _isDirty            = false;
    private string  _projectName        = "Untitled";
    private DateTime _projectCreatedAt  = DateTime.UtcNow;

    // Persists user-edited segment names and specific weights across RefreshSegmentTable() calls.
    // Key: (StartCm, EndCm) — survives as long as the segment boundaries don't move.
    private readonly Dictionary<(double, double), (string Name, double SpecWeight)> _segmentMetadata = new();

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
            SetupPlot();
            _autoFitEnabled = _vm.Settings.Chart.AutoFit;
            _vm.Points.CollectionChanged += Points_CollectionChanged;
            _vm.PropertyChanged += (s, e) =>
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
            };
            await _vm.InitializeAsync();
            RefreshPlot();
            RefreshStatusBar();
            UpdateProjectTitle();
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
        plot.Title("Profilo diametro linea");
        plot.XLabel("Lunghezza (cm)");
        plot.YLabel("Diametro (mm)");
        plot.ShowLegend();

        PlotControl.PreviewMouseLeftButtonDown  += PlotControl_PreviewMouseLeftButtonDown;
        PlotControl.PreviewMouseRightButtonDown += PlotControl_PreviewMouseRightButtonDown;
        PlotControl.PreviewMouseMove            += PlotControl_PreviewMouseMove;
        PlotControl.PreviewMouseLeftButtonUp    += PlotControl_PreviewMouseLeftButtonUp;
        PlotControl.PreviewMouseDoubleClick     += PlotControl_PreviewMouseDoubleClick;
        PlotControl.MouseLeave                  += (_, _) => HoverCoordsStatus = string.Empty;

        _plotInitialized = true;
        PlotControl.Refresh();
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

        var plot = PlotControl.Plot;
        plot.Clear();

        var pts = _vm.Points.OrderBy(p => p.X).ToList();
        var displayedYs = GetDisplayedSeries(pts);  // full diameters

        if (_showScanLayer && pts.Count > 0)
        {
            double[] xs = pts.Select(p => p.X).ToArray();

            // Symmetric profile: top = +radius, bottom = -radius (mirrors the web UI)
            if (_vm.Settings.Chart.ShowFilteredSeries)
            {
                var alpha = (float)Math.Clamp(_vm.Settings.Chart.FilteredOpacity, 0.0, 1.0);
                var col   = Colors.Blue.WithAlpha(alpha);
                int lw    = _vm.Settings.Chart.LineWidth;

                double[] topYs = displayedYs.Select(y => y / 2.0).ToArray();
                double[] botYs = displayedYs.Select(y => -y / 2.0).ToArray();

                // Filled gradient band simulating a round solid cross-section
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

            // Optional raw series (also mirrored)
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

            // Live scan annotation: dimension label at the latest received point
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

        // Imported comparison series — also mirrored with fill
        if (_showScanLayer)
        {
            foreach (var series in _importedSeries)
            {
                double[] halfYs = series.Ys.Select(y =>  y / 2.0).ToArray();
                double[] negYs  = series.Ys.Select(y => -y / 2.0).ToArray();

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

        if (_showDesignLayer)
            RenderSegmentOverlay(plot);

        plot.Title("Profilo diametro linea");
        plot.XLabel("Lunghezza (cm)");
        plot.YLabel("Diametro (mm)");
        plot.ShowLegend();

        if (_autoFitEnabled) plot.Axes.AutoScale();
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
        _segmentMetadata.Clear();
        ProjectSegments.Clear();
        DesignNodes.Clear();
        TotalVolumeText = string.Empty;
        _lastImportedFile = "-";
        _vm.Points.CollectionChanged += Points_CollectionChanged;
    }

    private void SaveProjectToFile(string path)
    {
        var project = new FlyLineProject
        {
            Name       = _projectName,
            CreatedAt  = _projectCreatedAt,
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
            SegmentMetadata = ProjectSegments
                                .Select(s => new ProjectSegmentMeta
                                {
                                    StartCm    = s.StartCm,
                                    EndCm      = s.EndCm,
                                    Name       = s.Name,
                                    SpecWeight = s.SpecWeightGCm3
                                })
                                .ToList()
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

        // Restore segment metadata (names, spec weights)
        _segmentMetadata.Clear();
        foreach (var m in project.SegmentMetadata)
            _segmentMetadata[(m.StartCm, m.EndCm)] = (m.Name, m.SpecWeight);

        _projectName        = project.Name;
        _projectCreatedAt   = project.CreatedAt;
        _currentProjectPath = path;
        _isDirty            = false;
        _lastImportedFile   = _importedSeries.Count > 0 ? _importedSeries[^1].Name : "-";

        _vm.Points.CollectionChanged += Points_CollectionChanged;

        RefreshPlot();
        FitAfterRefresh();
        RefreshSegmentTable();
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
    /// Draws a filled band between topYs and botYs with a layered gradient that
    /// simulates the cross-section of a round solid (fly line), giving a "sfumatura"
    /// cylindrical appearance: darker body at the edges, lighter highlight at centre.
    /// </summary>
    private static void DrawLineFill(ScottPlot.Plot plot,
                                     double[] xs, double[] topYs, double[] botYs,
                                     ScottPlot.Color bodyColor)
    {
        if (xs.Length < 2) return;

        // Build a closed polygon for a given fraction of the full radius on each side.
        ScottPlot.Coordinates[] Band(double fraction)
        {
            var top = xs.Select((x, i) => new ScottPlot.Coordinates(x,  topYs[i] * fraction));
            var bot = xs.Select((x, i) => new ScottPlot.Coordinates(x,  botYs[i] * fraction))
                        .Reverse();
            return top.Concat(bot).ToArray();
        }

        // Layer 1 — full extent: body colour, semi-transparent (dark edges implied by absence of highlight)
        var body = plot.Add.Polygon(Band(1.0));
        body.FillColor  = bodyColor.WithAlpha(0.50f);
        body.LineWidth  = 0;
        body.LineColor  = Colors.Transparent;

        // Layer 2 — inner 60% of radius: lighter tint, softens the mid-zone
        var mid = plot.Add.Polygon(Band(0.60));
        mid.FillColor = Colors.White.WithAlpha(0.14f);
        mid.LineWidth = 0;
        mid.LineColor = Colors.Transparent;

        // Layer 3 — inner 25% of radius: stronger highlight simulating specular top surface
        var hi = plot.Add.Polygon(Band(0.25));
        hi.FillColor = Colors.White.WithAlpha(0.22f);
        hi.LineWidth = 0;
        hi.LineColor = Colors.Transparent;
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

    // Segment overlay rendering
    // node.Y stores full diameter in mm; chart Y axis is radius (diameter/2)
    private void RenderSegmentOverlay(Plot plot)
    {
        if (_segmentNodes.Count == 0) return;

        var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
        double[] xs       = sorted.Select(n => n.X).ToArray();
        double[] halfYs   = sorted.Select(n =>  n.Y / 2.0).ToArray();   // +radius
        double[] negHalfYs = sorted.Select(n => -n.Y / 2.0).ToArray(); // -radius (mirror)

        var designColor = new ScottColor(220, 50, 50); // red

        if (sorted.Count >= 2)
        {
            // Gradient fill like the scan profile
            DrawLineFill(plot, xs, halfYs, negHalfYs, designColor);

            var topLine = plot.Add.Scatter(xs, halfYs);
            topLine.LegendText = $"Design ({sorted.Count} nodes)";
            topLine.Color      = designColor;
            topLine.LineWidth  = 2;
            topLine.MarkerSize = 0;

            var botLine = plot.Add.Scatter(xs, negHalfYs);
            botLine.Color      = designColor;
            botLine.LineWidth  = 2;
            botLine.MarkerSize = 0;
        }

        // Markers at node positions (on the top profile line)
        var markers = plot.Add.Scatter(xs, halfYs);
        markers.Color      = designColor;
        markers.LineWidth  = 0;
        markers.MarkerSize = 8;

        // Label each node: diameter (full value), position
        foreach (var node in sorted)
        {
            double chartY = node.Y / 2.0;
            var lbl = plot.Add.Text($"Ø {node.Y:0.000} mm\n{node.X:0.0} cm", node.X, chartY);
            lbl.LabelFontSize        = 10;
            lbl.LabelBold            = true;
            lbl.LabelFontColor       = Colors.DarkRed;
            lbl.LabelAlignment       = Alignment.LowerLeft;
            lbl.LabelBackgroundColor = Colors.White.WithAlpha(0.93f);
            lbl.LabelBorderColor     = Colors.DarkRed;
            lbl.LabelBorderWidth     = 1.5f;
            lbl.LabelPadding         = 3;
            lbl.OffsetX              = 8;
            lbl.OffsetY              = -8;
        }
    }

    /// <summary>
    /// Builds ProjectSegments from the current sorted node list and refreshes the
    /// bound DataGrid + the total-volume label.
    /// </summary>
    private void RefreshSegmentTable()
    {
        // Save current user edits (name, spec weight) before clearing
        foreach (var seg in ProjectSegments)
            _segmentMetadata[(seg.StartCm, seg.EndCm)] = (seg.Name, seg.SpecWeightGCm3);

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

            // Restore name and spec weight from metadata dict
            if (_segmentMetadata.TryGetValue((seg.StartCm, seg.EndCm), out var meta))
            {
                seg.Name            = meta.Name;
                seg.SpecWeightGCm3  = meta.SpecWeight;
            }
            else
            {
                seg.Name = $"S{i + 1}";
            }

            ProjectSegments.Add(seg);
        }

        double totalCm3  = ProjectSegments.Sum(s => s.VolumeCm3);
        bool   hasMass   = ProjectSegments.Count > 0 && ProjectSegments.All(s => s.SpecWeightGCm3 > 0);
        double totalMassG = hasMass ? ProjectSegments.Sum(s => s.MassG) : 0;

        TotalVolumeText = ProjectSegments.Count > 0
            ? hasMass
                ? $"Total: {totalCm3:0.00} cm³  |  {totalMassG:0.00} g  |  {ProjectSegments.Count} segments"
                : $"Total: {totalCm3:0.00} cm³  |  {ProjectSegments.Count} segments"
            : string.Empty;

        // Keep the editable node DataGrid in sync
        SyncDesignNodesToList();
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

    // Segment drawing mouse handlers
    private void PlotControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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

    private void ShowScanLayer_Click(object sender, RoutedEventArgs e)
    {
        _showScanLayer = ShowScanToggle.IsChecked ?? true;
        RefreshPlot();
        UiStatus = _showScanLayer ? "Scan layer visible" : "Scan layer hidden";
    }

    private void ShowDesignLayer_Click(object sender, RoutedEventArgs e)
    {
        _showDesignLayer = ShowDesignToggle.IsChecked ?? true;
        RefreshPlot();
        UiStatus = _showDesignLayer ? "Design layer visible" : "Design layer hidden";
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

        // Col 1 = Name, Col 8 = Sp.W. — handled by direct binding, no node sync needed
        if (colIdx == 1 || colIdx == 8)
        {
            Dispatcher.BeginInvoke(RefreshTotals);
            Dispatcher.BeginInvoke((Action)MarkDirty);
            return;
        }

        // Col 2 = Start Ø, Col 3 = End Ø, Col 4 = Length
        if (colIdx < 2 || colIdx > 4) return;
        if (!TryParseDouble(text, out double newVal) || newVal <= 0) return;

        Dispatcher.BeginInvoke(() =>
        {
            switch (colIdx)
            {
                case 2: // Start Ø — change the Y of the start node
                {
                    int idx = _segmentNodes.FindIndex(n => Math.Abs(n.X - seg.StartCm) < 0.05);
                    if (idx >= 0) _segmentNodes[idx] = (_segmentNodes[idx].X, newVal);
                    break;
                }
                case 3: // End Ø — change the Y of the end node (shared with next segment's start)
                {
                    int idx = _segmentNodes.FindIndex(n => Math.Abs(n.X - seg.EndCm) < 0.05);
                    if (idx >= 0) _segmentNodes[idx] = (_segmentNodes[idx].X, newVal);
                    break;
                }
                case 4: // Length — move the end node's X position
                {
                    double newEndCm = seg.StartCm + newVal;
                    int idx = _segmentNodes.FindIndex(n => Math.Abs(n.X - seg.EndCm) < 0.05);
                    if (idx >= 0) _segmentNodes[idx] = (Math.Round(newEndCm, 1), _segmentNodes[idx].Y);
                    break;
                }
            }

            RefreshPlot();           // redraw chart with updated nodes
            RefreshSegmentTable();   // rebuild segment rows
            MarkDirty();
        });
    }

    private void RefreshTotals()
    {
        double totalCm3   = ProjectSegments.Sum(s => s.VolumeCm3);
        bool   hasMass    = ProjectSegments.Count > 0 && ProjectSegments.All(s => s.SpecWeightGCm3 > 0);
        double totalMassG = hasMass ? ProjectSegments.Sum(s => s.MassG) : 0;

        TotalVolumeText = ProjectSegments.Count > 0
            ? hasMass
                ? $"Total: {totalCm3:0.00} cm³  |  {totalMassG:0.00} g  |  {ProjectSegments.Count} segments"
                : $"Total: {totalCm3:0.00} cm³  |  {ProjectSegments.Count} segments"
            : string.Empty;
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
