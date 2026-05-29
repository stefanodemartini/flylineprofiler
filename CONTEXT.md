# flylineprofiler — Coding Context

> Last updated: 2026-05-29 | Firmware: v0.4.22 | Status: **stable, working**

---

## What this system does

Measures the diameter profile of a fishing line as it is wound/unwound from a spool.
A wheel rolls on the line surface and drives a quadrature encoder (length). A digital caliper head reads the instantaneous diameter. An ESP32 (master) collects the data, hosts a web UI, and sends motor commands to an ESP32-S3 (slave) via UART.

---

## Hardware

| Component | GPIO (master ESP32) | Notes |
|---|---|---|
| Encoder A phase | 12 | PCNT pulse input |
| Encoder B phase | 13 | PCNT direction control |
| Caliper DATA | 27 | ISR on CLOCK |
| Caliper CLOCK | 26 | `CHANGE` interrupt → `onCaliperChange()` |
| WiFi reset button | 14 | Active LOW → wipes WiFiManager settings |
| Motor UART TX | 17 | → slave RX GPIO21 |
| Motor UART RX | 16 | ← slave TX GPIO18 |

**Encoder wheel:** 600 PPR, circumference = 200 mm → **PPC = 30.0 formula value**.  
**Measured PPC:** 30.134 (encoder_test 500 cm), 30.21 (master PCNT 100 cm test).  
**Current EEPROM-stored PPC:** 30.21 (set by user, survives reboot).

**Slave:** ESP32-S3, TB6600 stepper (PUL=GPIO4, DIR=GPIO5, ENA=GPIO6), FastAccelStepper library.

---

## File map

```
master/master.ino       ← ESP32 master: encoder, caliper, web UI, WebSocket, motor relay
slave/slave.ino         ← ESP32-S3: stepper motor control, receives UART commands
encoder_test/           ← Diagnostic sketch (encoder + WiFiManager only, no caliper)
app/                    ← WPF .NET desktop app (alternative to browser UI)
CONTEXT.md              ← This file
```

---

## Firmware architecture (master.ino v0.4.22)

### Key constants (lines 41–45)
```cpp
#define ENCODER_PPR     600
#define WHEEL_CIRC_MM   200
#define PULSES_PER_CM   ((ENCODER_PPR * 10.0f) / WHEEL_CIRC_MM)  // = 30.0
#define EEPROM_ADDR_CALIB_PPC 4   // float, 4 bytes
```

### EEPROM layout
| Address | Type | Content |
|---|---|---|
| 0 | float (4 B) | `caliperZeroOffset` — caliper mechanical zero |
| 4 | float (4 B) | `calibratedPpc` — measured pulses/cm; 0 = use formula |

### Hardware PCNT encoder (lines 107–161)
Replaced software `attachInterrupt` encoder in v0.4.18. Root cause of prior ~5% error: caliper ISR (~4000 interrupts/sec) was interfering with the software encoder ISR, causing it to read stale GPIO state.

```cpp
// PCNT_UNIT_0, 1× decoding:
// A-fall + B=0 → INC (forward)
// A-rise + B=0 → DEC (reverse)
// Overflow at ±30000 → pcntOverflowISR() accumulates into pcntAccum
long getEncoderValue()  // atomic composite read (retry loop)
void setEncoderValue(v) // pause PCNT, clear, set pcntAccum, resume
```

**Startup offset:** `setEncoderValue(ceil(2 × getActivePpc()))` so display starts at 0 cm.  
**Position formula:** `cm = getEncoderValue() / getActivePpc() - 2.0`

### PPC selection (line 227)
```cpp
inline float getActivePpc() {
  return (calibratedPpc >= 5.0f && calibratedPpc <= 500.0f) ? calibratedPpc : (float)PULSES_PER_CM;
}
```

### Caliper (non-blocking, lines ~292–450)
- ISR: `onCaliperChange()` — fires on `CHANGE` of CALIPER_CLOCK_PIN
- Main loop: `pumpCaliper()` — decodes completed packets, fills `calRollingBuf[7]`
- Read: `readCaliperBufferedMedian()` — median of rolling buffer (no stall)
- NaN/Inf guard applied before JSON broadcast (v0.4.21)

