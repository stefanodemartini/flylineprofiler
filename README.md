# Line Diameter Monitoring System (ESP32 Master + ESP32‑S3 Motor Slave)

A two‑microcontroller embedded system that measures **line diameter in real time** along the **unwound length** (encoder), visualizes it on a **web dashboard** (Chart.js), exports the live dataset as **CSV**, and controls a **stepper motor traction unit** via a dedicated motor controller board.

This repo is organized as two Arduino sketches:

- `master.ino` → ESP32 Master: sensors + web UI + WebSocket + UART to motor slave  
- `slave.ino`  → ESP32‑S3 Slave: TB6600 stepper control + physical buttons + UART protocol

---

## Why two boards?

- The **Master (ESP32)** handles Wi‑Fi/HTTP/WebSocket plus sensor acquisition and data storage.
- The **Slave (ESP32‑S3)** handles time‑critical stepper pulses (STEP/DIR/ENA) and local buttons, receiving high‑level commands from the master over UART.

This separation keeps motor timing isolated from networking load.

---

## Features

### Master (`master.ino`)
- Encoder reading with interrupts (A/B) → length in cm.
- Digital caliper reading (DATA/CLOCK).
- Processing:
  - Display “zero” reference (set from UI)
  - Persistent offset stored in EEPROM
  - EMA smoothing (`ALPHA`)
  - Compensated diameter:
    - `compensated = displayValue - displayZeroValue - caliperZeroOffset`
- Stores points in RAM as linked list: `{cm, compensatedDiameter, rawDisplay}`.
- Web UI:
  - Chart.js live plot (zoom/pan)
  - Current speed (cm/s)
  - Motor status display (from slave)
  - Buttons for motor commands (scan/stop/fast)
  - CSV upload for dataset comparison (overlay on chart)
  - CSV export of all acquired live data

### Slave (`slave.ino`)
- Stepper control via TB6600 using **FastAccelStepper**.
- Local physical buttons (active LOW) to toggle:
  - SCAN
  - FAST same direction
  - FAST opposite direction
- UART command interface from master:
  - `SCAN`, `STOP`, `FAST_S`, `FAST_O`, `STATUS?`
- Sends status back:
  - `STATUS:<MODE>:<DIR>`

---

## Hardware

### Master (ESP32)
- ESP32 DevKit / ESP32‑WROOM
- Incremental encoder (quadrature A/B)
- Digital caliper output (clock/data)
- Optional Wi‑Fi reset button
- UART link to the motor slave

### Slave (ESP32‑S3)
- ESP32‑S3 board
- TB6600 (or compatible) stepper driver
- Stepper motor
- 3 push buttons (optional)

---

## GPIO pinout

### ESP32 Master (`master.ino`)

| Function | GPIO | Notes |
|---|---:|---|
| Encoder channel A | 12 | `INPUT_PULLUP`, interrupt |
| Encoder channel B | 13 | `INPUT_PULLUP`, interrupt |
| Caliper DATA | 27 | input |
| Caliper CLOCK | 26 | input |
| Wi‑Fi reset | 14 | `INPUT_PULLUP`, LOW resets WiFiManager settings |
| UART to Slave (Master RX2) | 16 | RX pin on master |
| UART to Slave (Master TX2) | 17 | TX pin on master |

### ESP32‑S3 Slave (`slave.ino`)

| Function | GPIO | Notes |
|---|---:|---|
| TB6600 STEP / PUL | 4 | `PUL_PIN` |
| TB6600 DIR | 5 | `DIR_PIN` |
| TB6600 ENA | 6 | `ENA_PIN` (library controlled) |
| Button: SCAN | 10 | `INPUT_PULLUP`, active LOW |
| Button: FAST same dir | 11 | `INPUT_PULLUP`, active LOW |
| Button: FAST opposite dir | 12 | `INPUT_PULLUP`, active LOW |
| UART from Master (Slave RX2) | 21 | RX pin on slave |
| UART to Master (Slave TX2) | 18 | TX pin on slave |

---

## Wiring

### 1) UART Master ↔ Slave

