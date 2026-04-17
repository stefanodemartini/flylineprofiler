using System.Collections.ObjectModel;
using System.Threading.Tasks;
using DiametroLineaDesktop.Helpers;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Services;

namespace DiametroLineaDesktop.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly BackendClient _backend = new();

    private AppSettings _settings;

    private string _connectionStatus = "Disconnesso";
    private string _motorState = "--";
    private string _currentLength = "0 cm";
    private string _currentDiameter = "0.00 mm";
    private string _currentDisplay = "0.00 mm";
    private string _speed = "0.00 cm/s";
    private string _pointsCount = "0";
    private string _gotoStatus = "";
    private string _lastUpdate = "--";
    private string _logText = "";

    private double? _ema;

    public ObservableCollection<MeasurementPoint> Points { get; } = new();

    public AppSettings Settings => _settings;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public string MotorState
    {
        get => _motorState;
        set => SetProperty(ref _motorState, value);
    }

    public string CurrentLength
    {
        get => _currentLength;
        set => SetProperty(ref _currentLength, value);
    }

    public string CurrentDiameter
    {
        get => _currentDiameter;
        set => SetProperty(ref _currentDiameter, value);
    }

    public string CurrentDisplay
    {
        get => _currentDisplay;
        set => SetProperty(ref _currentDisplay, value);
    }

    public string Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    public string PointsCount
    {
        get => _pointsCount;
        set => SetProperty(ref _pointsCount, value);
    }

    public string GotoStatus
    {
        get => _gotoStatus;
        set => SetProperty(ref _gotoStatus, value);
    }

    public string LastUpdate
    {
        get => _lastUpdate;
        set => SetProperty(ref _lastUpdate, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }

    public MainViewModel()
    {
        _settings = _settingsService.Load();

        _backend.Connected += () =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatus = "Connesso";
                AppendLog("WebSocket connesso");
            });
        };

        _backend.Disconnected += () =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatus = "Disconnesso";
                AppendLog("WebSocket disconnesso");
            });
        };

        _backend.RawMessageReceived += OnRawMessage;
    }

    public async Task InitializeAsync()
    {
        if (!_settings.Backend.AutoConnect)
        {
            ConnectionStatus = "Pronto";
            AppendLog("AutoConnect disattivato");
            return;
        }

        await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        try
        {
            ConnectionStatus = "Connessione in corso...";
            AppendLog($"Connessione a ws://{_settings.Backend.Host}:{_settings.Backend.WebSocketPort}/");

            await _backend.ConnectAsync(_settings.Backend);

            ConnectionStatus = "Connesso";

            if (_settings.Backend.LoadParamsOnConnect)
                await _backend.SendAsync("getparams");

            if (_settings.Backend.LoadMotorStatusOnConnect)
                await _backend.SendAsync("motor status");
        }
        catch (TaskCanceledException ex)
        {
            ConnectionStatus = "Timeout connessione";
            AppendLog("Timeout connessione WebSocket: " + ex.Message);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Errore connessione";
            AppendLog("Errore connessione: " + ex);
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await _backend.DisconnectAsync();
            ConnectionStatus = "Disconnesso";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Errore disconnessione";
            AppendLog("Errore disconnessione: " + ex);
        }
    }

    public async Task SendCommandAsync(string cmd)
    {
        try
        {
            AppendLog("> " + cmd);
            await _backend.SendAsync(cmd);
        }
        catch (Exception ex)
        {
            AppendLog("Errore invio comando: " + ex.Message);
        }
    }

    public void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
            AppendLog("Impostazioni salvate");
        }
        catch (Exception ex)
        {
            AppendLog("Errore salvataggio impostazioni: " + ex.Message);
        }
    }

public AppSettings CloneSettings()
{
    return new AppSettings
    {
        Backend = new BackendSettings
        {
            ProfileName = _settings.Backend.ProfileName,
            Host = _settings.Backend.Host,
            WebSocketPort = _settings.Backend.WebSocketPort,
            HttpPort = _settings.Backend.HttpPort,
            AutoConnect = _settings.Backend.AutoConnect,
            ReconnectSeconds = _settings.Backend.ReconnectSeconds,
            ConnectTimeoutSeconds = _settings.Backend.ConnectTimeoutSeconds,
            LoadParamsOnConnect = _settings.Backend.LoadParamsOnConnect,
            LoadMotorStatusOnConnect = _settings.Backend.LoadMotorStatusOnConnect
        },
        Chart = new ChartSettings
        {
            ShowFilteredSeries = _settings.Chart.ShowFilteredSeries,
            ShowRawSeries = _settings.Chart.ShowRawSeries,
            AutoFit = _settings.Chart.AutoFit,
            Theme = _settings.Chart.Theme,
            XAxisUnit = _settings.Chart.XAxisUnit,
            SmoothingAlpha = _settings.Chart.SmoothingAlpha,
            LineWidth = _settings.Chart.LineWidth
        }
    };
}

