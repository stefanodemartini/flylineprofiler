# flylineprofiler — Coding Context

> Firmware section last updated: 2026-05-29 (v0.4.22, stable, working) | WPF app section refreshed: 2026-08-05

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
- Encoder true PPC (30.21) slightly differs from formula (30.0) — EEPROM value takes precedence; formula is the fallback if EEPROM is blank.
- WPF app: nearly all Design Mode logic (nozzles, compensation, plotting, PDF/CSV export) lives in `MainWindow.xaml.cs` code-behind (~4600 lines) rather than in `MainViewModel`. `MainViewModel` only handles the SCAN/backend half. Worth a ViewModel split if the file keeps growing.

---

## Verified accuracy (v0.4.22)

| Test | Error |
|---|---|
| encoder_test (no caliper), 500 cm | 0.4% |
| master.ino software ISR (with caliper) | ~5% (caliper ISR interference) |
| master.ino PCNT hardware, 100 cm | **0.28%** ✅ |

---

## WPF Desktop App (`app/`)

> App section last verified: 2026-08-05 (code had drifted ~2 months ahead of this doc — nozzle system, NC→C compensation and PDF export below did not exist when this doc was last fully written).

Windows alternative to the browser UI. Connects to the same ESP32 backend over WebSocket/HTTP. Also the primary tool now for **designing** fly line profiles (Design Mode) independent of any hardware scan — see `MANUALE.md` for full user-facing docs (Italian).

