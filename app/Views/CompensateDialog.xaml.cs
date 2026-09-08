using System;
using System.Windows;

namespace DiametroLineaDesktop.Views;

/// <summary>
/// Asks for the target sink speed of a physical compensation. Replaces the old live toolbar
/// slider: the main window never shows a compensated preview any more — "Create C file" solves
/// the profile and writes it straight to its own sibling file, leaving this design untouched.
/// </summary>
public partial class CompensateDialog : Window
{
    private readonly Func<double, string> _fileNameFor;

    /// <summary>Chosen target sink speed, in inches per second.</summary>
    public double SelectedSpeedIns { get; private set; }

    /// <param name="minIns">Slowest sink speed among the current sections (in/s).</param>
    /// <param name="maxIns">Fastest sink speed among the current sections (in/s).</param>
    /// <param name="defaultIns">Initial slider position (in/s).</param>
    /// <param name="fileNameFor">Maps a target speed to the file name that would be written.</param>
    public CompensateDialog(double minIns, double maxIns, double defaultIns, Func<double, string> fileNameFor)
    {
        _fileNameFor = fileNameFor;
        InitializeComponent();

        // A single-section line (or one whose sections happen to match) has a degenerate range;
        // open it up slightly so the slider is still usable rather than locked to one value.
        if (maxIns - minIns < 0.01)
        {
            minIns = Math.Max(0.01, minIns - 0.25);
            maxIns += 0.25;
        }

        SpeedSlider.Minimum = minIns;
        SpeedSlider.Maximum = maxIns;
        SpeedSlider.Value   = Math.Clamp(defaultIns, minIns, maxIns);
        MinLabel.Text = $"{minIns:0.00}";
        MaxLabel.Text = $"{maxIns:0.00}";
        UpdateLabels();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void UpdateLabels()
    {
        if (SpeedValueLabel == null) return; // fires mid-InitializeComponent, before the fields exist
        SpeedValueLabel.Text = $"{SpeedSlider.Value:0.000} in/s";
        FileNameLabel.Text   = _fileNameFor(SpeedSlider.Value);
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectedSpeedIns = SpeedSlider.Value;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
