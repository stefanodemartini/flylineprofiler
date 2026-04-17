using System.Collections.ObjectModel;
using System.Globalization;
using DiametroLineaDesktop.Helpers;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Services;

namespace DiametroLineaDesktop.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = new();
    private readonly BackendClient _backend = new();
    private AppSettings _settings;

    private bool _shouldReconnect;
    private CancellationTokenSource? _reconnectCts;
    private double? _ema;

    // ── Events for the view ──────────────────────────────────────────────────
    public event Action? OnConnected;
    public event Action? OnDataCleared;

    // ── Observable properties ────────────────────────────────────────────────
    private string _connectionStatus = "Disconnesso";
    private string _motorState = "--";
    private string _currentLength = "0 cm";
    private string _currentDiameter = "0.00 mm";
    private string _currentDisplay = "0.00 mm";
    private string _speed = "0.00 cm/s";
    private string _speedStatus = "—";
    private string _pointsCount = "0";
    private string _gotoStatus = "";
    private string _lastUpdate = "--";
    private string _logText = "";
    private string _displayZeroText = "0.00 mm";
    private string _offsetText = "0.00 mm";
    private bool _scanReceiving;
    private bool _isGoToActive;
    private bool _smoothingEnabled = true;

    public ObservableCollection<MeasurementPoint> Points { get; } = new();
    public AppSettings Settings => _settings;

    public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value); }
    public string MotorState       { get => _motorState;       set => SetProperty(ref _motorState, value); }
    public string CurrentLength    { get => _currentLength;    set => SetProperty(ref _currentLength, value); }
    public string CurrentDiameter  { get => _currentDiameter;  set => SetProperty(ref _currentDiameter, value); }
    public string CurrentDisplay   { get => _currentDisplay;   set => SetProperty(ref _currentDisplay, value); }
    public string Speed            { get => _speed;            set => SetProperty(ref _speed, value); }
    public string SpeedStatus      { get => _speedStatus;      set => SetProperty(ref _speedStatus, value); }
    public string PointsCount      { get => _pointsCount;      set => SetProperty(ref _pointsCount, value); }
    public string GotoStatus       { get => _gotoStatus;       set => SetProperty(ref _gotoStatus, value); }
    public string LastUpdate       { get => _lastUpdate;       set => SetProperty(ref _lastUpdate, value); }
    public string LogText          { get => _logText;          set => SetProperty(ref _logText, value); }
    public string DisplayZeroText  { get => _displayZeroText;  set => SetProperty(ref _displayZeroText, value); }
    public string OffsetText       { get => _offsetText;       set => SetProperty(ref _offsetText, value); }

    public bool SmoothingEnabled
    {
        get => _smoothingEnabled;
        set
        {
            if (SetProperty(ref _smoothingEnabled, value))
                _ema = null; // reset EMA state when toggled
        }
    }

    public bool ScanReceiving
    {
        get => _scanReceiving;
        set
        {
            if (SetProperty(ref _scanReceiving, value))
                OnPropertyChanged(nameof(CanEnableScan));
        }
    }

    public bool IsGoToActive
    {
        get => _isGoToActive;
        set
        {
            if (SetProperty(ref _isGoToActive, value))
            {
                OnPropertyChanged(nameof(CanControl));
                OnPropertyChanged(nameof(CanEnableScan));
            }
        }
    }

    // Derived enables — used by XAML to disable buttons during GOTOPOS
    public bool CanControl    => !_isGoToActive;
    public bool CanEnableScan => !_scanReceiving && !_isGoToActive;

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainViewModel()
    {
        _settings = _settingsService.Load();

        _backend.Connected += () =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatus = "Connesso";
                AppendLog("WebSocket connesso");
                OnConnected?.Invoke();
            });
        };

        _backend.Disconnected += () =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatus = "Disconnesso";
                AppendLog("WebSocket disconnesso");
                if (_shouldReconnect && _settings.Backend.ReconnectSeconds > 0)
                    ScheduleReconnect();
            });
        };

        _backend.RawMessageReceived += OnRawMessage;
    }

    // ── Auto-reconnect ───────────────────────────────────────────────────────
    private void ScheduleReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;
        ConnectionStatus = $"Riconnessione tra {_settings.Backend.ReconnectSeconds}s…";
        Task.Delay(TimeSpan.FromSeconds(_settings.Backend.ReconnectSeconds), token)
            .ContinueWith(async t =>
            {
                if (!t.IsCanceled)
                    await ConnectAsync(initiatedByUser: false);
            }, TaskScheduler.Default);
    }

    // ── Public API ───────────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        if (!_settings.Backend.AutoConnect)
        {
            ConnectionStatus = "Pronto";
            AppendLog("AutoConnect disattivato");
            return;
        }
        await ConnectAsync(initiatedByUser: true);
    }

    public async Task ConnectAsync(bool initiatedByUser = true)
    {
        if (initiatedByUser) _shouldReconnect = true;
        _reconnectCts?.Cancel();

        try
        {
            ConnectionStatus = "Connessione in corso…";
            AppendLog($"Connessione a ws://{_settings.Backend.Host}:{_settings.Backend.WebSocketPort}/");
            await _backend.ConnectAsync(_settings.Backend);

            if (_settings.Backend.LoadParamsOnConnect)
                await _backend.SendAsync("getparams");
            if (_settings.Backend.LoadMotorStatusOnConnect)
                await _backend.SendAsync("motor status");
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Errore connessione";
            AppendLog("Errore: " + ex.Message);
            if (_shouldReconnect && _settings.Backend.ReconnectSeconds > 0)
                ScheduleReconnect();
        }
    }

    public async Task DisconnectAsync()
    {
        _shouldReconnect = false;
        _reconnectCts?.Cancel();
        try
        {
            await _backend.DisconnectAsync();
            ConnectionStatus = "Disconnesso";
        }
        catch (Exception ex)
        {
            AppendLog("Errore disconnessione: " + ex.Message);
        }
    }

    /// <summary>
    /// Fetches /export from the ESP32 HTTP server and populates Points with the full history.
    /// Call this from the view after subscribing to OnConnected, with CollectionChanged temporarily unhooked.
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        AppendLog("Caricamento storico da /export…");
        try
        {
            var csv = await _backend.FetchExportCsvAsync(_settings.Backend);
            if (csv is null)
            {
                AppendLog("Storico non disponibile (HTTP /export fallito)");
                return;
            }

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = 0;
            bool first = true;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim('\r', ' ', '"');
                if (string.IsNullOrEmpty(line)) continue;

                // Skip header row
                if (first)
                {
                    first = false;
                    if (line.Length > 0 && char.IsLetter(line[0])) continue;
                }

                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (!TryParseDouble(parts[0], out double x)) continue;
                if (!TryParseDouble(parts[1], out double y)) continue;

                // History is already EMA-smoothed by firmware; store as-is and seed the live EMA
                Points.Add(new MeasurementPoint { X = x, RawY = y, FilteredY = y });
                _ema = y;
                count++;
            }

            PointsCount = Points.Count.ToString();
            AppendLog($"Storico caricato: {count} punti");
        }
        catch (Exception ex)
        {
            AppendLog("Errore caricamento storico: " + ex.Message);
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
            AppendLog("Errore invio: " + ex.Message);
        }
    }

    public void ClearAllData()
    {
        Points.Clear();
        _ema = null;
        PointsCount = "0";
        OnDataCleared?.Invoke();
    }

    public void SaveSettings()
    {
        try { _settingsService.Save(_settings); AppendLog("Impostazioni salvate"); }
        catch (Exception ex) { AppendLog("Errore salvataggio: " + ex.Message); }
    }

    public AppSettings CloneSettings() => new AppSettings
    {
        Backend = new BackendSettings
        {
            ProfileName           = _settings.Backend.ProfileName,
            Host                  = _settings.Backend.Host,
            WebSocketPort         = _settings.Backend.WebSocketPort,
            HttpPort              = _settings.Backend.HttpPort,
            AutoConnect           = _settings.Backend.AutoConnect,
            ReconnectSeconds      = _settings.Backend.ReconnectSeconds,
            ConnectTimeoutSeconds = _settings.Backend.ConnectTimeoutSeconds,
            LoadParamsOnConnect        = _settings.Backend.LoadParamsOnConnect,
            LoadMotorStatusOnConnect   = _settings.Backend.LoadMotorStatusOnConnect
        },
        Chart = new ChartSettings
        {
            ShowFilteredSeries = _settings.Chart.ShowFilteredSeries,
            ShowRawSeries      = _settings.Chart.ShowRawSeries,
            AutoFit            = _settings.Chart.AutoFit,
            Theme              = _settings.Chart.Theme,
            XAxisUnit          = _settings.Chart.XAxisUnit,
            SmoothingAlpha     = _settings.Chart.SmoothingAlpha,
            LineWidth          = _settings.Chart.LineWidth
        }
    };

    public void ApplySettings(AppSettings newSettings)
    {
        _settings = newSettings;
        SaveSettings();
        AppendLog("Nuove impostazioni applicate");
    }

    // ── Message dispatcher ───────────────────────────────────────────────────
    private void OnRawMessage(string msg)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                LastUpdate = DateTime.Now.ToLongTimeString();

                using var doc = BackendClient.TryParseJson(msg);
                if (doc is null) return;
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                {
                    switch (typeEl.GetString())
                    {
                        case "params":
                            var dz  = root.TryGetProperty("displayZero", out var dzEl)  ? dzEl.GetDouble()  : 0;
                            var off = root.TryGetProperty("offset",      out var offEl) ? offEl.GetDouble() : 0;
                            DisplayZeroText = $"{dz:0.00} mm";
                            OffsetText      = $"{off:0.00} mm";
                            AppendLog($"Params ricevuti: zeroDisplay={dz:0.00} mm, offset={off:0.00} mm");
                            break;

                        case "speed":
                            if (root.TryGetProperty("speed", out var spEl))
                            {
                                var spd = spEl.GetDouble();
                                Speed = $"{spd:0.00} cm/s";
                                SpeedStatus = spd < 0.5  ? "Troppo lenta" :
                                              spd > 2.5  ? "Troppo alta"  : "Ottimale";
                            }
                            break;

                        case "motor":
                            var mode = root.TryGetProperty("mode", out var mEl) ? mEl.GetString() ?? "--" : "--";
                            var dir  = root.TryGetProperty("dir",  out var dEl) ? dEl.GetString() ?? "--" : "--";
                            MotorState = $"{mode} {dir}";
                            break;

                        case "scan_enabled":
                            ScanReceiving = root.TryGetProperty("value", out var seEl) && seEl.GetBoolean();
                            AppendLog("Ricezione: " + (ScanReceiving ? "ON" : "OFF"));
                            break;

                        case "goto_status":
                        {
                            var active    = root.TryGetProperty("active",    out var actEl)  && actEl.GetBoolean();
                            var completed = root.TryGetProperty("completed", out var compEl) && compEl.GetBoolean();
                            IsGoToActive = active;
                            if (active)
                            {
                                var target  = root.TryGetProperty("target",  out var tEl) ? tEl.GetDouble()  : 0;
                                var current = root.TryGetProperty("current", out var cEl) ? cEl.GetDouble()  : 0;
                                GotoStatus = $"GOTOPOS attivo: {current:0.0} → {target:0.0} cm";
                            }
                            else
                            {
                                GotoStatus = completed ? "✔ Target raggiunto" : "Movimento interrotto";
                                AppendLog(GotoStatus);
                            }
                            break;
                        }

                        case "goto_progress":
                        {
                            var rem = root.TryGetProperty("remaining_cm", out var remEl) ? remEl.GetDouble() : 0;
                            var cur = root.TryGetProperty("current_cm",   out var curEl) ? curEl.GetDouble() : 0;
                            var tar = root.TryGetProperty("target_cm",    out var tarEl) ? tarEl.GetDouble() : 0;
                            GotoStatus    = $"GOTOPOS: {cur:0.0}/{tar:0.0} cm — mancano {rem:0.0} cm";
                            CurrentLength = $"{cur:0.0} cm";
                            break;
                        }
                    }
                    return;
                }

                // Live measurement point: {cm, diameter, rawDisplay, totalPoints}
                if (root.TryGetProperty("cm", out var cmEl))
                {
                    var x = cmEl.GetDouble();
                    CurrentLength = $"{x:0.0} cm";

                    if (root.TryGetProperty("diameter", out var diaEl))
                    {
                        var y   = diaEl.GetDouble();
                        var raw = root.TryGetProperty("rawDisplay", out var rawEl) ? rawEl.GetDouble() : y;
                        CurrentDiameter = $"{y:0.00} mm";
                        CurrentDisplay  = $"{raw:0.00} mm";
                        AddLivePoint(x, raw, y);
                    }

                    if (root.TryGetProperty("totalPoints", out var tpEl))
                        PointsCount = tpEl.GetInt32().ToString();
                }
            }
            catch (Exception ex)
            {
                AppendLog("Errore parsing: " + ex.Message);
            }
        });
    }

    private void AddLivePoint(double x, double rawY, double firmwareY)
    {
        double filtered;
        if (_smoothingEnabled)
        {
            var alpha = _settings.Chart.SmoothingAlpha;
            _ema = _ema is null ? firmwareY : alpha * firmwareY + (1 - alpha) * _ema.Value;
            filtered = Math.Round(_ema.Value, 3);
        }
        else
        {
            _ema = null;
            filtered = Math.Round(firmwareY, 3);
        }

        // Remove existing point at same cm to trigger CollectionChanged (→ plot refresh)
        var existing = Points.FirstOrDefault(p => Math.Abs(p.X - x) < 0.0001);
        if (existing is not null)
            Points.Remove(existing);

        Points.Add(new MeasurementPoint { X = x, RawY = rawY, FilteredY = filtered });
        PointsCount = Points.Count.ToString();
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.GetCultureInfo("it-IT"), out value);

    public void AppendLog(string line) =>
        LogText += (string.IsNullOrWhiteSpace(LogText) ? "" : Environment.NewLine) + line;
}