**Compensated diameter:**
```cpp
compensatedDiameter = displayValue - displayZeroValue - caliperZeroOffset;
// rounded to 0.01 mm precision
```
- `caliperZeroOffset`: EEPROM-backed, set by `zero` command
- `displayZeroValue`: RAM-only, resets on reboot, set by `setzero` command

### Scan loop (lines 2069–2095)
```cpp
int currentCm = (int)(encSnap / getActivePpc()) - 2;   // encoder position in cm

if (!isGoToActive && currentCm != lastCm && currentCm >= 0 && scanEnabled) {
    lastCm = currentCm;
    float displayValue = readCaliperBufferedMedian();
    float compensatedDiameter = displayValue - displayZeroValue - caliperZeroOffset;
    // NaN guard...
    int actualCm = currentCm;   // ← direct encoder position (v0.4.22 fix)
    addDataPoint(actualCm, currentCm, compensatedDiameter, displayValue);
    // broadcast JSON: {"cm": actualCm, "diameter": ..., "rawDisplay": ..., "totalPoints": ...}
}
```

> **v0.4.22 critical fix:** `actualCm = currentCm` directly. Prior code used `actualPositionCm += 1.0f` (odometer) which overcounted during line oscillation/bounce.

### DataPoint linked list (line 95)
```cpp
struct DataPoint {
  int cm;           // chart X axis (encoder-based position, 0-referenced)
  int encoderCm;    // raw encoder cm — used by goToPosition() for GOTOPOS slave command
  float diameter;   // compensated diameter mm (stored for CSV export)
  float rawDisplay; // raw caliper display mm
  DataPoint* next;
};
```
Sorted by `cm`. Duplicate `cm` values update the existing node. Max ~1000 points before memory concern.

### Chart display
- Y axis shows `diameter / 2` (radius) — mirrored profile = real diameter height visually
- Tooltip shows `abs(y) × 2` to restore real diameter
- CSV export stores full diameter mm

### Motor relay (UART2, lines 57–59)
```cpp
HardwareSerial SerialMotor(2);   // RX=16, TX=17, 115200 8N1
```
Commands queued via `motorQueueTx()`. Duplicate STATUS? dropped if real command pending.

### GOTOPOS (lines 447–515)
```cpp
void goToPosition(float targetCm)
```
- Suspends scan, saves `oldScanState`
- Looks up `encoderCm` from DataPoint list for the target `actualCm`
- Sends: `GOTOPOS:<encoderCm+2>:<MOTOR_FAST_HZ>:<encNow>:<F|B>`
- Overshoot guard: if encoder passes target by >1 cm, forces STOP
- Restores scan state on completion

### Encoder watchdog (lines 2055–2067)
Auto-sends `STOP` to motor after 5 s of encoder inactivity. Suppressed during GOTOPOS.

### Web server endpoints
| URL | Method | Description |
|---|---|---|
| `/` | GET | Full web UI (embedded in `R"rawliteral(...)"`) |
| `/export` | GET | CSV export: `Dataset,Lunghezza cm,Diametro mm,Display mm` |
| `/import` | POST | CSV import |
| `/params` | GET | JSON of current parameters |
| `/encoder` | GET | Diagnostic: `{"ticks","pcntAccum","hwCounter","ppc","cm","scanEnabled","totalPoints"}` |

**WebSocket port 81** — new clients get full history only via `/export`; live frames only on connect.

### WebSocket JSON events (master → browsers)
```json
{"cm": 42, "diameter": 0.85, "rawDisplay": 0.95, "totalPoints": 42}
{"type": "motor", "mode": "SCAN", "dir": "FWD"}
{"type": "goto_status", "active": true, "target": 150.0, "current": 42.0}
{"type": "goto_progress", "remaining_cm": 50, "current_cm": 100, "target_cm": 150}
{"type": "speed", "speed": 1.23}
{"type": "scan_enabled", "value": true}
```

### Serial / WebSocket commands (handleCommand)
| Command | Effect |
|---|---|
| `scan_on` | Enable scan (blocked during GOTOPOS) |
| `scan_off` | Disable scan |
| `reset` | Zero encoder + clear all data |
| `resetpos` | Zero encoder only, keep data; sets `calStartEncoder` baseline |
| `zero` | Set caliper zero offset (EEPROM) |
| `setzero` | Set display zero (RAM only) |
| `readenc` | Serial print: ticks, pcntAccum, HW counter, PPC, cm |
| `readraw` | Serial print: raw caliper reading |
| `calibrate:<cm>` | Compute PPC from encoder delta since last `resetpos`, store in EEPROM |
| `goto:<cm>` | Move motor to position cm |
| `scan` / `stop` / `fast_s` / `fast_o` | Motor commands |

