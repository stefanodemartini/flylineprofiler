using System.ComponentModel;

namespace DiametroLineaDesktop.Models;

/// <summary>
/// View-model for a coloured band painted over the profile, editable in the DataGrid.
/// StartCm / EndCm are positions along the line in cm.
/// ColorHex is a 6-char RGB string without '#' (e.g. "DC3232").
/// </summary>
public class LineColorSectionVm : INotifyPropertyChanged
{
    private double _startCm;
    private double _endCm;
    private string _colorHex = "DC3232";
    private string _label    = string.Empty;

    public double StartCm
    {
        get => _startCm;
        set { _startCm = value; OnPropertyChanged(nameof(StartCm)); }
    }

    public double EndCm
    {
        get => _endCm;
        set { _endCm = value; OnPropertyChanged(nameof(EndCm)); }
    }

    /// <summary>6-char RGB hex, no '#'. E.g. "DC3232" for red.</summary>
    public string ColorHex
    {
        get => _colorHex;
        set { _colorHex = (value ?? "DC3232").TrimStart('#').ToUpperInvariant(); OnPropertyChanged(nameof(ColorHex)); }
    }

    public string Label
    {
        get => _label;
        set { _label = value ?? string.Empty; OnPropertyChanged(nameof(Label)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