public void ApplySettings(AppSettings newSettings)
{
    _settings = newSettings;
    SaveSettings();
    AppendLog("Nuove impostazioni applicate");
}

    private void OnRawMessage(string msg)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                AppendLog("< " + msg);
                LastUpdate = DateTime.Now.ToLongTimeString();

                using var doc = BackendClient.TryParseJson(msg);
                if (doc is null)
                    return;

                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeElement))
                {
                    var type = typeElement.GetString();

                    switch (type)
                    {
                        case "params":
                            AppendLog("Parametri ricevuti");
                            break;

                        case "speed":
                            if (root.TryGetProperty("speed", out var sp))
                                Speed = $"{sp.GetDouble():0.00} cm/s";
                            break;

                        case "motor":
                            var mode = root.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : "--";
                            var dir = root.TryGetProperty("dir", out var dirEl) ? dirEl.GetString() : "--";
                            MotorState = $"{mode} {dir}";
                            break;

                        case "goto_status":
                            {
                                var active = root.TryGetProperty("active", out var act) && act.GetBoolean();
                                var completed = root.TryGetProperty("completed", out var comp) && comp.GetBoolean();

                                if (active)
                                {
                                    var target = root.TryGetProperty("target", out var targetEl) ? targetEl.GetDouble() : 0;
                                    var current = root.TryGetProperty("current", out var currentEl) ? currentEl.GetDouble() : 0;
                                    GotoStatus = $"GOTOPOS attivo - attuale {current:0.0} cm / target {target:0.0} cm";
                                }
                                else if (completed)
                                {
                                    GotoStatus = "Target raggiunto";
                                }
                                else
                                {
                                    var reason = root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : "stopped";
                                    GotoStatus = $"Movimento interrotto ({reason})";
                                }
                            }
                            break;

                        case "goto_progress":
                            {
                                var remaining = root.TryGetProperty("remaining_cm", out var remEl) ? remEl.GetDouble() : 0;
                                var current = root.TryGetProperty("current_cm", out var curEl) ? curEl.GetDouble() : 0;
                                var target = root.TryGetProperty("target_cm", out var tarEl) ? tarEl.GetDouble() : 0;

                                GotoStatus = $"GOTOPOS: {current:0.0} / {target:0.0} cm, restano {remaining:0.0} cm";
                                CurrentLength = $"{current:0.0} cm";
                            }
                            break;

                        case "scan_enabled":
                            if (root.TryGetProperty("enabled", out var enabledEl))
                            {
                                var enabled = enabledEl.GetBoolean();
                                AppendLog("Ricezione " + (enabled ? "ON" : "OFF"));
                            }
                            break;
                    }

                    return;
                }

                if (root.TryGetProperty("cm", out var cmEl))
                {
                    var x = cmEl.GetDouble();
                    CurrentLength = $"{x:0.0} cm";

                    if (root.TryGetProperty("diameter", out var diaEl))
                    {
                        var y = diaEl.GetDouble();
                        var raw = root.TryGetProperty("rawDisplay", out var rawEl) ? rawEl.GetDouble() : y;

                        CurrentDiameter = $"{y:0.00} mm";
                        CurrentDisplay = $"{raw:0.00} mm";

                        AddPoint(x, raw, y);
                    }

                    if (root.TryGetProperty("totalPoints", out var tpEl))
                        PointsCount = tpEl.ToString();
                }
            }
            catch (Exception ex)
            {
                AppendLog("Errore parsing messaggio: " + ex.Message);
            }
        });
    }

    private void AddPoint(double x, double rawY, double y)
    {
        var alpha = _settings.Chart.SmoothingAlpha;
        _ema = _ema is null ? y : alpha * y + (1 - alpha) * _ema.Value;

        var filtered = Math.Round(_ema.Value, 3);

        var existing = Points.FirstOrDefault(p => Math.Abs(p.X - x) < 0.0001);

        if (existing is null)
        {
            Points.Add(new MeasurementPoint
            {
                X = x,
                RawY = rawY,
                FilteredY = filtered
            });
        }
        else
        {
            existing.RawY = rawY;
            existing.FilteredY = filtered;
        }

        PointsCount = Points.Count.ToString();
    }

    private void AppendLog(string line)
    {
        LogText += (string.IsNullOrWhiteSpace(LogText) ? "" : Environment.NewLine) + line;
    }
}