### Stack
| Item | Value |
|---|---|
| Target | .NET 8 WinExe, WPF |
| MVVM | `CommunityToolkit.Mvvm` 8.4 (`ObservableObject`) — used only by `MainViewModel` (SCAN/backend side) |
| UI shell | `Fluent.Ribbon` 10.0 |
| Chart | `ScottPlot.WPF` 5.0.52 |
| PDF export | `FlyLinePdfExporter.cs` (custom, no 3rd-party PDF lib dependency noted here — see file) |
| Project files | `.flp` (JSON, saved in `Documents\FlyLineProfiler\Projects\`) |

### File structure
```
app/
├── Models/
│   ├── AppSettings.cs         — AppSettings, BackendSettings, ChartSettings, MeasurementPoint
│   ├── FlyLineProject.cs      — FlyLineProject (.flp schema), NozzleDefinition, NozzleZone,
│   │                             ProjectImportedSeries, ProjectDesignNode, ProjectSegmentMeta,
│   │                             LineColorSection (legacy, kept for migration)
│   ├── ProjectSegment.cs      — INotifyPropertyChanged segment: volume/mass/taper/sink speed +
│   │                             per-slice compensation results (CompSlice*, HasClampedSlices)
│   ├── LineColorSectionVm.cs  — NozzleDefinitionVm / NozzleZoneVm (view-model wrappers for nozzles)
│   └── DesignNode.cs          — single design node on the chart
├── Services/
│   ├── BackendClient.cs       — WebSocket client (ClientWebSocket) + HTTP /export fetcher;
│   │                             reassembles fragmented WS frames, single-fire Disconnected event
│   ├── ProjectService.cs      — Save/Load .flp files (JSON)
│   ├── RecentFilesService.cs  — "Open Recent" MRU list
│   ├── SettingsService.cs     — Load/save appsettings.json
│   ├── SinkingSpeedCalc.cs    — Physics engine (bisection solver): per-cylinder, tapered-segment,
│   │                             whole-line, CompensateProfile (NC→C), DensityForTargetSinkSpeed
│   └── FlyLinePdfExporter.cs  — ~900 lines; NC/C production-worksheet PDF export
├── ViewModels/
│   └── MainViewModel.cs       — SCAN-mode ViewModel only: backend connect/scan/motor control,
│                                 GOTOPOS, live chart points. ~430 lines.
├── Views/
│   ├── MainWindow.xaml/.cs    — code-behind IS the app: Design Mode, nozzle system, compensation
│   │                             (NC→C), sink map, AFFTA badge, PDF/CSV export, chart interaction.
│   │                             ~4600 lines — the real "controller" of the app, not MainViewModel.
│   ├── SettingsWindow.xaml/.cs
│   ├── AboutDialog.xaml/.cs
│   ├── WeightToDensityDialog.xaml/.cs      — density from measured mass ("From Weight")
│   ├── SinkSpeedToDensityDialog.xaml/.cs   — density from target sink speed ("From Sink Speed")
│   └── InverseBoolConverter.cs
├── appsettings.json           — Host, ports, chart options (canonical config location)
└── DiametroLineaDesktop.csproj
```

**Note on architecture:** despite the name, `MainViewModel` only owns the hardware-SCAN half of the app (WebSocket connect, live points, motor/GOTOPOS). All Design Mode logic — nozzles, segments, compensation, physics glue, plotting, PDF/CSV export, project load/save — lives directly in `MainWindow.xaml.cs` as code-behind, not in a ViewModel. `MainWindow` implements its own `INotifyPropertyChanged` for XAML bindings.

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
| `HasCompensation` / `HasClampedSlices` | True after `SetCompensation()`; clamped = any slice hit `RhoFloor` |
| `CompSliceXsCm/DiamsMm/Densities/Clamped` | Per-1cm-slice compensated profile arrays, set by `SetCompensation()` |

### `SinkingSpeedCalc.cs` — physics engine
Bisection solver for cylinder drag. Units: mm / cm / g/cm³ in, m/s out. `RhoFloor = 0.94 g/cm³` (min practical material density).

| Method | Description |
|---|---|
| `CylinderSinkSpeed(isSalt, tempC, diamMm, densGcm3)` | Single uniform cylinder terminal speed |
| `TaperedSegmentSinkSpeed(...)` | Tapered segment sliced into N cylinders, single shared equilibrium speed |
| `LineSinkSpeed(...)` | Whole line (all segments) as one rigid body at a uniform density |
| `DensityForTargetSinkSpeed(...)` | Inverse: density (g/cm³) needed for the whole line to hit a target speed |
| `LineSinkSpeedRange(...)` | (min, max) achievable speed for the line across ρ 1.001–2.5 |
| `CompensateProfile(isSalt, tempC, startDiam, endDiam, lengthCm, densityGcm3, targetSpeedMs, sliceLenCm=1)` | NC→C: per 1cm slice, new diameter + density at `targetSpeedMs`, mass-conserved (`ρ_new·d_new² = ρ_orig·d_orig²`); density floored at `RhoFloor`, floored slices flagged in `clamped[]` |

Drag model: `Cd = 1 + 10/Re^(2/3)`. Bisection: 100 iterations, tol=1e-12.  
Water: fresh density = 5th-order polynomial; viscosity = lookup table (0–40 °C). Salt: `ρ=1027−0.2T`, `ν=1.07×ν_fresh`.

### Nozzle system (M1–M4) — replaces old `ColorSections`
A project has exactly 4 `NozzleDefinition` slots (color + density g/cm³ + label), each assignable to zero or more `NozzleZone`s (start cm, end cm → nozzle index). This is how both design coloring and physical extrusion material are specified.

- **M1's color always drives `DesignColor`** — the main profile line color on the chart.
- Unused nozzles (no zone assigned, density = 0) auto-label `"N/A"` (`UpdateNozzleUsageLabels()`).
- `NozzleCountBadge` shows `x/4` active nozzles (`UpdateNozzleBadge()`).
- Old `.flp` files with `ColorSections` are migrated on load: each distinct color becomes a nozzle (up to 4), zones map 1:1.
- `SyncNozzleDensitiesFromComp` / comp mode: when a C (compensated) profile is shown, M1–M4 are temporarily auto-populated with the 4 K-means-quantized densities from the comp gradient (color = density gradient, label = `ρ X.XX`); the original NC nozzle colors/labels/densities are cached in `_ncNozzleColors`/`_ncNozzleLabels` and restored on switching back to NC.

### Compensation (NC → C)
`compensation.md`, `compensation_physics.md`, `comp_design_report.md` contain the original design rationale. Implemented state:

- **One file, no separate C project**: comp results live in the same `.flp` (`CompTargetSpeedMs`, `ShowCompProfile`), computed on the fly from the NC segments + target speed — not a separate save.
- **Workflow**: set material density → line must classify as Sinking → **⚖ Compensate** computes C with a density gradient → a speed slider appears, range = `LineSinkSpeedRange` (V_min slowest slice … V_max fastest slice), default V_max.
- Dragging the slider recomputes C live (`ComputeCompensation()` → `SinkingSpeedCalc.CompensateProfile` per segment).
- **Show C** toggles NC/C display; **Show NC ghost** overlays the original NC profile translucent-gray for comparison.
- C profile rendered with Lambert-cylindrical shading, colored by density gradient (blue=light → red=heavy); adjacent slices overlap 18% to hide anti-alias seams between swatches.
- Floating projects (`IsSinking == false`) hide compensation entirely — physically meaningless.
- Density floor 0.94 g/cm³ (`RhoFloor`): slices that would need to go lighter are clamped and flagged (`HasClampedSlices`) with a UI warning.
- Segments table: NC and Comp are separate column groups; **Sink is always the rightmost column**.

### PDF export (`FlyLinePdfExporter.cs`, ~900 lines)
- If the project has a saved C profile, export prompts **NC or C** and suffixes the filename `_NC` / `_C`.
- Content: header (name/date/NC-or-C), profile chart (3200×600px) with node/segment labels, full segment table, info card (core type, laser mark, color note), nozzle swatches (Lambert-gradient, matching in-app legend at 0.00 precision), AFFTA badge + CoM/Rg + taper classification.
- C-specific PDF text is English-only per design decision; density zones and clamped-slice warnings called out explicitly.

### AFFTA badge & mass-analysis chart
- Status bar badge: `AFFTA  LW <n>  <weight> gr  ✓/✗` — computed from mass of the first 30 ft (914.4 cm, AFFTA standard length); ✓ if within ±6 gr of the nominal class.
- **Full Line** toggle enables a "Head" column in the segments table; when set, CoM/Rg/AFFTA calculations use only head segments.
- Mass-analysis chart (Design Mode, below main chart): red bars = head segments, blue = running line, ◆ = per-segment center of mass, dashed line = total CoM, yellow dashed = AFFTA 30ft boundary, green curve = cumulative weight.

### `FlyLineProject.cs` — `.flp` save format (JSON)
```json
{
  "Name": "...",
  "UseSharedDensity": true,
  "SharedDensityGCm3": 0.65,
  "IsSinking": true,
  "IsFullLine": false,
  "WaterType": "fresh",
  "WaterTempC": 20.0,
  "ScanPoints": [{"X": 1, "RawY": 0.85, "FilteredY": 0.82}],
  "ImportedSeries": [{"Name": "...", "Xs": [], "Ys": [], "ColorHex": "#28C996"}],
  "DesignNodes": [{"X": 5.0, "Y": 1.2}],
  "SegmentMetadata": [{"StartCm": 0, "EndCm": 10, "Name": "Head", "SpecWeight": 0.65, "IsHead": true}],
  "NozzleDefinitions": [{"ColorHex": "DC3232", "DensityGCm3": 0.65, "Label": ""}],
  "NozzleZones": [{"StartCm": 0, "EndCm": 100, "NozzleIndex": 0}],
  "CompTargetSpeedMs": 0.038,
  "ShowCompProfile": true,
  "ColorSections": []
}
```
`ColorSections` is a legacy field kept only so old files still load; new files always use `NozzleDefinitions`/`NozzleZones`.

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