Cross TX/RX and share ground:

- Master **TX (GPIO17)** → Slave **RX (GPIO21)**
- Master **RX (GPIO16)** ← Slave **TX (GPIO18)**
- **GND Master ↔ GND Slave** (mandatory)

UART settings: **115200, 8N1**

### 2) TB6600 control wiring (Common Cathode)

This project uses **common cathode** wiring for TB6600 inputs.

TB6600 inputs are pairs:
- `PUL+ / PUL-` (STEP)
- `DIR+ / DIR-` (DIR)
- `ENA+ / ENA-` (ENABLE)

With **common cathode**:
- Connect all negative inputs to **GND** (common reference):
  - `PUL-`, `DIR-`, `ENA-` → GND
- Drive the positive inputs with ESP32‑S3 GPIO:
  - `PUL+` ← GPIO4
  - `DIR+` ← GPIO5
  - `ENA+` ← GPIO6 (if enable is used)

Important:
- Always share **GND** between the TB6600 control side and the ESP32‑S3.
- TB6600 “clone” boards may differ; verify with your module’s labeling.

### 3) Encoder → Master
- Encoder A → GPIO12  
- Encoder B → GPIO13  
- Power encoder according to its specs (watch for 5V encoders vs 3.3V GPIO).

### 4) Caliper → Master
- Caliper DATA → GPIO27  
- Caliper CLOCK → GPIO26  
- Ensure common ground and safe logic levels.

---

## Protocols

### UART commands (Master → Slave)
One ASCII command per line (`\n` terminated):

- `SCAN`
- `STOP`
- `FAST_S` (fast, same direction)
- `FAST_O` (fast, opposite direction)
- `STATUS?`

### UART status (Slave → Master)
- `STATUS:<MODE>:<DIR>`

Examples:
- `STATUS:SCAN:FWD`
- `STATUS:FAST_OPP:BWD`
- `STATUS:STOPPED:FWD`

### Browser updates (Master → WebSocket clients)
The master broadcasts JSON messages for:
- Parameters (`displayZero`, `offset`)
- Speed (`speed`)
- Motor state (`mode`, `dir`)
- Measurement points (`cm`, `diameter`, `rawDisplay`, `totalPoints`)

---

## CSV export

The master exposes `/export` and generates a CSV with header:

`Dataset,Lunghezza cm,Diametro mm,Display mm`

Each row corresponds to one acquired point in RAM.

---

## Multi‑browser behavior (history vs live)

The master streams **only new points** via WebSocket.  
If a second browser connects later, it will initially see only points acquired after it connected.

Recommended approach:
- At page load, the UI fetches `/export` to rebuild the full dataset,
- Then it connects to WebSocket for live updates.

---

## Build & Flash

### Requirements
- Arduino IDE (or PlatformIO)
- ESP32 board support
- Libraries used in `master.ino`:
  - `WiFi`, `WebServer`, `WebSocketsServer`, `EEPROM`, `WiFiManager`
- Libraries used in `slave.ino`:
  - `FastAccelStepper`

### Flash order
1. Flash `slave.ino` to the ESP32‑S3 (motor controller).
2. Flash `master.ino` to the ESP32 (web + sensors).

### Wi‑Fi provisioning (Master)
The master uses WiFiManager. On first boot (or after Wi‑Fi reset), it opens an AP:
- SSID: `DiametroLineaSetup`
- Password: `12345678`
- Portal IP: `192.168.4.1`

---

## Troubleshooting

### CSV export does not download
Ensure the server sends `Content-Disposition` header **before** sending the response body.

### No full chart on a new browser
Load history from `/export` on page load, then use WebSocket for live updates.

### Motor does not respond to UI commands
- Check UART cross wiring and common ground.
- Confirm slave is running and responds to `STATUS?`.

### Motor vibration / wrong direction
- Verify TB6600 wiring (common cathode) and that STEP/DIR/ENA go to the correct pins.
- Check TB6600 DIP switches (current and microstepping) match your motor and mechanics.

---

## License
Add a `LICENSE` file (MIT is a common choice).

---

## Author
Stefano De Martini
