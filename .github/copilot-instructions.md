# Copilot Instructions – flylineprofiler

## Architecture Overview

This project is a **line-diameter measurement system** with three distinct components:

### 1. `master/master.ino` — ESP32 Firmware
- Reads a **quadrature encoder** (A/B interrupts on GPIO 12/13) → length in cm using `PULSES_PER_CM = 30`.
- Reads a **digital caliper** (DATA/CLOCK on GPIO 27/26) for diameter.
- Stores measured points in a **sorted linked list** (`DataPoint*`) keyed by `cm` position; duplicate cm values update the existing node.
- Applies **EMA smoothing** (alpha hardcoded at `0.1f` in `addDataPoint`) before storing diameter.
- Compensated diameter: `compensated = displayValue − displayZeroValue − caliperZeroOffset`.
- Persists `caliperZeroOffset` in **EEPROM at address 0**.
- Serves the entire **web UI as a raw string literal** embedded directly in `master.ino` via `R"rawliteral(...)rawliteral"`.
- Exposes **HTTP on port 80** and **WebSocket on port 81** (`WebSocketsServer`).
- Connects to Wi-Fi via **WiFiManager** (AP SSID: `DiametroLinea_Setup`, password: `12345678`).
- Relays motor commands to the slave over **UART2** (TX=GPIO17, RX=GPIO16) at 115200 8N1.

### 2. `slave/slave.ino` — ESP32-S3 Firmware
- Controls a TB6600 stepper driver via **FastAccelStepper** (PUL=GPIO4, DIR=GPIO5, ENA=GPIO6).
- Receives ASCII commands over **UART2** (RX=GPIO21, TX=GPIO18) at 115200 8N1.
- Maintains a `Mode` enum: `STOPPED | SCAN | FAST_S | FAST_O | GOTOPOS`.
- `GOTOPOS` uses quadratic-curve dynamic speed reduction near the target (`DISTANCE_THRESHOLD = 500` steps, `MIN_SPEED_HZ = 300`).
- Buttons on GPIO 10/11/12 (`INPUT_PULLUP`, active LOW) with 250 ms debounce.

### 3. `app/` — WPF .NET Desktop App
- A Windows desktop alternative to the browser UI; the ESP32 backend is unchanged.
- MVVM architecture: `ViewModels/MainViewModel.cs`, `Services/BackendClient.cs`, `Services/SettingsService.cs`.
- `BackendClient` connects to `ws://<host>:81/` using `ClientWebSocket`; it raises `RawMessageReceived` for every JSON frame.
- Settings are loaded from `appsettings.json` into `AppSettings` → `BackendSettings` + `ChartSettings`.
- Chart integration (ScottPlot WPF) is a placeholder; NuGet package must be added.

---

## UART Protocol

### Master → Slave (newline-terminated ASCII)
| Command | Meaning |
|---|---|
| `SCAN` | Start slow scan (`SCAN_HZ = 1500`) |
| `STOP` | Stop motor |
| `FAST_S` | Fast, same direction (`FAST_HZ = 12000`) |
| `FAST_O` | Fast, opposite direction |
| `STATUS?` | Request status |
| `GOTOPOS:<cm>:<max_hz>:<F\|B>` | Move to absolute position in cm |

### Slave → Master
`STATUS:<MODE>:<DIR>[:<remaining_steps>]`  
Examples: `STATUS:SCAN:FWD`, `STATUS:GOTOPOS:FWD:1500`, `STATUS:STOP:FWD`

---

## WebSocket JSON Events (Master → Browsers)
- `{"type":"motor","mode":"SCAN","dir":"FWD"}` — motor state update
- `{"type":"goto_status","active":true,"target":150.0,"current":42.0}` — GOTOPOS started
- `{"type":"goto_progress","remaining_cm":50,"current_cm":100,"target_cm":150}` — GOTOPOS in progress
- `{"type":"speed","speed":1.23}` — current speed in cm/s
- Measurement points are broadcast when `scanEnabled` is true and cm position changes

---

## CSV Format
Export endpoint `/export` and local desktop export use:
```
Dataset,Lunghezza cm,Diametro mm,Display mm
```
Import supports headers `Dataset,Lunghezza (cm),Diametro (mm)` or bare `cm,mm` pairs.

---

## Key Constants (must stay in sync between master and slave)
| Constant | Value | Location |
|---|---|---|
| `PULSES_PER_CM` | `30` | Both `master.ino` and `slave.ino` |
| `SCAN_HZ` | `1500` | `slave.ino` |
| `FAST_HZ` / `MOTOR_FAST_HZ` | `12000` | Both |
| UART baud | `115200, 8N1` | Both |

---

## Build & Flash

### Arduino Firmware
- Use **Arduino IDE** or **PlatformIO**.
- Board targets: **ESP32** for master, **ESP32-S3** for slave.
- Flash **slave first**, then master.
- Master libraries: `WiFi`, `WebServer`, `WebSocketsServer`, `EEPROM`, `WiFiManager`.
- Slave libraries: `FastAccelStepper`.

### WPF Desktop App
```sh
cd app
dotnet build
dotnet run
```

---

## Conventions & Gotchas

- The encoder ISR is marked `IRAM_ATTR` (`updateEncoder`) — keep ISR code minimal and RAM-resident.
- `motorQueueTx` drops a `STATUS?` poll if a real command is already queued; do not bypass this gate.
- The web UI is embedded inline in `master.ino`; HTML/CSS/JS changes require re-flashing the master.
- When a new browser connects it gets only future WebSocket frames; full history must be fetched from `/export` first.
- Optimal scanning speed is **0.5–2.5 cm/s** (hardcoded in JS as `OPTIMAL_SPEED_MIN/MAX`).
- `caliperZeroOffset` survives resets because it is EEPROM-backed; `displayZeroValue` is RAM-only and resets on reboot.
- The desktop app's `appsettings.json` is the canonical place to change host/port; do not hard-code them in C#.