### Calibration workflow
1. Send `resetpos` (zeroes encoder, saves `calStartEncoder`)
2. Pull exactly N cm of line
3. Send `calibrate:<N>` → computes and stores new PPC in EEPROM

---

## UART protocol (master ↔ slave)

### Master → Slave
| Command | Meaning |
|---|---|
| `SCAN` | Slow scan at `SCAN_HZ_INIT = 1500` |
| `STOP` | Stop motor |
| `FAST_S` | Fast same direction (`MOTOR_FAST_HZ = 12000`) |
| `FAST_O` | Fast opposite direction |
| `STATUS?` | Poll slave status |
| `GOTOPOS:<cm>:<max_hz>:<encNow>:<F\|B>` | Move to absolute encoder-cm position |

### Slave → Master
```
STATUS:<MODE>:<DIR>[:<remaining_steps>]
```
Examples: `STATUS:SCAN:FWD`, `STATUS:GOTOPOS:FWD:1500`, `STATUS:STOP:FWD`

---

## Known issues / open items

- `actualPositionCm` variable (line 184) is still declared and reset in `resetpos`/`clearAllData` but is **no longer updated in the scan loop** (v0.4.22). It is now unused dead code — safe to remove in a future cleanup.
- Chart uses CDN for Chart.js — no internet → blank chart. Consider embedding the JS locally.
- WPF app (`app/`) ScottPlot NuGet package is a placeholder — not yet added.
- Encoder true PPC (30.21) slightly differs from formula (30.0) — EEPROM value takes precedence; formula is the fallback if EEPROM is blank.

---

## Verified accuracy (v0.4.22)

| Test | Error |
|---|---|
| encoder_test (no caliper), 500 cm | 0.4% |
| master.ino software ISR (with caliper) | ~5% (caliper ISR interference) |
| master.ino PCNT hardware, 100 cm | **0.28%** ✅ |

---

## WPF Desktop App (`app/`)

Windows alternative to the browser UI. Connects to the same ESP32 backend over WebSocket/HTTP.

