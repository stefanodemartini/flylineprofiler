using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Views;

public partial class WeightToDensityDialog : Window
{
    private readonly IReadOnlyList<ProjectSegment> _segments;
    private readonly List<CheckBox> _segChecks = new();

    /// <summary>Back-calculated density in g/cm³. Valid only when DialogResult is true.</summary>
    public double ResultDensity { get; private set; }

    /// <summary>True when the whole-line scope was chosen (caller should set SharedDensity).</summary>
    public bool IsWholeLineScope { get; private set; }

    /// <summary>Segment indices (ProjectSegment.Index) to which the density should be applied.</summary>
    public List<int> AffectedIndices { get; private set; } = new();

    public WeightToDensityDialog(IReadOnlyList<ProjectSegment> segments)
    {
        _segments = segments;
        InitializeComponent();
        HeadRadio.IsEnabled = segments.Any(s => s.IsHead);
        BuildSegmentList();
        Recalculate();
    }

    private void BuildSegmentList()
    {
        var fg = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF4));
        foreach (var seg in _segments)
        {
            string label = string.IsNullOrEmpty(seg.Name) ? $"S{seg.Index + 1}" : seg.Name;
            var cb = new CheckBox
            {
                Content  = $"{label}  —  {seg.VolumeCm3:0.000} cm³",
                IsChecked = true,
                Foreground = fg,
                FontSize = 11,
                Margin   = new Thickness(0, 2, 0, 0),
                Tag      = seg.Index
            };
            cb.Checked   += (_, _) => Recalculate();
            cb.Unchecked += (_, _) => Recalculate();
            SegmentListPanel.Children.Add(cb);
            _segChecks.Add(cb);
        }
    }

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (SegmentListBorder is null) return;
        SegmentListBorder.Visibility = CustomRadio.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        Recalculate();
    }

    private void WeightBox_TextChanged(object sender, TextChangedEventArgs e) => Recalculate();

    private void Recalculate()
    {
        double vol = GetSelectedVolume();
        if (VolumeDisplay is not null)
            VolumeDisplay.Text = vol > 0 ? $"{vol:0.000} cm³" : "— cm³";

        bool weightOk = TryParseWeight(WeightBox?.Text, out double weightG);
        bool canApply = weightOk && vol > 0;

        if (DensityDisplay is not null)
            DensityDisplay.Text = canApply ? $"{weightG / vol:0.000}" : "—";

        if (ApplyBtn is not null)
            ApplyBtn.IsEnabled = canApply;
    }

    private double GetSelectedVolume()
    {
        if (WholeLineRadio?.IsChecked == true)
            return _segments.Sum(s => s.VolumeCm3);
        if (HeadRadio?.IsChecked == true)
            return _segments.Where(s => s.IsHead).Sum(s => s.VolumeCm3);
        // Custom
        return _segChecks
            .Where(cb => cb.IsChecked == true)
            .Select(cb => _segments.FirstOrDefault(s => s.Index == (int)cb.Tag!))
            .Where(s => s is not null)
            .Sum(s => s!.VolumeCm3);
    }

    private IEnumerable<int> GetSelectedIndices()
    {
        if (WholeLineRadio?.IsChecked == true)
            return _segments.Select(s => s.Index);
        if (HeadRadio?.IsChecked == true)
            return _segments.Where(s => s.IsHead).Select(s => s.Index);
        return _segChecks
            .Where(cb => cb.IsChecked == true)
            .Select(cb => (int)cb.Tag!);
    }

    private static bool TryParseWeight(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Accept both period and comma as decimal separator
        string normalised = text.Trim().Replace(',', '.');
        return double.TryParse(normalised, NumberStyles.Float,
                               CultureInfo.InvariantCulture, out value)
               && value > 0;
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        double vol = GetSelectedVolume();
        if (!TryParseWeight(WeightBox?.Text, out double weightG) || vol <= 0) return;

        ResultDensity    = weightG / vol;
        IsWholeLineScope = WholeLineRadio.IsChecked == true;
        AffectedIndices  = GetSelectedIndices().ToList();
        DialogResult     = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
