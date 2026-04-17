using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.ViewModels;
using ScottPlot;

namespace DiametroLineaDesktop.Views;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm = new();
    private bool _plotInitialized = false;
    private bool _autoFitEnabled = true;
    private readonly List<ImportedSeries> _importedSeries = new();
    private string _lastImportedFile = "-";
    private string _uiStatus = "Pronto";

    // Segment drawing
    private readonly List<(double X, double Y)> _segmentNodes = new();
    private readonly Stack<double> _segmentUndoStack = new();
    private bool _segmentDrawMode = false;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string UiStatus
    {
        get => _uiStatus;
        set { _uiStatus = value; OnPropertyChanged(nameof(UiStatus)); }
    }

    public string ImportedSeriesStatus   => $"Serie importate: {_importedSeries.Count}";
    public string LastImportedFileStatus => $"Ultimo CSV: {_lastImportedFile}";
    public string AutoFitStatus          => $"Auto-fit: {(_autoFitEnabled ? "ON" : "OFF")}";
    public string DrawModeStatus         => _segmentDrawMode ? "DISEGNO SEGMENTI ATTIVO" : string.Empty;
    public string SegmentNodesStatus     => _segmentNodes.Count > 0 ? $"Nodi: {_segmentNodes.Count}" : string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

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
            _vm.PropertyChanged += (_, _) => RefreshStatusBar();
            await _vm.InitializeAsync();
            RefreshPlot();
            RefreshStatusBar();
        };
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshStatusBar()
    {
        OnPropertyChanged(nameof(ImportedSeriesStatus));
        OnPropertyChanged(nameof(LastImportedFileStatus));
        OnPropertyChanged(nameof(AutoFitStatus));
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

        _plotInitialized = true;
        PlotControl.Refresh();
    }

    private void Points_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.Invoke(RefreshPlot);

    private void RefreshPlot()
    {
        if (!_plotInitialized) return;

        var plot = PlotControl.Plot;
        plot.Clear();

        var pts = _vm.Points.OrderBy(p => p.X).ToList();

        if (pts.Count > 0)
        {
            double[] xs = pts.Select(p => p.X).ToArray();

            if (_vm.Settings.Chart.ShowFilteredSeries)
            {
                var filtered = plot.Add.Scatter(xs, pts.Select(p => p.FilteredY).ToArray());
                filtered.LegendText = "Filtered";
                filtered.LineWidth  = _vm.Settings.Chart.LineWidth;
                filtered.MarkerSize = 0;
                filtered.Color      = Colors.Blue;
            }

            if (_vm.Settings.Chart.ShowRawSeries)
            {
                var raw = plot.Add.Scatter(xs, pts.Select(p => p.RawY).ToArray());
                raw.LegendText = "Raw";
                raw.LineWidth  = 1;
                raw.MarkerSize = 0;
                raw.Color      = Colors.Orange;
            }
        }

        foreach (var series in _importedSeries)
        {
            var imp = plot.Add.Scatter(series.Xs, series.Ys);
            imp.LegendText = series.Name;
            imp.LineWidth  = 2;
            imp.MarkerSize = 0;
            imp.Color      = series.Color;
        }

        RenderSegmentOverlay(plot);

        plot.Title("Profilo diametro linea");
        plot.XLabel("Lunghezza (cm)");
        plot.YLabel("Diametro (mm)");
        plot.ShowLegend();

        if (_autoFitEnabled) plot.Axes.AutoScale();
        PlotControl.Refresh();
        RefreshStatusBar();
    }

    // Segment overlay rendering
    private void RenderSegmentOverlay(Plot plot)
    {
        if (_segmentNodes.Count == 0) return;

        var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
        double[] xs = sorted.Select(n => n.X).ToArray();
        double[] ys = sorted.Select(n => n.Y).ToArray();

        if (sorted.Count >= 2)
        {
            var line = plot.Add.Scatter(xs, ys);
            line.LegendText = $"Segmenti ({sorted.Count} nodi)";
            line.Color      = Colors.Red;
            line.LineWidth  = 2;
            line.MarkerSize = 0;
        }

        var markers = plot.Add.Scatter(xs, ys);
        markers.LegendText = "";
        markers.Color      = Colors.Red;
        markers.LineWidth  = 0;
        markers.MarkerSize = 8;
    }

    // Segment drawing mouse handlers
    private void PlotControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_segmentDrawMode) return;

        var pos    = e.GetPosition(PlotControl);
        var coords = PlotControl.Plot.GetCoordinates(new Pixel((float)pos.X, (float)pos.Y));

        double snappedX = Math.Round(coords.X);
        double y        = Math.Round(coords.Y, 3);

        _segmentNodes.RemoveAll(n => n.X == snappedX);
        _segmentNodes.Add((snappedX, y));
        _segmentUndoStack.Push(snappedX);

        RefreshPlot();
        UiStatus = $"Nodo aggiunto: {snappedX:0} cm = {y:0.000} mm  (totale {_segmentNodes.Count})";
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
        UiStatus = $"Nodo rimosso: {closest.X:0} cm  (rimanenti: {_segmentNodes.Count})";
        e.Handled = true;
    }

    // Segment menu handlers
    private void ToggleDrawMode_Click(object sender, RoutedEventArgs e)
    {
        _segmentDrawMode = MenuDrawSegments.IsChecked;
        PlotControl.Cursor = _segmentDrawMode ? Cursors.Cross : Cursors.Arrow;
        UiStatus = _segmentDrawMode
            ? "Modalita disegno ON  —  clic sx = aggiungi nodo, clic dx = rimuovi nodo"
            : "Modalita disegno OFF";
        RefreshStatusBar();
    }

    private void UndoLastNode_Click(object sender, RoutedEventArgs e)
    {
        if (_segmentUndoStack.Count == 0) return;
        double lastX = _segmentUndoStack.Pop();
        _segmentNodes.RemoveAll(n => n.X == lastX);
        RefreshPlot();
        UiStatus = $"Annullato nodo a {lastX:0} cm  (rimanenti: {_segmentNodes.Count})";
    }

    private void ClearSegments_Click(object sender, RoutedEventArgs e)
    {
        _segmentNodes.Clear();
        _segmentUndoStack.Clear();
        RefreshPlot();
        UiStatus = "Segmenti cancellati";
    }

    // Export segments as CSV
    private void ExportSegmentsCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_segmentNodes.Count < 2)
        {
            MessageBox.Show("Servono almeno 2 nodi per esportare i segmenti.",
                            "Attenzione", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter     = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName   = "segmenti_profilo.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sorted = _segmentNodes.OrderBy(n => n.X).ToList();
        int minCm  = (int)Math.Round(sorted.First().X);
        int maxCm  = (int)Math.Round(sorted.Last().X);

        var sb = new StringBuilder();
        sb.AppendLine("Lunghezza cm,Segmento mm");
        for (int cm = minCm; cm <= maxCm; cm++)
            sb.AppendLine($"{cm},{InterpolateSegment(sorted, cm):0.000}");

        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        UiStatus = "Segmenti esportati";
        MessageBox.Show($"Esportati {maxCm - minCm + 1} punti  ({_segmentNodes.Count} nodi).\n{dlg.FileName}",
                        "Export segmenti");
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

    // Connessione
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        UiStatus = "Connessione in corso...";
        await _vm.ConnectAsync(initiatedByUser: true);
        UiStatus = "Connessione richiesta";
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.DisconnectAsync();
        UiStatus = "Disconnesso";
    }

    // Comandi motore / scan
    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor scan");
        await Send("scan_on");
        UiStatus = "SCAN inviato";
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor stop");
        await Send("scan_off");
        UiStatus = "STOP inviato";
    }

    private async void ScanOn_Click(object sender, RoutedEventArgs e)
    {
        await Send("scan_on");
        UiStatus = "Ricezione ON";
    }

    private async void ScanOff_Click(object sender, RoutedEventArgs e)
    {
        await Send("scan_off");
        UiStatus = "Ricezione OFF";
    }

    private async void FastS_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor fast_s");
        UiStatus = "FAST stessa direzione";
    }

    private async void FastO_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor fast_o");
        UiStatus = "FAST direzione opposta";
    }

    private async void MotorStatus_Click(object sender, RoutedEventArgs e)
    {
        await Send("motor status");
        UiStatus = "Stato motore richiesto";
    }

    // Strumenti
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        await Send("reset");
        _vm.ClearAllData();
        UiStatus = "Dati resettati";
    }

    private async void ReadRaw_Click(object sender, RoutedEventArgs e)
    {
        await Send("readraw");
        _vm.AppendLog("Nota: la risposta readraw appare sulla console seriale del dispositivo");
        UiStatus = "Lettura raw richiesta";
    }

    private async void SetDisplayZero_Click(object sender, RoutedEventArgs e)
    {
        await Send("setdisplayzero");
        UiStatus = "Zero display impostato";
    }

    private async void ResetOffset_Click(object sender, RoutedEventArgs e)
    {
        await Send("resetoffset");
        UiStatus = "Offset resettato";
    }

    private async void SetOffset_Click(object sender, RoutedEventArgs e)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            "Inserisci valore offset in mm (usa punto come separatore decimale):",
            "Imposta Offset",
            "0.00");

        if (string.IsNullOrWhiteSpace(input)) return;

        if (TryParseDouble(input, out var offset))
        {
            await Send($"setoffset {offset.ToString("0.000", CultureInfo.InvariantCulture)}");
            UiStatus = $"Offset impostato: {offset:0.000} mm";
        }
        else
        {
            MessageBox.Show("Valore non valido. Usa il punto come separatore decimale (es. 0.25).",
                            "Errore", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // GOTOPOS
    private async void Goto_Click(object sender, RoutedEventArgs e)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            "Inserisci la posizione target in cm:",
            "Vai a posizione",
            "150");

        if (TryParseDouble(input, out var cm) && cm >= 0)
        {
            await Send($"goto {cm.ToString("0.0", CultureInfo.InvariantCulture)}");
            UiStatus = $"Goto richiesto a {cm:0.0} cm";
        }
    }

    // CSV import / export
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter      = "CSV files (*.csv)|*.csv",
            DefaultExt  = ".csv",
            FileName    = "diametrolinea_export.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Lunghezza cm,Raw mm,Filtered mm");
        foreach (var p in _vm.Points.OrderBy(p => p.X))
            sb.AppendLine($"{p.X:0.0},{p.RawY:0.000},{p.FilteredY:0.000}");

        File.WriteAllText(dlg.FileName, sb.ToString());
        UiStatus = "CSV esportato";
        MessageBox.Show($"CSV esportato in:\n{dlg.FileName}");
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter     = "PNG files (*.png)|*.png",
            DefaultExt = ".png",
            FileName   = "diametrolinea_plot.png"
        };
        if (dlg.ShowDialog() != true) return;

        PlotControl.Plot.SavePng(dlg.FileName, 1400, 800);
        UiStatus = "PNG esportato";
        MessageBox.Show($"PNG esportato in:\n{dlg.FileName}");
    }

    private void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter      = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = false,
            Title       = "Importa CSV confronto"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var series = LoadImportedSeries(dlg.FileName);
            _importedSeries.Add(series);
            _lastImportedFile = Path.GetFileName(dlg.FileName);
            UiStatus = $"CSV importato: {series.Name}";
            RefreshPlot();
            MessageBox.Show($"CSV importato: {series.Name}", "Import CSV");
        }
        catch (Exception ex)
        {
            UiStatus = "Errore import CSV";
            MessageBox.Show("Errore import CSV:\n" + ex.Message, "Errore");
        }
    }

    // Grafico
    private void FitPlot_Click(object sender, RoutedEventArgs e)
    {
        PlotControl.Plot.Axes.AutoScale();
        PlotControl.Refresh();
        UiStatus = "Assi adattati";
    }

    private void ClearImported_Click(object sender, RoutedEventArgs e)
    {
        _importedSeries.Clear();
        _lastImportedFile = "-";
        RefreshPlot();
        UiStatus = "Serie importate cancellate";
    }

    private void AutoFitToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            _autoFitEnabled = mi.IsChecked;
            _vm.Settings.Chart.AutoFit = _autoFitEnabled;
            _vm.SaveSettings();
            RefreshPlot();
            UiStatus = _autoFitEnabled ? "Auto-fit ON" : "Auto-fit OFF";
        }
    }

    // Impostazioni
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var copy = _vm.CloneSettings();
        var win  = new SettingsWindow(copy) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _vm.ApplySettings(win.EditableSettings);
            _autoFitEnabled = _vm.Settings.Chart.AutoFit;
            RefreshPlot();
            UiStatus = "Impostazioni salvate";
            MessageBox.Show("Impostazioni salvate.", "Settings");
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

        string[] lines = File.ReadAllLines(fileName);
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var line = rawLine.Trim();
            if (char.IsLetter(line[0])) continue;

            var parts = line.Split(new[] { ';', ',', '\t' }, StringSplitOptions.None)
                            .Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) continue;

            bool okX = TryParseDouble(parts[0], out double x);
            bool okY = false;
            double y = 0;

            if (parts.Length >= 3) okY = TryParseDouble(parts[2], out y);
            if (!okY) okY = TryParseDouble(parts[1], out y);

            if (okX && okY) { xs.Add(x); ys.Add(y); }
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

    private static Color PickImportColor(int index)
    {
        Color[] palette = { Colors.Green, Colors.Red, Colors.Purple, Colors.Brown, Colors.Cyan, Colors.Magenta };
        return palette[index % palette.Length];
    }

    private class ImportedSeries
    {
        public string   Name  { get; set; } = "";
        public double[] Xs    { get; set; } = Array.Empty<double>();
        public double[] Ys    { get; set; } = Array.Empty<double>();
        public Color    Color { get; set; } = Colors.Green;
    }
}
