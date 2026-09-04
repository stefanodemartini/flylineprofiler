using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DiametroLineaDesktop.Views;

public partial class GenerateLineFamilyDialog : Window
{
    private readonly List<CheckBox> _checkboxes = new();
    public List<int> SelectedWeights { get; private set; } = new();

    public GenerateLineFamilyDialog(int currentLw)
    {
        InitializeComponent();

        for (int lw = 1; lw <= 14; lw++)
        {
            var cb = new CheckBox { Tag = lw };
            if (lw == currentLw)
            {
                cb.Content   = $"#{lw} (current)";
                cb.IsEnabled = false;
            }
            else
            {
                cb.Content = $"#{lw}";
            }
            _checkboxes.Add(cb);
            WeightsGrid.Children.Add(cb);
        }

        if (currentLw > 0)
            SubText.Text = $"This design is currently AFFTA #{currentLw}. Select the classes to generate.";
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectedWeights = _checkboxes.Where(cb => cb.IsChecked == true)
                                      .Select(cb => (int)cb.Tag!)
                                      .ToList();
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