### Stack
| Item | Value |
|---|---|
| Target | .NET 8 WinExe, WPF |
| MVVM | `CommunityToolkit.Mvvm` 8.4 (`ObservableObject`) |
| UI shell | `Fluent.Ribbon` 10.0 |
| Chart | `ScottPlot.WPF` 5.0.52 |
| Project files | `.flp` (JSON, saved in `Documents\FlyLineProfiler\Projects\`) |

### File structure
```
app/
├── Models/
│   ├── AppSettings.cs         — AppSettings, BackendSettings, ChartSettings, MeasurementPoint
│   ├── FlyLineProject.cs      — FlyLineProject, ProjectImportedSeries, ProjectDesignNode, ProjectSegmentMeta
│   ├── ProjectSegment.cs      — INotifyPropertyChanged segment with physics (volume, mass, taper, sink speed)
│   └── DesignNode.cs          — single design node on the chart
├── Services/
│   ├── BackendClient.cs       — WebSocket client (ClientWebSocket) + HTTP /export fetcher
│   ├── ProjectService.cs      — Save/Load .flp files (JSON)
│   ├── SettingsService.cs     — Load/save appsettings.json
│   └── SinkingSpeedCalc.cs    — Physics engine (bisection solver)
├── ViewModels/
│   └── MainViewModel.cs       — Single ViewModel for main window
├── Views/
│   ├── MainWindow.xaml/.cs    — Main UI with ScottPlot WpfPlot chart
│   ├── SettingsWindow.xaml/.cs
│   └── InverseBoolConverter.cs
├── appsettings.json           — Host, ports, chart options (canonical config location)
└── DiametroLineaDesktop.csproj
```

### `appsettings.json` (canonical config — never hardcode in C#)
```json
{
  "Backend": {
    "Host": "192.168.1.50",
    "WebSocketPort": 81,
    "HttpPort": 80,
    "AutoConnect": false,
    "ReconnectSeconds": 3,
    "ConnectTimeoutSeconds": 5,
    "LoadParamsOnConnect": true,
    "LoadMotorStatusOnConnect": true
  },
  "Chart": {
    "ShowFilteredSeries": true,
    "ShowRawSeries": false,
    "AutoFit": true,
    "Theme": "Light",
    "SmoothingAlpha": 0.10
  }
}
```

### `BackendClient.cs`
- Connects to `ws://<host>:81/`
- `RawMessageReceived` event fires for every JSON frame
- `SendAsync(string)` — sends a command string over WebSocket
- `FetchExportCsvAsync()` — HTTP GET `http://<host>:80/export`
- `TryParseJson(string)` — safe static helper, returns null on parse failure
- `WebSocketException` / `IOException` on ESP32 remote-close silently swallowed (normal for ESP32)

### `MainViewModel.cs`
- `ObservableObject` from CommunityToolkit.Mvvm
- `Points: ObservableCollection<MeasurementPoint>` — live data for chart
- `SmoothingEnabled` — client-side EMA (alpha = `ChartSettings.SmoothingAlpha`); toggling resets `_ema` state
- `CanControl` / `CanEnableScan` — derived booleans (both false during GOTOPOS) for XAML button disabling
- `LoadHistoryAsync()` — fetches `/export` CSV on connect, populates `Points`
- Auto-reconnect: schedules `ConnectAsync` after `ReconnectSeconds` on disconnect
- All `_backend` event callbacks dispatched to UI thread via `App.Current.Dispatcher.Invoke()`

### `ProjectSegment.cs` — physics model
Each segment is a frustum (truncated cone) or cylinder. Key computed properties:

| Property | Formula |
|---|---|
| `VolumeCm3` | Frustum: `π·L/3·(r1²+r1·r2+r2²)/1000`, Cylinder: `π·r²·L/1000` |
| `MassG` | `VolumeCm3 × SpecWeightGCm3` (0 if density not set) |
| `TaperMmPerMeter` | `(EndDiam − StartDiam) / (LengthCm / 100)` |
| `SinkSpeedText` | in/s (`m/s × 39.3701`), positive = sinking |
| `HasCompensation` | True after `SetCompensation()` called |

### `SinkingSpeedCalc.cs` — physics engine
Bisection solver for cylinder drag. Units: mm / cm / g/cm³ in, m/s out.

| Method | Description |
|---|---|
| `CylinderSinkSpeed(isSalt, tempC, diamMm, densGcm3)` | Single uniform cylinder terminal speed |
| `TaperedSegmentSinkSpeed(...)` | Tapered segment sliced into N cylinders, single shared equilibrium speed |
| `CompensateProfile(...)` | Per slice: new diameter + density at a given target speed (mass conserved) |

Drag model: `Cd = 1 + 10/Re^(2/3)`. Bisection: 100 iterations, tol=1e-12.  
Water: fresh density = 5th-order polynomial; viscosity = lookup table (0–40 °C). Salt: `ρ=1027−0.2T`, `ν=1.07×ν_fresh`.

### `FlyLineProject.cs` — `.flp` save format (JSON)
```json
{
  "Name": "...",
  "UseSharedDensity": true,
  "SharedDensityGCm3": 0.65,
  "WaterType": "fresh",
  "WaterTempC": 20.0,
  "ScanPoints": [{"X": 1, "RawY": 0.85, "FilteredY": 0.82}],
  "ImportedSeries": [{"Name": "...", "Xs": [], "Ys": [], "ColorHex": "#28C996"}],
  "DesignNodes": [{"X": 5.0, "Y": 1.2}],
  "SegmentMetadata": [{"StartCm": 0, "EndCm": 10, "Name": "Head", "SpecWeight": 0.65, "IsHead": true}]
}
```

---

## Build & flash

### master (ESP32)
- Board: **esp32:esp32:esp32**
- Libraries: `WiFi`, `WebServer`, `WebSocketsServer`, `EEPROM`, `WiFiManager`, `driver/pcnt.h` (IDF built-in)
- Flash **slave first**, then master

### slave (ESP32-S3)
- Board: **esp32:esp32:esp32s3**
- Library: `FastAccelStepper`

### WPF app
```sh
cd app
dotnet build
dotnet run
```

---

## WiFi
- WiFiManager AP: SSID `DiametroLinea_Setup`, password `12345678`
- Portal timeout: 180 s
- Reset: hold GPIO 14 LOW at boot
