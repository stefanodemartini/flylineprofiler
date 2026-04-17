using System.Windows;
using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Views;

public partial class SettingsWindow : Window
{
    public AppSettings EditableSettings { get; }

    public SettingsWindow(AppSettings settingsCopy)
    {
        InitializeComponent();
        EditableSettings = settingsCopy;
        DataContext = EditableSettings;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}