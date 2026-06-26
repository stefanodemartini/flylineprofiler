using System.Globalization;
using System.Windows;
using System.Windows.Input;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Services;

namespace DiametroLineaDesktop.Views;

public partial class SinkSpeedToDensityDialog : Window
{
    private readonly IReadOnlyList<ProjectSegment> _segments;
    private readonly bool   _isSalt;
    private readonly double _tempC;

    private const double MaxPracticalDensity = 2.5;  // g/cm³ — upper limit for tungsten-loaded compounds

    public double ResultDensity { get; private set; }

    public SinkSpeedToDensityDialog(
        IReadOnlyList<ProjectSegment> segments,
        bool isSalt, double tempC)
    {
        _segments = segments;
        _isSalt   = isSalt;
        _tempC    = tempC;
        InitializeComponent();
        SpeedBox.Focus();
    }

    private IEnumerable<(double S, double E, double L)> GetGeometries() =>
        _segments.Select(s => (s.StartDiameterMm, s.EndDiameterMm, s.LengthCm));

    private void SpeedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Recalculate();

    private void SpeedBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Recalculate(); e.Handled = true; }
    }

    private void Recalculate()
    {
        if (!TryParseSpeed(SpeedBox?.Text, out double speedIns) || speedIns <= 0)
        {
            DensityDisplay.Text      = "—";
            DensityDisplay.Foreground = FindResource("Success") as System.Windows.Media.Brush;
            WarnDisplay.Visibility   = Visibility.Collapsed;
            ApplyBtn.IsEnabled       = false;
            return;
        }

        double speedMs = speedIns / 39.3701;
        var (density, _) = SinkingSpeedCalc.DensityForTargetSinkSpeed(
            _isSalt, _tempC, GetGeometries(), speedMs);

        if (density <= 0)
        {
            DensityDisplay.Text    = "—";
            WarnDisplay.Text       = "Cannot compute — check that segments have valid geometry.";
            WarnDisplay.Visibility = Visibility.Visible;
            ApplyBtn.IsEnabled     = false;
            return;
        }

        DensityDisplay.Text = $"{density:0.00}";

        if (density > MaxPracticalDensity)
        {
            DensityDisplay.Foreground = FindResource("Warn") as System.Windows.Media.Brush;
            WarnDisplay.Text = $"Required density {density:0.00} g/cm³ exceeds the practical maximum " +
                               $"({MaxPracticalDensity:0.0} g/cm³ for tungsten-loaded compounds). " +
                               "This sink speed may not be achievable with standard materials.";
            WarnDisplay.Visibility = Visibility.Visible;
            ApplyBtn.IsEnabled     = true;
        }
        else
        {
            DensityDisplay.Foreground = FindResource("Success") as System.Windows.Media.Brush;
            WarnDisplay.Visibility    = Visibility.Collapsed;
            ApplyBtn.IsEnabled        = true;
        }

        ResultDensity = density;
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ResultDensity > 0) DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParseSpeed(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string n = text.Trim().Replace(',', '.');
        return double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && value > 0;
    }
}
