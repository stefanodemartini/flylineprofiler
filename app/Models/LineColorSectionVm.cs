using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DiametroLineaDesktop.Models;

/// <summary>
/// ViewModel for one nozzle definition (M1–M4).
/// Color + density are both fixed properties of the nozzle material.
/// </summary>
public class NozzleDefinitionVm : INotifyPropertyChanged
{
    private string _colorHex    = "DC3232";
    private double _densityGCm3 = 0.0;
    private string _label       = string.Empty;

    public int Number { get; init; }   // 1-based display number (M1 … M4)

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            _colorHex = (value ?? "DC3232").TrimStart('#').ToUpperInvariant();
            OnPropertyChanged(nameof(ColorHex));
        }
    }

    public double DensityGCm3
    {
        get => _densityGCm3;
        set { _densityGCm3 = value; OnPropertyChanged(nameof(DensityGCm3)); }
    }

    public string Label
    {
        get => _label;
        set { _label = value ?? string.Empty; OnPropertyChanged(nameof(Label)); }
    }

    public string DisplayName => $"M{Number}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// ViewModel for a zone assigned to one nozzle.
/// The zone's colour and density are derived entirely from the assigned nozzle.
/// </summary>
public class NozzleZoneVm : INotifyPropertyChanged
{
    private readonly ObservableCollection<NozzleDefinitionVm> _nozzles;
    private double _startCm;
    private double _endCm;
    private int    _nozzleIndex;

    public NozzleZoneVm(ObservableCollection<NozzleDefinitionVm> nozzles)
    {
        _nozzles = nozzles;
    }

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

    /// <summary>0-based index into the Nozzles collection.</summary>
    public int NozzleIndex
    {
        get => _nozzleIndex;
        set
        {
            _nozzleIndex = Math.Clamp(value, 0, 3);
            OnPropertyChanged(nameof(NozzleIndex));
            OnPropertyChanged(nameof(ColorHex));
            OnPropertyChanged(nameof(DensityGCm3));
            OnPropertyChanged(nameof(NozzleLabel));
        }
    }

    // Derived from nozzle definition
    public string ColorHex    => _nozzleIndex < _nozzles.Count ? _nozzles[_nozzleIndex].ColorHex    : "888888";
    public double DensityGCm3 => _nozzleIndex < _nozzles.Count ? _nozzles[_nozzleIndex].DensityGCm3 : 0;
    public string NozzleLabel => _nozzleIndex < _nozzles.Count ? _nozzles[_nozzleIndex].DisplayName  : $"M{_nozzleIndex + 1}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
