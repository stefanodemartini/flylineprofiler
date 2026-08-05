using System.Windows;

namespace DiametroLineaDesktop.Views;

public enum OverlaySelection { Design, Scan, Both }

public partial class OverlaySelectDialog : Window
{
    public OverlaySelection Selection { get; private set; } = OverlaySelection.Both;

    public OverlaySelectDialog(string projectName)
    {
        InitializeComponent();
        SubText.Text = $"“{projectName}” has both a design profile and a scan profile. Choose what to overlay.";
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        Selection = DesignRadio.IsChecked == true ? OverlaySelection.Design
                   : ScanRadio.IsChecked  == true ? OverlaySelection.Scan
                   : OverlaySelection.Both;
        DialogResult = true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
