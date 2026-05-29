using System.ComponentModel;

namespace DiametroLineaDesktop.Models;

/// <summary>
/// A design node used for the editable nodes DataGrid in the Project panel.
/// node.X = position in cm, node.Y = full diameter in mm.
/// </summary>
public class DesignNode : INotifyPropertyChanged
{
    private double _positionCm;
    private double _diameterMm;

    public double PositionCm
    {
        get => _positionCm;
        set { _positionCm = value; OnPropertyChanged(nameof(PositionCm)); }
    }

    public double DiameterMm
    {
        get => _diameterMm;
        set { _diameterMm = value; OnPropertyChanged(nameof(DiameterMm)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
