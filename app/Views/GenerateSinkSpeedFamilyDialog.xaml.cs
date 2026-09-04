using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DiametroLineaDesktop.Views;

public partial class GenerateSinkSpeedFamilyDialog : Window
{
    private readonly List<double> _speeds = new(); // in/s; 0 = floating

    public List<double> SelectedSpeedsIns { get; private set; } = new();

    public GenerateSinkSpeedFamilyDialog()
    {
        InitializeComponent();
        UpdateSpeedLabel();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateSpeedLabel();

    private void UpdateSpeedLabel()
    {
        if (SpeedValueLabel == null) return; // ValueChanged can fire mid-InitializeComponent, before this field is assigned
        double v = SpeedSlider.Value;
        SpeedValueLabel.Text = v < 0.05 ? "Floating" : $"{v:0.00} in/s";
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        double v = SpeedSlider.Value < 0.05 ? 0.0 : Math.Round(SpeedSlider.Value, 2);
        if (_speeds.Contains(v)) return;
        _speeds.Add(v);
        _speeds.Sort();
        RefreshList();
    }

    private void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SpeedsListBox.SelectedIndex < 0) return;
        _speeds.RemoveAt(SpeedsListBox.SelectedIndex);
        RefreshList();
    }

    private void RefreshList()
    {
        SpeedsListBox.ItemsSource = _speeds
            .Select(v => v <= 0 ? "Floating" : $"{v:0.00} in/s")
            .ToList();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectedSpeedsIns = new List<double>(_speeds);
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
