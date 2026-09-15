using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Physics;
using DiametroLineaDesktop.Services;
using DiametroLineaDesktop.ViewModels;
using Microsoft.Win32;
using ScottPlot;

namespace DiametroLineaDesktop.Views;

/// <summary>
/// Simulator window: loads one or two .flp projects, runs a pre-registered casting scenario for
/// each (app/Physics/), and shows the loop animated in the vertical casting plane plus the two
/// supporting comparison charts. Talks to <see cref="SimulatorViewModel"/> only — no physics here,
/// just rendering. 2D by design — see <see cref="Physics.Vec2"/>.
/// </summary>
public partial class SimulatorWindow : Window
{
    private readonly SimulatorViewModel _vm = new();
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private IPlottable? _speedMarker;
    private IPlottable? _heightMarker;

    public SimulatorWindow()
    {
        InitializeComponent();
        _playTimer.Tick += PlayTimer_Tick;
    }

    private void BtnLoadV1_Click(object sender, RoutedEventArgs e) => LoadInto(_vm.LoadV1, TxtV1Name);
    private void BtnLoadV2_Click(object sender, RoutedEventArgs e) => LoadInto(_vm.LoadV2, TxtV2Name);

    private void LoadInto(Func<string, bool> loader, System.Windows.Controls.TextBlock label)
    {
        var dlg = new OpenFileDialog
        {
            Filter = ProjectService.FileFilter,
            Title = "Apri progetto .flp",
            InitialDirectory = ProjectService.DefaultProjectFolder,
        };
        if (dlg.ShowDialog() != true) return;
        if (loader(dlg.FileName)) label.Text = System.IO.Path.GetFileName(dlg.FileName);
        TxtStatus.Text = _vm.StatusText;
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        _vm.SelectedScenario = CmbScenario.SelectedIndex == 1
            ? CastingScenarioKind.Distance
            : CastingScenarioKind.OverheadCast;

        BtnRun.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        TxtStatus.Text = "Simulazione in corso...";
        try
        {
            await System.Threading.Tasks.Task.Run(() => _vm.Run());
        }
        finally
        {
            Mouse.OverrideCursor = null;
            BtnRun.IsEnabled = true;
        }
        TxtStatus.Text = _vm.StatusText;

        PlotFullSeries();

        int frameCount = Math.Max(_vm.ResultV1?.FramePositions.Length ?? 0, _vm.ResultV2?.FramePositions.Length ?? 0);
        SldTime.IsEnabled = frameCount > 1;
        BtnPlay.IsEnabled = frameCount > 1;
        SldTime.Minimum = 0;
        SldTime.Maximum = Math.Max(0, frameCount - 1);
        SldTime.Value = 0;
        UpdateFrame(0);
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateFrame((int)Math.Round(e.NewValue));

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_playTimer.IsEnabled)
        {
            _playTimer.Stop();
            BtnPlay.Content = "▶ Play";
        }
        else
        {
            _playTimer.Start();
            BtnPlay.Content = "⏸ Pausa";
        }
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        double next = SldTime.Value + 1;
        if (next > SldTime.Maximum) next = 0;
        SldTime.Value = next;
    }

    private void UpdateFrame(int frameIndex)
    {
        double t = _vm.ResultV1?.FrameTimesS.ElementAtOrDefault(Math.Min(frameIndex, Math.Max(0, _vm.ResultV1.FrameTimesS.Length - 1)))
                   ?? _vm.ResultV2?.FrameTimesS.ElementAtOrDefault(Math.Min(frameIndex, Math.Max(0, _vm.ResultV2.FrameTimesS.Length - 1)))
                   ?? 0;
        TxtFrameTime.Text = $"t = {t:0.00} s";

        UpdateLoopShapePlot(frameIndex);
        UpdateTimeMarkers(t);
    }

    // ── Loop shape (animated) ────────────────────────────────────────────────

    private void UpdateLoopShapePlot(int frameIndex)
    {
        var plot = PlotLoopShape.Plot;
        plot.Clear();

        var ground = plot.Add.HorizontalLine(0);
        ground.Color = ScottPlot.Colors.SteelBlue.WithAlpha(0.5);
        ground.LineWidth = 1.5f;
        ground.Text = "Acqua / suolo";

        var reference = _vm.ResultV1 ?? _vm.ResultV2;
        if (reference is not null && reference.FramePositions.Length > 0)
        {
            int fi = Math.Clamp(frameIndex, 0, reference.FramePositions.Length - 1);
            var tip = reference.FramePositions[fi][0];
            var pivot = new Vec2(0, _vm.Settings.RodPivotHeightM);
            var rod = plot.Add.Scatter(new[] { pivot.X, tip.X }, new[] { pivot.Y, tip.Y });
            rod.Color = ScottPlot.Colors.SaddleBrown;
            rod.LineWidth = 4f;
            rod.MarkerSize = 6;
            rod.LegendText = "Canna";
        }

        if (_vm.ProjectV1 is not null && _vm.ResultV1 is not null)
            AddColoredLoopShape(plot, _vm.ProjectV1, _vm.ResultV1, frameIndex, dashed: false, lineLabel: "V1");
        if (_vm.ProjectV2 is not null && _vm.ResultV2 is not null)
            AddColoredLoopShape(plot, _vm.ProjectV2, _vm.ResultV2, frameIndex, dashed: true, lineLabel: "V2");
        plot.Axes.AutoScale();
        plot.ShowLegend();
        PlotLoopShape.Refresh();
    }

    /// <summary>Chunk size (nodes) for re-drawing the line's real taper as visible thickness — fine
    /// enough to show a taper's shape, coarse enough to keep the plottable count per frame small.</summary>
    private const int ThicknessChunkNodes = 8;

    /// <summary>Splits the current frame's shape into contiguous same-nozzle-color runs (the same
    /// colouring the app's own segment chart uses, <see cref="SimulatorViewModel.NozzleColorHexAt"/>),
    /// and within each run into smaller chunks drawn at a line width proportional to the real local
    /// diameter — otherwise every taper renders as one uniform-thickness stroke, which hides the
    /// very thing (varying diameter, and so mass) that makes one line cast differently from
    /// another.</summary>
    private static void AddColoredLoopShape(Plot plot, FlyLineProject project, CastSimulationResult result, int frameIndex, bool dashed, string lineLabel)
    {
        if (result.FramePositions.Length == 0) return;
        int fi = Math.Clamp(frameIndex, 0, result.FramePositions.Length - 1);
        var pos = result.FramePositions[fi];
        double[] arcs = result.NodeArcLengthsCm;
        int n = pos.Length;
        if (n < 2) return;

        var sampler = new LineArcSampler(project);
        bool legendDrawn = false;

        int runStart = 0;
        string currentColor = SimulatorViewModel.NozzleColorHexAt(project, arcs[0]);
        for (int i = 1; i < n; i++)
        {
            string color = SimulatorViewModel.NozzleColorHexAt(project, arcs[i]);
            if (!string.Equals(color, currentColor, StringComparison.OrdinalIgnoreCase))
            {
                AddTaperedRun(plot, pos, arcs, sampler, runStart, i, currentColor, dashed, lineLabel, ref legendDrawn);
                runStart = i;
                currentColor = color;
            }
        }
        AddTaperedRun(plot, pos, arcs, sampler, runStart, n - 1, currentColor, dashed, lineLabel, ref legendDrawn);
    }

    private static void AddTaperedRun(Plot plot, Vec2[] pos, double[] arcs, LineArcSampler sampler,
        int startIdx, int endIdx, string colorHex, bool dashed, string lineLabel, ref bool legendDrawn)
    {
        if (endIdx <= startIdx) return;
        var color = HexToScottPlotColor(colorHex);
        for (int chunkStart = startIdx; chunkStart < endIdx; chunkStart += ThicknessChunkNodes)
        {
            int chunkEnd = Math.Min(chunkStart + ThicknessChunkNodes, endIdx);
            double midArcCm = (arcs[chunkStart] + arcs[chunkEnd]) / 2.0;
            double diamMm = sampler.DiameterMmAt(midArcCm);

            int count = chunkEnd - chunkStart + 1;
            double[] xs = new double[count];
            double[] ys = new double[count];
            for (int i = chunkStart; i <= chunkEnd; i++)
            {
                xs[i - chunkStart] = pos[i].X;
                ys[i - chunkStart] = pos[i].Y;
            }
            var s = plot.Add.Scatter(xs, ys);
            s.Color = color;
            s.MarkerSize = 0;
            s.LineWidth = DiameterToLineWidth(diamMm);
            if (dashed) s.LinePattern = LinePattern.Dashed;
            if (!legendDrawn) { s.LegendText = lineLabel; legendDrawn = true; }
        }
    }

    /// <summary>Maps a real line diameter (mm, typically ~0.5-2 mm for a fly line) to a visibly
    /// distinct pixel line width — otherwise a taper's actual thickness variation, sub-millimetre
    /// in reality, would be invisible on screen.</summary>
    private static float DiameterToLineWidth(double diameterMm) => (float)(1.5 + diameterMm * 4.0);

    private static ScottPlot.Color HexToScottPlotColor(string hex)
    {
        hex = (hex ?? string.Empty).TrimStart('#');
        if (hex.Length != 6) return ScottPlot.Colors.Gray;
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return new ScottPlot.Color(r, g, b);
    }

    // ── Charts ───────────────────────────────────────────────────────────────

    private void PlotFullSeries()
    {
        PlotSpeed.Plot.Clear();
        PlotHeightLoss.Plot.Clear();

        if (_vm.ResultV1 is { } r1)
        {
            AddSeries(PlotSpeed.Plot, r1.TimesS, r1.FreeEndSpeedMs, ScottPlot.Colors.Red, "V1", dashed: false);
            AddSeries(PlotHeightLoss.Plot, r1.TimesS, r1.FreeEndHeightM, ScottPlot.Colors.Red, "V1 quota", dashed: false);
            AddSeries(PlotHeightLoss.Plot, r1.TimesS, r1.FreeEndHeightBallisticM, ScottPlot.Colors.Red.WithAlpha(0.4), "V1 senza drag", dashed: true);
        }
        if (_vm.ResultV2 is { } r2)
        {
            AddSeries(PlotSpeed.Plot, r2.TimesS, r2.FreeEndSpeedMs, ScottPlot.Colors.Blue, "V2", dashed: true);
            AddSeries(PlotHeightLoss.Plot, r2.TimesS, r2.FreeEndHeightM, ScottPlot.Colors.Blue, "V2 quota", dashed: false);
            AddSeries(PlotHeightLoss.Plot, r2.TimesS, r2.FreeEndHeightBallisticM, ScottPlot.Colors.Blue.WithAlpha(0.4), "V2 senza drag", dashed: true);
        }

        PlotSpeed.Plot.Axes.AutoScale();
        PlotSpeed.Plot.ShowLegend();
        PlotSpeed.Refresh();
        PlotHeightLoss.Plot.Axes.AutoScale();
        PlotHeightLoss.Plot.ShowLegend();
        PlotHeightLoss.Refresh();
    }

    private static void AddSeries(Plot plot, double[] xs, double[] ys, ScottPlot.Color color, string label, bool dashed)
    {
        var s = plot.Add.Scatter(xs, ys);
        s.Color = color;
        s.LegendText = label;
        s.MarkerSize = 0;
        s.LineWidth = dashed ? 1.5f : 2f;
        if (dashed) s.LinePattern = LinePattern.Dashed;
    }

    private void UpdateTimeMarkers(double t)
    {
        if (_speedMarker is not null) PlotSpeed.Plot.Remove(_speedMarker);
        var sm = PlotSpeed.Plot.Add.VerticalLine(t);
        sm.Color = ScottPlot.Colors.Black.WithAlpha(0.5);
        _speedMarker = sm;
        PlotSpeed.Refresh();

        if (_heightMarker is not null) PlotHeightLoss.Plot.Remove(_heightMarker);
        var hm = PlotHeightLoss.Plot.Add.VerticalLine(t);
        hm.Color = ScottPlot.Colors.Black.WithAlpha(0.5);
        _heightMarker = hm;
        PlotHeightLoss.Refresh();
    }
}
