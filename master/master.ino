
#include <WiFi.h>
#include <WebServer.h>
#include <WebSocketsServer.h>
#include <EEPROM.h>
#include <WiFiManager.h>
#include <HardwareSerial.h>

// ===============================
// FW
// ===============================
#define FIRMWARE_VERSION "0.4.0"
#define FIRMWARE_DATE "2026-04-17"
#define FIRMWARE_FEATURES "WiFi Manager + EMA + 0.01mm + UART Motor + Scan timer + Autostop + RicezioneON/OFF + GOTOPOS + Caliper timeout + Atomic encoder + Watchdog fixes"

// -----------------------------
#define ENCODER_DATA_PIN 12
#define ENCODER_CLOCK_PIN 13
#define CALIPER_DATA_PIN 27
#define CALIPER_CLOCK_PIN 26
#define WIFI_RESET_PIN 14
#define PULSES_PER_REV 600
#define PULSES_PER_CM 30
#define EEPROM_SIZE 512

float lineDiameter = 0.0;
bool scanEnabled = false;
bool oldScanState = false;  // Per salvare stato scansione durante GOTOPOS

// FW-14: removed dead #define ALPHA 0.05 (actual EMA uses hardcoded 0.1f)

float smoothedDiameter = 0.0;
bool filterInitialized = false;
float emaStore = 0.0f;
bool emaStoreInit = false;

static const int MOTOR_UART_RX_PIN = 16;
static const int MOTOR_UART_TX_PIN = 17;
HardwareSerial SerialMotor(2);

String motorMode = "UNKNOWN";
String motorDir = "UNKNOWN";
unsigned long motorLastSeenMs = 0;

static char motorRxLine[48];
static size_t motorRxLen = 0;
static String motorTxPending;
static bool motorTxHasPending = false;

static unsigned long encoderLastMoveMs = 0;
static long encoderLastValueForWatchdog = 0;
static bool encoderWatchdogStopSent = false;

static unsigned long lastMotorPollMs = 0;
static const unsigned long MOTOR_POLL_INTERVAL_MS = 2000;
static const unsigned long MOTOR_STALE_MS = 4000;

// Parametri per GOTOPOS
const uint32_t MOTOR_FAST_HZ = 12000;
bool isGoToActive = false;
float goToTargetCm = 0;
unsigned long lastGoToStatusCheck = 0;
bool goToEncoderReached = false;  // set when encoder reaches target; used to send completed:true

struct DataPoint {
  int cm;
  float diameter;
  float rawDisplay;
  DataPoint* next;
};

DataPoint* firstDataPoint = nullptr;
DataPoint* lastDataPoint = nullptr;
int totalDataPoints = 0;

volatile long encoderValue = 0;
volatile int lastEncoded = 0;

// Caliper ISR state — written only in onCaliperChange(), read under noInterrupts()
volatile long  calBitAccum   = 0;
volatile int   calSignAccum  = 1;
volatile int   calBitCount   = 0;
volatile long  calRawValue   = 0;
volatile int   calSign       = 1;
volatile bool  calDataReady  = false;
volatile unsigned long calRiseUs = 0;

float caliperZeroOffset = 0.0;
float displayZeroValue = 0.0;
int lastCm = -1;

unsigned long lastSpeedTime = 0;
long lastSpeedEncoder = 0;
float currentSpeed = 0.0;

WiFiManager wifiManager;
WebServer server(80);
WebSocketsServer webSocket(81);

// Forward declarations
void readCaliper();
float readCaliperAverage(int samples = 3);
float readCaliperDisplay();
void handleCommand(String cmd);
void sendParamsToClients();
void calculateSpeed();
void addDataPoint(int cm, float diameter, float rawDisplay);
void clearAllData();
int getTotalDataPoints();
void setDisplayZero();
void motorQueueTx(const String& lineNoNewline);
void motorPumpTx();
void motorPumpRx();
void motorPollIfNeeded();
void motorHandleStatusLine(const char* line);
void goToPosition(float targetCm);
void checkGoToStatus();

// -----------------------------
void addDataPoint(int cm, float diameter, float rawDisplay) {
  DataPoint* newPoint = new DataPoint();
  if (!newPoint) { Serial.println("[OOM] DataPoint alloc failed"); return; }  // FW-11
  newPoint->cm = cm;
  
  // Applica EMA smoothing
  if (!emaStoreInit) {
    emaStore = diameter;
    emaStoreInit = true;
  } else {
    emaStore = 0.1f * diameter + 0.9f * emaStore;
  }
  newPoint->diameter = round(emaStore * 1000.0) / 1000.0;
  newPoint->rawDisplay = rawDisplay;
  newPoint->next = nullptr;

  if (firstDataPoint == nullptr) {
    firstDataPoint = newPoint;
    lastDataPoint = newPoint;
  } else {
    DataPoint* current = firstDataPoint;
    DataPoint* prev = nullptr;
    while (current != nullptr) {
      if (current->cm == cm) {
        current->diameter = newPoint->diameter;  // FW-04: use EMA-smoothed value, not raw
        current->rawDisplay = rawDisplay;
        delete newPoint;
        return;
      }
      if (current->cm > cm) {
        newPoint->next = current;
        if (prev == nullptr) firstDataPoint = newPoint;
        else prev->next = newPoint;
        totalDataPoints++;
        return;
      }
      prev = current;
      current = current->next;
    }
    lastDataPoint->next = newPoint;
    lastDataPoint = newPoint;
  }
  totalDataPoints++;
}

void clearAllData() {
  DataPoint* current = firstDataPoint;
  while (current != nullptr) {
    DataPoint* next = current->next;
    delete current;
    current = next;
  }
  firstDataPoint = nullptr;
  lastDataPoint = nullptr;
  totalDataPoints = 0;
  filterInitialized = false;
  smoothedDiameter = 0.0;
  emaStoreInit = false;
  emaStore = 0.0f;
}

int getTotalDataPoints() {
  return totalDataPoints;
}

void IRAM_ATTR updateEncoder() {
  int MSB = digitalRead(ENCODER_DATA_PIN);
  int LSB = digitalRead(ENCODER_CLOCK_PIN);
  int encoded = (MSB << 1) | LSB;
  int sum = (lastEncoded << 2) | encoded;
  if (sum == 0b1000) encoderValue++;
  if (sum == 0b0010) encoderValue--;
  lastEncoded = encoded;
}

// Caliper ISR — mirrors the original blocking logic but fully non-blocking.
// Triggers on every CHANGE of CALIPER_CLOCK_PIN:
//   Rising edge  → record timestamp.
//   Falling edge → measure HIGH duration; if >500 µs it was the inter-packet gap,
//                  commit the completed 24-bit word, then read the new data bit.
void IRAM_ATTR onCaliperChange() {
  unsigned long now = micros();
  if (digitalRead(CALIPER_CLOCK_PIN)) {
    // Rising edge
    calRiseUs = now;
  } else {
    // Falling edge
    unsigned long highDur = now - calRiseUs;
    if (highDur > 500) {
      // Inter-packet gap detected — commit if we collected exactly 24 bits
      if (calBitCount == 24) {
        calRawValue  = calBitAccum;
        calSign      = calSignAccum;
        calDataReady = true;
      }
      calBitAccum  = 0;
      calSignAccum = 1;
      calBitCount  = 0;
    }
    // Read data bit at falling edge (same timing as original blocking code)
    if (calBitCount < 24) {
      if (digitalRead(CALIPER_DATA_PIN)) {
        if      (calBitCount < 20)  calBitAccum |= (1L << calBitCount);
        else if (calBitCount == 20) calSignAccum = -1;
      }
      calBitCount++;
    }
  }
}

void motorQueueTx(const String& lineNoNewline) {
    // Non sovrascrivere un comando utente con un semplice poll
    if (motorTxHasPending && lineNoNewline == "STATUS?") return;
    motorTxPending = lineNoNewline + "\n";
    motorTxHasPending = true;
}

void motorPumpTx() {
  if (!motorTxHasPending) return;
  int freeBytes = SerialMotor.availableForWrite();
  if (freeBytes >= (int)motorTxPending.length()) {
    SerialMotor.print(motorTxPending);
    motorTxHasPending = false;
  }
}

void motorHandleStatusLine(const char* line) {
  if (strncmp(line, "STATUS:", 7) != 0) return;
  
  const char* p = line + 7;
  const char* colon = strchr(p, ':');
  if (!colon) return;
  
  String newMode = String(p).substring(0, colon - p);
  motorMode = newMode;

  // FW-12: parse direction field only (stop before the next colon that carries remaining steps)
  const char* dirStart = colon + 1;
  const char* colon2 = strchr(dirStart, ':');
  if (colon2) {
    motorDir = String(dirStart).substring(0, colon2 - dirStart);
  } else {
    motorDir = String(dirStart);
  }

  // Gestione GOTOPOS
  if (motorMode == "GOTOPOS") {
    isGoToActive = true;
    if (colon2) {
      int remaining = atoi(colon2 + 1);
      if (remaining == 0) {
        // FW-03: slave sent STATUS:GOTOPOS:DIR:0 before calling stopMotion() → confirmed arrival
        isGoToActive = false;
        scanEnabled = oldScanState;
        oldScanState = false;
        Serial.println("GOTOPOS completato!");
        // FW-13: broadcast restored scan state so clients stay in sync
        String scanJson = "{\"type\":\"scan_enabled\",\"value\":" + String(scanEnabled ? "true" : "false") + "}";
        webSocket.broadcastTXT(scanJson);
        String json = "{\"type\":\"goto_status\",\"active\":false,\"completed\":true}";
        webSocket.broadcastTXT(json);
      } else {
        // Invia stato di avanzamento al client
        int currentCm = (encoderValue / PULSES_PER_CM) - 2;
        int remainingCm = remaining / PULSES_PER_CM;
        String json = "{\"type\":\"goto_progress\",\"remaining_cm\":" + String(remainingCm) + 
                     ",\"current_cm\":" + String(currentCm) + ",\"target_cm\":" + String(goToTargetCm) + "}";
        webSocket.broadcastTXT(json);
      }
    }
  } else if (motorMode == "STOP") {
    if (isGoToActive) {
      isGoToActive = false;
      scanEnabled = oldScanState;
      oldScanState = false;
      // FW-13: broadcast restored scan state
      String scanJson = "{\"type\":\"scan_enabled\",\"value\":" + String(scanEnabled ? "true" : "false") + "}";
      webSocket.broadcastTXT(scanJson);
      // Use goToEncoderReached so encoder-based stops report completed:true
      String json = "{\"type\":\"goto_status\",\"active\":false,\"completed\":" +
                    String(goToEncoderReached ? "true" : "false") + ",\"reason\":\"stopped\"}";
      webSocket.broadcastTXT(json);
      goToEncoderReached = false;
    }
  }
  
  motorLastSeenMs = millis();
  String json = "{\"type\":\"motor\",\"mode\":\"" + motorMode + "\",\"dir\":\"" + motorDir + "\"}";
  webSocket.broadcastTXT(json);
}

void motorPumpRx() {
  while (SerialMotor.available() > 0) {
    char c = (char)SerialMotor.read();
    if (c == '\r') continue;
    if (c == '\n') {
      motorRxLine[motorRxLen] = '\0';
      if (motorRxLen > 0) motorHandleStatusLine(motorRxLine);
      motorRxLen = 0;
      continue;
    }
    if (motorRxLen < sizeof(motorRxLine) - 1) motorRxLine[motorRxLen++] = c;
    else motorRxLen = 0;
  }
}

void motorPollIfNeeded() {
  unsigned long now = millis();
  bool stale = (motorLastSeenMs == 0) || (now - motorLastSeenMs > MOTOR_STALE_MS);
  if (stale && (now - lastMotorPollMs > MOTOR_POLL_INTERVAL_MS)) {
    motorQueueTx("STATUS?");
    lastMotorPollMs = now;
    String json = "{\"type\":\"motor\",\"mode\":\"OFFLINE\",\"dir\":\"--\"}";
    webSocket.broadcastTXT(json);
  }
}

void checkGoToStatus() {
  if (!isGoToActive) return;
  
  unsigned long now = millis();
  if (now - lastGoToStatusCheck > 500) {  // Ogni 500ms
    lastGoToStatusCheck = now;
    motorQueueTx("STATUS?");
  }
}

// ----------------------------- GOTOPOS Function -----------------------------
void goToPosition(float targetCm) {
  if (isGoToActive) return;  // FW-08: prevent re-entrant call from WebSocket corrupting oldScanState
  if (targetCm < 0) {
    Serial.println("ERRORE: Target non valido");
    return;
  }

  float currentCm = (encoderValue / (float)PULSES_PER_CM) - 2;
  
  // Se già a target
  if (abs(targetCm - currentCm) < 0.1) {
    Serial.println("Già alla posizione target");
    return;
  }

  // Salva stato scansione e sospendi
  oldScanState = scanEnabled;
  scanEnabled = false;
  // Notify all clients that scan is suspended (symmetric with restore broadcast on completion)
  webSocket.broadcastTXT("{\"type\":\"scan_enabled\",\"value\":false}");
  
  // Determina la direzione
  bool direction = (targetCm > currentCm);
  
  // Sync slave step counter to encoder before sending GOTOPOS.
  // This ensures slave absolute position matches encoder regardless of
  // any prior drift, reset, or slippage.
  noInterrupts();
  long encSnap0 = encoderValue;
  interrupts();
  motorQueueTx("SYNCPOS:" + String(encSnap0));

  // FW-07: slave step counter starts at 0 at boot; master's cm = (encoder/30)-2,
  // so to reach master-coordinate X the slave must move to (X+2)*30 steps → send X+2.
  String cmd = "GOTOPOS:" + String(targetCm + 2.0f, 2) + ":" + String(MOTOR_FAST_HZ) + ":" + (direction ? "F" : "B");
  motorQueueTx(cmd);
  
  goToTargetCm = targetCm;
  isGoToActive = true;
  goToEncoderReached = false;
  lastGoToStatusCheck = 0;
  
  Serial.print("GOTOPOS avviato verso ");
  Serial.print(targetCm, 2);
  Serial.print(" cm (posizione attuale: ");
  Serial.print(currentCm, 2);
  Serial.println(" cm)");
  
  // Notifica il client web
  String json = "{\"type\":\"goto_status\",\"active\":true,\"target\":" + String(targetCm, 2) + 
                ",\"current\":" + String(currentCm, 2) + "}";
  webSocket.broadcastTXT(json);
}

// -----------------------------
void setup() {
  Serial.begin(115200);
  EEPROM.begin(EEPROM_SIZE);

  SerialMotor.begin(115200, SERIAL_8N1, MOTOR_UART_RX_PIN, MOTOR_UART_TX_PIN);
  motorQueueTx("STATUS?");

  pinMode(ENCODER_DATA_PIN, INPUT_PULLUP);
  pinMode(ENCODER_CLOCK_PIN, INPUT_PULLUP);
  pinMode(CALIPER_CLOCK_PIN, INPUT);
  pinMode(CALIPER_DATA_PIN, INPUT);

  attachInterrupt(digitalPinToInterrupt(ENCODER_DATA_PIN), updateEncoder, CHANGE);
  attachInterrupt(digitalPinToInterrupt(ENCODER_CLOCK_PIN), updateEncoder, CHANGE);
  attachInterrupt(digitalPinToInterrupt(CALIPER_CLOCK_PIN), onCaliperChange, CHANGE);

  EEPROM.get(0, caliperZeroOffset);
  if (isnan(caliperZeroOffset)) caliperZeroOffset = 0.0;

  lastSpeedTime = millis();
  lastSpeedEncoder = encoderValue;

  Serial.println("\n=== WiFi Manager v2.3.0 ===");
  pinMode(WIFI_RESET_PIN, INPUT_PULLUP);
  if (digitalRead(WIFI_RESET_PIN) == LOW) {
    Serial.println("Reset WiFi!");
    wifiManager.resetSettings();
    delay(1000);
  }
  wifiManager.setConfigPortalTimeout(180);
  wifiManager.setAPCallback([](WiFiManager* m) {
    Serial.println("AP: " + String(m->getConfigPortalSSID()));
    Serial.println("Pwd: 12345678 | IP: 192.168.4.1");
  });
  if (!wifiManager.autoConnect("DiametroLinea_Setup", "12345678")) {
    Serial.println("Timeout!");
    delay(3000);
    ESP.restart();
  }
  Serial.println("\nWiFi OK! IP: " + WiFi.localIP().toString());

  server.on("/", []() {
    String html = R"rawliteral(
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <title>Sistema Monitoraggio Diametro Linea</title>
    <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
    <script src="https://cdn.jsdelivr.net/npm/chartjs-plugin-zoom@2.0.1/dist/chartjs-plugin-zoom.min.js"></script>
    <style>
        * { box-sizing: border-box; }
        body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; }
        .container { max-width: 1200px; margin: 0 auto; background: white; padding: 20px; border-radius: 10px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .header { text-align: center; margin-bottom: 18px; padding-bottom: 14px; border-bottom: 2px solid #eee; }
        .header h1 { margin: 0 0 4px; font-size: 1.5em; color: #222; }
        .status-panel { display: flex; flex-wrap: wrap; gap: 8px; justify-content: space-between; margin-bottom: 14px; padding: 12px 16px; background: #f8f9fa; border-radius: 8px; }
        .status-item { text-align: center; min-width: 110px; }
        .status-value { font-size: 1.2em; font-weight: bold; color: #007bff; }
        .status-item .speed-optimal { color: #28a745; font-weight: bold; }
        .status-item .speed-too-slow { color: #ffc107; font-weight: bold; }
        .status-item .speed-too-fast { color: #dc3545; font-weight: bold; }
        .display-info { background: #e8f4f8; padding: 8px 14px; border-radius: 5px; margin: 10px 0; font-size: 0.9em; display: flex; flex-wrap: wrap; gap: 14px; align-items: center; }
        .scan-timer { font-family: monospace; font-weight: bold; }
        .motor-bar { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 10px 14px; background: #f8f9fa; border-radius: 8px; margin-bottom: 10px; border: 1px solid #dee2e6; }
        .motor-bar-title { font-weight: bold; font-size: 0.9em; color: #333; margin-right: 4px; }
        .chart-wrapper { position: relative; margin-bottom: 10px; }
        .chart-container { position: relative; height: 500px; border: 1px solid #ddd; border-radius: 5px; background: white; }
        .chart-controls { display: flex; justify-content: center; align-items: center; margin-top: 8px; padding: 8px; background: #f8f9fa; border-radius: 5px; }
        .zoom-controls { display: flex; gap: 10px; }
        .accordion { border: 1px solid #dee2e6; border-radius: 6px; overflow: hidden; margin-bottom: 8px; }
        .accordion-header { display: flex; justify-content: space-between; align-items: center; padding: 10px 16px; background: #f0f4f8; cursor: pointer; user-select: none; font-weight: bold; font-size: 0.95em; color: #333; border: none; width: 100%; text-align: left; transition: background 0.15s; }
        .accordion-header:hover { background: #e2eaf2; }
        .accordion-arrow { font-size: 0.85em; color: #666; transition: transform 0.2s; display: inline-block; }
        .accordion-header.acc-open .accordion-arrow { transform: rotate(180deg); }
        .accordion-body { padding: 14px 16px; background: white; display: none; }
        .accordion-body.acc-open { display: block; }
        button { padding: 8px 16px; margin: 5px; background: #007bff; color: white; border: none; border-radius: 5px; cursor: pointer; font-size: 14px; }
        button:hover { background: #0056b3; }
        button.danger { background: #dc3545; } button.danger:hover { background: #c82333; }
        button.success { background: #28a745; } button.success:hover { background: #218838; }
        button.warning { background: #ffc107; color: #212529; } button.warning:hover { background: #e0a800; }
        .auto-control { display: inline-block; margin: 0 10px; }
        .auto-control input { padding: 8px; margin: 0 5px; border: 1px solid #ddd; border-radius: 4px; width: 80px; }
        .param-display { display: inline-block; margin: 0 15px; padding: 5px 10px; background: #e9ecef; border-radius: 4px; font-family: monospace; }
        .upload-section { margin: 10px 0; padding: 15px; background: #f8f9fa; border-radius: 8px; border: 2px dashed #dee2e6; }
        .upload-area { text-align: center; padding: 10px; }
        .file-input { margin: 10px 0; }
        .upload-info { margin-top: 10px; font-size: 0.9em; color: #6c757d; }
        .dataset-controls { display: flex; gap: 10px; justify-content: center; margin-top: 10px; }
        .dataset-list { margin-top: 10px; max-height: 200px; overflow-y: auto; border: 1px solid #ddd; border-radius: 5px; padding: 10px; background: white; }
        .dataset-item { display: flex; justify-content: space-between; align-items: center; padding: 5px; margin: 2px 0; border-radius: 3px; background: #f8f9fa; }
        .dataset-item:hover { background: #e9ecef; }
        .dataset-color { width: 20px; height: 20px; border-radius: 3px; margin-right: 10px; }
        .dataset-name { flex-grow: 1; font-size: 0.9em; }
        .color-palette { display: flex; gap: 5px; margin: 10px 0; flex-wrap: wrap; }
        .color-option { width: 25px; height: 25px; border-radius: 3px; cursor: pointer; border: 2px solid transparent; }
        .color-option.selected { border-color: #000; }
        .info-badge { display: inline-block; padding: 2px 8px; background: #007bff; color: white; border-radius: 12px; font-size: 0.85em; margin-left: 10px; }
        .calib-grid { display: flex; flex-wrap: wrap; gap: 20px; }
        .calib-col { flex: 1; min-width: 200px; }
        .calib-col h4 { margin: 0 0 10px; color: #555; font-size: 0.9em; text-transform: uppercase; letter-spacing: 0.04em; }
        .footer-info { margin-top: 20px; text-align: center; color: #666; }
        .modal {
            display: none;
            position: fixed;
            z-index: 1000;
            left: 0;
            top: 0;
            width: 100%;
            height: 100%;
            background-color: rgba(0,0,0,0.5);
        }
        .modal-content {
            background-color: white;
            margin: 15% auto;
            padding: 20px;
            border-radius: 8px;
            width: 300px;
            text-align: center;
        }
        .modal-content input {
            padding: 8px;
            margin: 10px 0;
            width: 90%;
            border: 1px solid #ddd;
            border-radius: 4px;
        }
        .modal-buttons {
            display: flex;
            gap: 10px;
            justify-content: center;
        }
        .goto-progress {
            background: #e3f2fd;
            padding: 5px 10px;
            border-radius: 4px;
            font-size: 0.85em;
        }
    </style>
</head>
<body>
<div class="container">

    <div class="header">
        <h1>Sistema Monitoraggio Diametro Linea <span class="info-badge">CALIBRO FISSO</span></h1>
    </div>

    <div class="status-panel">
        <div class="status-item">
            <div>Lunghezza Attuale</div>
            <div class="status-value" id="currentLength">0 cm</div>
        </div>
        <div class="status-item">
            <div>Diametro Compensato</div>
            <div class="status-value" id="currentDiameter">0.00 mm</div>
        </div>
        <div class="status-item">
            <div>Display Calibro</div>
            <div class="status-value" id="currentDisplay">0.00 mm</div>
        </div>
        <div class="status-item">
            <div>Velocit&agrave;</div>
            <div class="status-value" id="currentSpeed">0.00 cm/s</div>
        </div>
        <div class="status-item">
            <div>Stato Velocit&agrave;</div>
            <div class="status-value" id="speedStatus">Ottimale</div>
        </div>
        <div class="status-item">
            <div>Punti Registrati</div>
            <div class="status-value" id="dataPointsCount">0</div>
        </div>
        <div class="status-item">
            <div>Stato Connessione</div>
            <div class="status-value" id="connectionStatus">Connesso</div>
        </div>
    </div>

    <div class="display-info">
        <strong>Configurazione:</strong> Calibro fisso (punto di misura 20mm dietro encoder) |
        <strong>Zero Display:</strong> <span id="displayZeroValue">0.00</span> mm |
        <strong>Offset:</strong> <span id="currentOffset">0.00</span> mm |
        <strong>Motore:</strong> <span id="motorState">--</span> |
        <strong>Timer:</strong> <span class="scan-timer" id="scanTimer">00:00</span>
        <span id="gotoProgress" class="goto-progress" style="display:none;"></span>
    </div>

    <div class="motor-bar">
        <span class="motor-bar-title">&#x1F527; Motore</span>
        <button class="success" onclick="startScan()">START SCAN</button>
        <button class="danger" onclick="stopScan()">STOP</button>
        <button id="btnScanEnable" class="danger" onclick="toggleScanEnable()">&#9208; Ricezione OFF</button>
        <button class="warning" onclick="sendCommand('motor fast_s')">FAST stessa dir</button>
        <button class="warning" onclick="sendCommand('motor fast_o')">FAST opposta dir</button>
        <button onclick="sendCommand('motor status')">Aggiorna Stato</button>
        <button onclick="showGoToDialog()" style="background: #6c757d;">🎯 Vai a posizione</button>
    </div>

    <div class="chart-wrapper">
        <div class="chart-container">
            <canvas id="lineChart"></canvas>
        </div>
        <div class="chart-controls">
            <div class="zoom-controls">
                <button onclick="zoomIn()">Zoom +</button>
                <button onclick="zoomOut()">Zoom -</button>
                <button onclick="resetZoom()">Reset Zoom</button>
                <button onclick="userHasZoomed=false; fitToData()">Adatta ai Dati</button>
            </div>
        </div>
    </div>

    <div class="accordion">
        <button class="accordion-header acc-open" onclick="accToggle(this)">
            &#x1F4CA; Grafico &amp; Dati <span class="accordion-arrow">&#x25BC;</span>
        </button>
        <div class="accordion-body acc-open">
            <label>Smooth Alpha:
            <input type="range" id="smoothAlpha" min="0.01" max="0.5" step="0.01" value="0.1"
                oninput="document.getElementById('alphaVal').textContent=this.value">
            <span id="alphaVal">0.1</span>
            </label>
            <button onclick="resetChart()">Reset Grafico</button>
            <button onclick="exportAllData()">Esporta Tutti i Dati CSV</button>
            <button onclick="toggleAutoScale()">Auto-Fit: <span id="autoScaleStatus">ON</span></button>
            <button class="danger" onclick="sendCommand('reset')">Reset Lunghezza e Dati</button>
        </div>
    </div>

    <div class="accordion">
        <button class="accordion-header" onclick="accToggle(this)">
            &#x1F4C2; Carica &amp; Confronta CSV <span class="accordion-arrow">&#x25BC;</span>
        </button>
        <div class="accordion-body">
            <div class="upload-section">
                <div class="upload-area">
                    <input type="file" id="csvFile" accept=".csv" class="file-input"><br>
                    <input type="text" id="datasetName" placeholder="Nome dataset" style="padding:8px;margin:5px;border:1px solid #ddd;border-radius:4px;width:200px;">
                    <div class="color-palette" id="colorPalette"></div>
                    <button onclick="uploadCSV()" class="success">Carica CSV</button>
                    <button onclick="clearAllDatasets()" class="danger">Rimuovi Tutti</button>
                    <div class="dataset-list" id="datasetList"></div>
                    <div class="upload-info">
                        Formati supportati:<br>
                        - Con header: "Dataset,Lunghezza (cm),Diametro (mm)"<br>
                        - Con header: "Lunghezza (cm),Diametro (mm)"<br>
                        - Senza header: "0.0,0.000"<br>
                        Prima riga: "0,0.000" (punto zero)
                    </div>
                </div>
            </div>
        </div>
    </div>

    <div class="accordion">
        <button class="accordion-header" onclick="accToggle(this)">
            &#x2699;&#xFE0F; Calibrazione &amp; Sistema <span class="accordion-arrow">&#x25BC;</span>
        </button>
        <div class="accordion-body">
            <div class="calib-grid">
                <div class="calib-col">
                    <h4>Offset Calibro</h4>
                    <div class="auto-control">
                        <input type="number" id="offsetValue" placeholder="offset mm" step="0.01">
                        <button onclick="setOffset()">Imposta Offset</button>
                    </div>
                    <button onclick="sendCommand('resetoffset')">Resetta Offset a 0</button>
                    <div style="margin-top:12px;">
                        <span>Zero Display: </span>
                        <span class="param-display" id="currentDisplayZero">0.00</span>
                        <span> mm &nbsp;|&nbsp; Offset: </span>
                        <span class="param-display" id="currentOffsetValue">0.00</span>
                        <span> mm</span>
                    </div>
                </div>
                <div class="calib-col">
                    <h4>Calibro</h4>
                    <button onclick="sendCommand('readraw')">Lettura Display Calibro</button>
                    <button onclick="sendCommand('setdisplayzero')">Imposta Zero da Display</button>
                </div>
            </div>
        </div>
    </div>

    <div class="footer-info">
        <p>Ultimo aggiornamento: <span id="lastUpdate">--</span></p>
        <p id="commandStatus"></p>
        <hr style="border:none;border-top:1px solid #ddd;margin:15px 0;">
        <p style="font-size:0.85em;color:#999;">
            <strong>Sistema Monitoraggio Diametro Linea</strong><br>
            Versione <strong>)rawliteral"
                  + String(FIRMWARE_VERSION) + R"rawliteral(</strong> |
            Build <strong>)rawliteral"
                  + String(FIRMWARE_DATE) + R"rawliteral(</strong> |
            <strong>)rawliteral"
                  + String(FIRMWARE_FEATURES) + R"rawliteral(</strong>
        </p>
    </div>
</div>

<div id="gotoModal" class="modal">
    <div class="modal-content">
        <h3>Vai a posizione (cm)</h3>
        <input type="number" id="targetCm" placeholder="Inserisci lunghezza in cm" step="0.1">
        <div class="modal-buttons">
            <button onclick="executeGoTo()" class="success">Avvia</button>
            <button onclick="closeModal()">Annulla</button>
        </div>
    </div>
</div>

<script>
    let chart;
    let dataPoints = [];
    let autoScaleEnabled = true;
    let userHasZoomed = false;
    let ws;
    let zeroPoint = {x: 0, y: 0};
    let currentDisplayZero = 0.0;
    let currentOffset = 0.0;
    let uploadedDatasets = [];
    let nextDatasetId = 1;
    let isGoToActive = false;
    const colorPalette = [
        '#FF6384','#36A2EB','#FFCE56','#4BC0C0','#9966FF',
        '#FF9F40','#FF6384','#C9CBCF','#7CFFB2','#F465D4',
        '#8C564B','#E377C2','#7F7F7F','#BCBD22','#17BECF'
    ];
    let selectedColor = colorPalette[0];

    let scanRunning = false;
    let scanStartMs = 0;
    let scanInterval = null;
    let scanReceiving = false;

    function smoothEMA(data, alpha) {
        let result = [{x: data[0].x, y: data[0].y}];
        for (let i = 1; i < data.length; i++) {
            result.push({
                x: data[i].x,
                y: alpha * data[i].y + (1 - alpha) * result[i-1].y
            });
        }
        return result;
    }

    function formatMMSS(totalSeconds) {
        const m = Math.floor(totalSeconds / 60);
        const s = totalSeconds % 60;
        return String(m).padStart(2,'0') + ':' + String(s).padStart(2,'0');
    }

    function startTimerIfNeeded() {
        if (scanRunning) return;
        scanRunning = true;
        scanStartMs = Date.now();
        document.getElementById('scanTimer').textContent = "00:00";
        if (scanInterval) clearInterval(scanInterval);
        scanInterval = setInterval(() => {
            if (!scanRunning) return;
            const elapsed = Math.floor((Date.now() - scanStartMs) / 1000);
            document.getElementById('scanTimer').textContent = formatMMSS(elapsed);
        }, 500);
    }

    function stopTimer() {
        scanRunning = false;
        if (scanInterval) { clearInterval(scanInterval); scanInterval = null; }
    }

    function startScan() {
        if (isGoToActive) {
            showCommandStatus('Attendi completamento GOTOPOS', true);
            return;
        }
        stopTimer();
        document.getElementById('scanTimer').textContent = "00:00";
        startTimerIfNeeded();
        sendCommand('motor scan');
    }

    function stopScan() {
        sendCommand('motor stop');
        stopTimer();
    }

    function toggleScanEnable() {
        if (isGoToActive) {
            showCommandStatus('Impossibile modificare durante GOTOPOS', true);
            return;
        }
        scanReceiving = !scanReceiving;
        const btn = document.getElementById('btnScanEnable');
        if (scanReceiving) {
            sendCommand('scan_on');
            btn.textContent = '\u23FA Ricezione ON';
            btn.className = 'success';
        } else {
            sendCommand('scan_off');
            btn.textContent = '\u23F8 Ricezione OFF';
            btn.className = 'danger';
        }
    }

    const OPTIMAL_SPEED_MIN = 0.5;
    const OPTIMAL_SPEED_MAX = 2.5;

    function initializeChart() {
        const ctx = document.getElementById('lineChart').getContext('2d');
        initializeColorPalette();
        chart = new Chart(ctx, {
            type: 'line',
            data: {
                datasets: [{
                    label: 'Diametro Compensato (mm)',
                    data: dataPoints,
                    borderColor: '#007bff',
                    backgroundColor: 'rgba(0, 123, 255, 0.1)',
                    borderWidth: 3,
                    fill: true,
                    pointRadius: 1,
                    pointHoverRadius: 4,
                    pointBackgroundColor: '#007bff',
                    pointBorderColor: '#007bff',
                    pointBorderWidth: 0
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: { duration: 0 },
                scales: {
                    x: {
                        type: 'linear',
                        title: { display: true, text: 'Lunghezza (cm)', font: { size: 14, weight: 'bold' } },
                        grid: { color: 'rgba(0,0,0,0.1)' }
                    },
                    y: {
                        title: { display: true, text: 'Diametro Compensato (mm)', font: { size: 14, weight: 'bold' } },
                        grid: { color: 'rgba(0,0,0,0.1)' }
                    }
                },
                plugins: {
                    legend: { display: true, position: 'top' },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                        callbacks: {
                            label: function(context) {
                                return `${context.dataset.label}: ${context.parsed.y.toFixed(3)} mm a ${context.parsed.x} cm`;
                            }
                        }
                    },
                    zoom: {
                        pan: { enabled: true, mode: 'xy', modifierKey: 'ctrl' },
                        zoom: { wheel: { enabled: true }, pinch: { enabled: true }, mode: 'xy' },
                        limits: { x: { min: 0, max: 100000 }, y: { min: -10, max: 10 } }
                    }
                },
                interaction: { intersect: false, mode: 'nearest' }
            }
        });
        ensureZeroPoint();
        fitToData();
    }

    function initializeColorPalette() {
        const paletteContainer = document.getElementById('colorPalette');
        paletteContainer.innerHTML = '';
        colorPalette.forEach(color => {
            const colorOption = document.createElement('div');
            colorOption.className = 'color-option';
            colorOption.style.backgroundColor = color;
            if (color === selectedColor) colorOption.classList.add('selected');
            colorOption.onclick = function() {
                document.querySelectorAll('.color-option').forEach(opt => opt.classList.remove('selected'));
                this.classList.add('selected');
                selectedColor = color;
            };
            paletteContainer.appendChild(colorOption);
        });
    }

    function updateSpeedDisplay(speed) {
        const speedElement = document.getElementById('currentSpeed');
        const statusElement = document.getElementById('speedStatus');
        speedElement.textContent = speed.toFixed(2) + ' cm/s';
        if (speed < OPTIMAL_SPEED_MIN) {
            statusElement.textContent = 'Troppo Lenta';
            statusElement.className = 'status-value speed-too-slow';
            speedElement.className = 'status-value speed-too-slow';
        } else if (speed > OPTIMAL_SPEED_MAX) {
            statusElement.textContent = 'Troppo Alta';
            statusElement.className = 'status-value speed-too-fast';
            speedElement.className = 'status-value speed-too-fast';
        } else {
            statusElement.textContent = 'Ottimale';
            statusElement.className = 'status-value speed-optimal';
            speedElement.className = 'status-value speed-optimal';
        }
    }

    function uploadCSV() {
        const fileInput = document.getElementById('csvFile');
        const datasetNameInput = document.getElementById('datasetName');
        const file = fileInput.files[0];
        const datasetName = datasetNameInput.value || `Dataset ${nextDatasetId}`;
        if (!file) { showCommandStatus('Seleziona un file CSV', true); return; }
        const reader = new FileReader();
        reader.onload = function(e) {
            try {
                const csv = e.target.result;
                const lines = csv.split('\n');
                const newDataPoints = [];
                let hasHeader = false;
                let xIndex = 1;
                let yIndex = 2;
                if (lines.length > 0) {
                    const firstLine = lines[0].toLowerCase();
                    if (firstLine.includes('dataset') || firstLine.includes('lunghezza') ||
                        firstLine.includes('diametro') || firstLine.includes(',')) {
                        hasHeader = true;
                        const headers = firstLine.split(',').map(h => h.trim().toLowerCase().replace(/\"/g, ''));
                        xIndex = 0; yIndex = 1;
                        for (let i = 0; i < headers.length; i++) {
                            if (headers[i].includes('lunghezza') || headers[i].includes('cm')) xIndex = i;
                            if (headers[i].includes('diametro') || headers[i].includes('mm')) yIndex = i;
                        }
                        if (headers.includes('dataset') && headers.length >= 3) { xIndex = 1; yIndex = 2; }
                    }
                }
                const startLine = hasHeader ? 1 : 0;
                let invalidPoints = 0;
                for (let i = startLine; i < lines.length; i++) {
                    const line = lines[i].trim();
                    if (line) {
                        const parts = line.split(',').map(part => part.trim().replace(/\"/g, ''));
                        if (parts.length >= Math.max(xIndex, yIndex) + 1) {
                            const x = parseFloat(parts[xIndex]);
                            const y = parseFloat(parts[yIndex]);
                            if (!isNaN(x) && !isNaN(y)) newDataPoints.push({x: x, y: y});
                            else invalidPoints++;
                        } else invalidPoints++;
                    }
                }
                if (newDataPoints.length === 0) {
                    showCommandStatus('Nessun dato valido trovato nel file.', true);
                    return;
                }
                newDataPoints.sort((a, b) => a.x - b.x);
                const newDataset = { id: nextDatasetId++, name: datasetName, color: selectedColor, data: newDataPoints, visible: true };
                uploadedDatasets.push(newDataset);
                addDatasetToChart(newDataset);
                updateDatasetList();
                if (autoScaleEnabled) fitToData(); else chart.update();
                let statusMsg = `Caricati ${newDataPoints.length} punti dati come "${datasetName}"`;
                if (invalidPoints > 0) statusMsg += ` (${invalidPoints} punti ignorati)`;
                showCommandStatus(statusMsg);
                fileInput.value = '';
                datasetNameInput.value = '';
            } catch (error) {
                showCommandStatus('Errore nel processare il file CSV: ' + error.message, true);
            }
        };
        reader.onerror = function() { showCommandStatus('Errore nella lettura del file', true); };
        reader.readAsText(file);
    }

    function addDatasetToChart(dataset) {
        const smoothed = smoothEMA(dataset.data, 0.1);
        chart.data.datasets.push({
            label: dataset.name,
            data: smoothed,
            borderColor: dataset.color,
            backgroundColor: dataset.color + '1A',
            borderWidth: 3,
            borderDash: [],
            fill: false,
            pointRadius: 1,
            pointHoverRadius: 4,
            pointBackgroundColor: dataset.color,
            pointBorderColor: dataset.color,
            pointBorderWidth: 0,
            hidden: !dataset.visible
        });
        chart.update();
    }

    function updateDatasetList() {
        const datasetList = document.getElementById('datasetList');
        datasetList.innerHTML = '';
        uploadedDatasets.forEach(dataset => {
            const datasetItem = document.createElement('div');
            datasetItem.className = 'dataset-item';
            let maxLengthMeters = 0;
            if (dataset.data.length > 0) {
                const maxLengthCm = Math.max(...dataset.data.map(point => point.x));
                maxLengthMeters = (maxLengthCm / 100).toFixed(1);
            }
            datasetItem.innerHTML = `
                <div style="display:flex;align-items:center;flex-grow:1;">
                    <div class="dataset-color" style="background-color:${dataset.color}"></div>
                    <div class="dataset-name">${dataset.name} (${dataset.data.length} punti, ${maxLengthMeters} m)</div>
                </div>
                <div class="dataset-controls">
                    <button onclick="toggleDataset(${dataset.id})" style="padding:2px 8px;font-size:0.8em;">${dataset.visible ? 'Nascondi' : 'Mostra'}</button>
                    <button onclick="removeDataset(${dataset.id})" style="padding:2px 8px;font-size:0.8em;background:#dc3545;">Rimuovi</button>
                </div>`;
            datasetList.appendChild(datasetItem);
        });
    }

    function toggleDataset(datasetId) {
        const dataset = uploadedDatasets.find(ds => ds.id === datasetId);
        if (dataset) {
            dataset.visible = !dataset.visible;
            const chartDataset = chart.data.datasets.find(ds => ds.label === dataset.name);
            if (chartDataset) chartDataset.hidden = !dataset.visible;
            chart.update();
            updateDatasetList();
        }
    }

    function removeDataset(datasetId) {
        const datasetIndex = uploadedDatasets.findIndex(ds => ds.id === datasetId);
        if (datasetIndex !== -1) {
            const dataset = uploadedDatasets[datasetIndex];
            const chartDatasetIndex = chart.data.datasets.findIndex(ds => ds.label === dataset.name);
            if (chartDatasetIndex !== -1) chart.data.datasets.splice(chartDatasetIndex, 1);
            uploadedDatasets.splice(datasetIndex, 1);
            chart.update();
            updateDatasetList();
            showCommandStatus(`Dataset "${dataset.name}" rimosso`);
        }
    }

    function clearAllDatasets() {
        if (uploadedDatasets.length === 0) { showCommandStatus('Nessun dataset da rimuovere', true); return; }
        if (confirm('Sei sicuro di voler rimuovere tutti i dataset caricati?')) {
            chart.data.datasets = [chart.data.datasets[0]];
            uploadedDatasets = [];
            chart.update();
            updateDatasetList();
            showCommandStatus('Tutti i dataset rimossi');
        }
    }

    async function loadHistoryFromExport() {
        try {
            const r = await fetch('/export', { cache: 'no-store' });
            if (!r.ok) throw new Error('HTTP ' + r.status);
            const csv = await r.text();
            const lines = csv.trim().split('\n');
            dataPoints = [];
            ensureZeroPoint();
            if (lines.length === 0) return;
            const first = (lines[0] || '').trim().toLowerCase();
            const hasHeader = first.includes('lunghezza') || first.includes('diametro') || first.includes('dataset');
            const start = hasHeader ? 1 : 0;
            for (let i = start; i < lines.length; i++) {
                const line = lines[i].trim();
                if (!line) continue;
                const parts = line.split(',').map(p => p.trim());
                if (parts.length < 2) continue;
                let xStr = (parts.length >= 3 ? parts[1] : parts[0]);
                let yStr = (parts.length >= 3 ? parts[2] : parts[1]);
                const x = parseFloat(xStr.replace(',', '.'));
                const y = parseFloat(yStr.replace(',', '.'));
                if (!isNaN(x) && !isNaN(y)) dataPoints.push({ x, y });
            }
            dataPoints.sort((a, b) => a.x - b.x);
            chart.data.datasets[0].data = dataPoints;
            document.getElementById('dataPointsCount').textContent = dataPoints.length;
            if (autoScaleEnabled) fitToData(); else chart.update();
            showCommandStatus('Storico caricato: ' + (dataPoints.length - 1) + ' punti', false);
        } catch (e) {
            showCommandStatus('Errore caricamento storico: ' + e.message, true);
        }
    }

    function connectWebSocket() {
        ws = new WebSocket('ws://' + location.hostname + ':81/');
        ws.onopen = function() {
            document.getElementById('connectionStatus').innerHTML = 'Connesso';
            document.getElementById('connectionStatus').style.color = '#28a745';
            sendCommand('getparams');
            sendCommand('motor status');
        };
        ws.onclose = function() {
            document.getElementById('connectionStatus').innerHTML = 'Disconnesso';
            document.getElementById('connectionStatus').style.color = '#dc3545';
            setTimeout(connectWebSocket, 3000);
        };
        ws.onmessage = function(event) {
            try {
                const msg = JSON.parse(event.data);
                if (msg.type === 'params') {
                    updateParamsDisplay(msg.displayZero, msg.offset);
                } else if (msg.type === 'speed') {
                    updateSpeedDisplay(msg.speed);
                } else if (msg.type === 'motor') {
                    document.getElementById('motorState').textContent = msg.mode + ' ' + msg.dir;
                } else if (msg.type === 'goto_status') {
                    isGoToActive = msg.active;
                    const gotoBtn = document.querySelector('button[onclick="executeGoTo()"]');
                    if (gotoBtn) gotoBtn.disabled = msg.active;
                    const scanBtn = document.querySelector('button[onclick="startScan()"]');
                    if (scanBtn) scanBtn.disabled = msg.active;
                    const toggleBtn = document.getElementById('btnScanEnable');
                    if (toggleBtn) toggleBtn.disabled = msg.active;
                    
                    if (!msg.active && msg.completed) {
                        showCommandStatus('Posizione raggiunta!');
                        const progressSpan = document.getElementById('gotoProgress');
                        progressSpan.style.display = 'none';
                        progressSpan.textContent = '';
                    } else if (!msg.active && !msg.completed) {
                        showCommandStatus('Movimento interrotto', true);
                    }
                } else if (msg.type === 'goto_progress') {
                    const progressSpan = document.getElementById('gotoProgress');
                    progressSpan.style.display = 'inline-block';
                    progressSpan.textContent = `📍 GOTOPOS: ${msg.remaining_cm.toFixed(1)} cm rimanenti (${msg.current_cm.toFixed(1)}/${msg.target_cm.toFixed(1)} cm)`;
                } else if (msg.type === 'scan_enabled') {
                    scanReceiving = msg.value;
                    const btn = document.getElementById('btnScanEnable');
                    if (msg.value) {
                        btn.textContent = '\u23FA Ricezione ON';
                        btn.className = 'success';
                    } else {
                        btn.textContent = '\u23F8 Ricezione OFF';
                        btn.className = 'danger';
                    }
                } else if (msg.cm !== undefined) {
                    if (!isGoToActive) {
                        startTimerIfNeeded();
                        updateDisplay(msg);
                    } else {
                        document.getElementById('currentLength').textContent = msg.cm + ' cm';
                    }
                }
            } catch(e) {
                console.error('Errore parsing JSON:', e);
            }
        };
        ws.onerror = function(error) { console.error('WebSocket error:', error); };
    }

    function updateParamsDisplay(displayZero, offset) {
        currentDisplayZero = displayZero;
        currentOffset = offset;
        document.getElementById('currentDisplayZero').textContent = displayZero.toFixed(2);
        document.getElementById('displayZeroValue').textContent = displayZero.toFixed(2);
        document.getElementById('currentOffsetValue').textContent = offset.toFixed(2);
        document.getElementById('currentOffset').textContent = offset.toFixed(2);
    }

    function updateDisplay(msg) {
        document.getElementById('currentLength').textContent = msg.cm + ' cm';
        document.getElementById('currentDiameter').textContent = msg.diameter.toFixed(2) + ' mm';
        document.getElementById('currentDisplay').textContent = msg.rawDisplay.toFixed(2) + ' mm';
        document.getElementById('lastUpdate').textContent = new Date().toLocaleTimeString();
        document.getElementById('dataPointsCount').textContent = msg.totalPoints || dataPoints.length;
        addDataPoint(msg.cm, msg.diameter);
    }

    function ensureZeroPoint() {
        const hasZeroPoint = dataPoints.some(point => point.x === 0 && point.y === 0);
        if (!hasZeroPoint) dataPoints.unshift(zeroPoint);
    }

    function addDataPoint(x, y) {
        const point = {x: x, y: Math.round(y * 1000) / 1000};
        ensureZeroPoint();
        const existingIndex = dataPoints.findIndex(p => p.x === x);
        if (existingIndex !== -1) {
            dataPoints[existingIndex] = point;
        } else {
            dataPoints.push(point);
        }
        dataPoints.sort((a, b) => a.x - b.x);
        chart.data.datasets[0].data = dataPoints;
        document.getElementById('dataPointsCount').textContent = dataPoints.length;
        if (autoScaleEnabled) fitToData();
        else chart.update('none');
    }

    function zoomIn()  { userHasZoomed = true; chart.zoom(1.1); }
    function zoomOut() { userHasZoomed = true; chart.zoom(0.9); }

    function resetZoom() {
        userHasZoomed = false;
        chart.resetZoom();
        if (autoScaleEnabled) fitToData();
    }

    function fitToData() {
        if (userHasZoomed) return;
        if (dataPoints.length === 0 && uploadedDatasets.length === 0) return;
        const allChartData = [...dataPoints];
        uploadedDatasets.forEach(dataset => { if (dataset.visible) allChartData.push(...dataset.data); });
        if (allChartData.length === 0) return;
        const xValues = allChartData.map(p => p.x);
        const yValues = allChartData.map(p => p.y);
        let minX = Math.min(...xValues);
        let maxX = Math.max(...xValues);
        let minY = Math.min(...yValues);
        let maxY = Math.max(...yValues);
        const xRange = maxX - minX;
        const yRange = maxY - minY;
        if (xRange === 0) { minX -= 10; maxX += 10; } else { minX -= xRange * 0.05; maxX += xRange * 0.05; }
        if (yRange === 0) { minY -= 0.1; maxY += 0.1; } else { minY -= yRange * 0.1; maxY += yRange * 0.1; }
        chart.options.scales.x.min = minX;
        chart.options.scales.x.max = maxX;
        chart.options.scales.y.min = minY;
        chart.options.scales.y.max = maxY;
        chart.update();
    }

    function resetChart() {
        dataPoints = [];
        chart.data.datasets[0].data = dataPoints;
        ensureZeroPoint();
        if (autoScaleEnabled) fitToData(); else chart.update();
        document.getElementById('dataPointsCount').textContent = 0;
        showCommandStatus('Grafico resettato');
    }

    function exportAllData() {
        let csv = "Lunghezza cm,Diametro mm (grafico)\n";
        dataPoints.forEach(point => {
            csv += point.x.toFixed(1) + "," + point.y.toFixed(3) + "\n";
        });
        const blob = new Blob([csv], { type: 'text/csv' });
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'diametrolinea_grafico.csv';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
    }

    function toggleAutoScale() {
        autoScaleEnabled = !autoScaleEnabled;
        document.getElementById('autoScaleStatus').textContent = autoScaleEnabled ? 'ON' : 'OFF';
        if (autoScaleEnabled) fitToData(); else chart.update();
        showCommandStatus('Auto-Fit: ' + (autoScaleEnabled ? 'ON' : 'OFF'));
    }

    function sendCommand(command) {
        if (ws && ws.readyState === WebSocket.OPEN) {
            ws.send(command);
            showCommandStatus('Comando inviato: ' + command);
            if (command === 'reset') {
                dataPoints = [];
                chart.data.datasets[0].data = dataPoints;
                ensureZeroPoint();
                if (autoScaleEnabled) fitToData(); else chart.update();
                document.getElementById('dataPointsCount').textContent = 0;
                stopTimer();
                document.getElementById('scanTimer').textContent = "00:00";
            }
        } else {
            showCommandStatus('Errore: WebSocket non connesso', true);
        }
    }

    function setOffset() {
        const offset = document.getElementById('offsetValue').value;
        if (offset && !isNaN(offset)) {
            sendCommand('setoffset ' + offset);
            document.getElementById('offsetValue').value = '';
        } else {
            showCommandStatus('Inserire un valore offset valido', true);
        }
    }

    function showCommandStatus(message, isError = false) {
        const statusElement = document.getElementById('commandStatus');
        statusElement.textContent = message;
        statusElement.style.color = isError ? '#dc3545' : '#28a745';
        setTimeout(() => { statusElement.textContent = ''; }, 3000);
    }

    function accToggle(btn) {
        btn.classList.toggle('acc-open');
        btn.nextElementSibling.classList.toggle('acc-open');
    }

    function showGoToDialog() {
        if (isGoToActive) {
            showCommandStatus('GOTOPOS già in corso', true);
            return;
        }
        document.getElementById('gotoModal').style.display = 'block';
        document.getElementById('targetCm').focus();
    }

    function closeModal() {
        document.getElementById('gotoModal').style.display = 'none';
        document.getElementById('targetCm').value = '';
    }

    function executeGoTo() {
        const target = document.getElementById('targetCm').value;
        if (target && !isNaN(target) && parseFloat(target) >= 0) {
            sendCommand('goto ' + target);
            closeModal();
        } else {
            showCommandStatus('Inserire una lunghezza valida (>= 0)', true);
        }
    }

    window.onclick = function(event) {
        const modal = document.getElementById('gotoModal');
        if (event.target == modal) {
            closeModal();
        }
    }

    document.addEventListener('DOMContentLoaded', async function () {
        initializeChart();
        await loadHistoryFromExport();
        connectWebSocket();
        document.getElementById('lastUpdate').textContent = new Date().toLocaleTimeString();
    });
</script>
</body>
</html>
)rawliteral";
    server.send(200, "text/html", html);
  });

  server.on("/export", HTTP_GET, []() {
    server.setContentLength(CONTENT_LENGTH_UNKNOWN);
    server.sendHeader("Content-Disposition", "attachment; filename=diametrolinea.csv");
    server.sendHeader("Cache-Control", "no-store");
    server.send(200, "text/csv", "");
    server.sendContent("Lunghezza cm,Diametro mm\r\n");
    char line[64];
    int n = 0;
    for (DataPoint* cur = firstDataPoint; cur != nullptr; cur = cur->next) {
      snprintf(line, sizeof(line), "%d,%.2f\r\n", cur->cm, cur->diameter);
      server.sendContent(line);
      if ((++n % 50) == 0) delay(1);
      else delay(0);
    }
  });

  server.begin();

  webSocket.begin();
  webSocket.onEvent([](uint8_t num, WStype_t type, uint8_t* payload, size_t length) {
    if (type == WStype_TEXT) {
      String cmd = String((char*)payload);
      if (cmd == "getparams") {
        String json = "{\"type\":\"params\",\"displayZero\":" + String(displayZeroValue, 2) + ",\"offset\":" + String(caliperZeroOffset, 2) + "}";
        webSocket.sendTXT(num, json);
      } else {
        handleCommand(cmd);
      }
    } else if (type == WStype_CONNECTED) {
      String json = "{\"type\":\"params\",\"displayZero\":" + String(displayZeroValue, 2) + ",\"offset\":" + String(caliperZeroOffset, 2) + "}";
      webSocket.sendTXT(num, json);
    }
  });

  Serial.println("Sistema pronto - CALIBRO FISSO");
  Serial.println("cm,diametro_compensato,display_calibro");
}

// -----------------------------
void calculateSpeed() {
  unsigned long currentTime = millis();
  if (currentTime - lastSpeedTime >= 1000) {
    int encoderDiff = encoderValue - lastSpeedEncoder;
    currentSpeed = (encoderDiff / (float)PULSES_PER_CM);
    lastSpeedEncoder = encoderValue;
    lastSpeedTime = currentTime;
    String json = "{\"type\":\"speed\",\"speed\":" + String(currentSpeed, 2) + "}";
    webSocket.broadcastTXT(json);
  }
}

void sendParamsToClients() {
  String json = "{\"type\":\"params\",\"displayZero\":" + String(displayZeroValue, 2) + ",\"offset\":" + String(caliperZeroOffset, 2) + "}";
  webSocket.broadcastTXT(json);
}

float readCaliperDisplay() {
  readCaliper();
  return lineDiameter;
}

// -----------------------------
void loop() {
  server.handleClient();
  webSocket.loop();

  motorPumpRx();
  motorPollIfNeeded();
  motorPumpTx();
  checkGoToStatus();

  calculateSpeed();

  // FW-09: single atomic snapshot of volatile encoderValue for consistent use this iteration
  noInterrupts();
  long encSnap = encoderValue;
  interrupts();

  // Watchdog encoder: auto-stop motore dopo 5s fermo
  {
    unsigned long now = millis();
    if (encSnap != encoderLastValueForWatchdog) {
      encoderLastValueForWatchdog = encSnap;
      encoderLastMoveMs = now;
      encoderWatchdogStopSent = false;
    } else if (!isGoToActive && !encoderWatchdogStopSent && encoderLastMoveMs > 0 && (now - encoderLastMoveMs >= 5000)) {
      // FW-10: watchdog suppressed during GOTOPOS (slow final approach looks like no movement)
      motorQueueTx("STOP");
      encoderWatchdogStopSent = true;
      Serial.println("Watchdog: encoder fermo da 5s, motore STOP");
    }
  }

  int currentCm = (encSnap / PULSES_PER_CM) - 2;

  // Acquisizione dati solo se scansione abilitata e NON in GOTOPOS
  if (!isGoToActive && currentCm != lastCm && currentCm >= 0 && scanEnabled) {
    lastCm = currentCm;

    float displayValue = readCaliperDisplay();

    float compensatedDiameter = displayValue - displayZeroValue - caliperZeroOffset;
    compensatedDiameter = round(compensatedDiameter * 1000.0) / 1000.0;
    addDataPoint(currentCm, compensatedDiameter, displayValue);

    String json = "{\"cm\":" + String(currentCm) + ",\"diameter\":" + String(compensatedDiameter, 2) + 
                  ",\"rawDisplay\":" + String(displayValue, 2) + ",\"totalPoints\":" + String(getTotalDataPoints()) + "}";
    webSocket.broadcastTXT(json);

    Serial.print(currentCm);
    Serial.print(",");
    Serial.print(compensatedDiameter, 2);
    Serial.print(",");
    Serial.println(displayValue, 2);
  } else if (isGoToActive) {
    // Encoder-based GOTOPOS tracking — authoritative stop trigger
    float floatCm = (float)encSnap / PULSES_PER_CM - 2.0f;
    if (!goToEncoderReached && abs(floatCm - goToTargetCm) <= 0.5f) {
      goToEncoderReached = true;
      motorQueueTx("STOP");  // encoder says we're there — stop the motor
    }
    // Aggiorna solo la posizione durante GOTOPOS
    if (currentCm != lastCm) {
      lastCm = currentCm;
      String json = "{\"cm\":" + String(currentCm) + "}";
      webSocket.broadcastTXT(json);
    }
  }

  if (Serial.available()) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();
    handleCommand(cmd);
  }
}

// -----------------------------
// readCaliper() is now non-blocking: it just copies the latest ISR-decoded
// value into lineDiameter. If no fresh packet has arrived since the last
// call, lineDiameter keeps its previous value (correct — caliper unchanged).
float readCaliperAverage(int samples) {
  float sum = 0;
  int got = 0;
  unsigned long deadline = millis() + 2000;
  while (got < samples && millis() < deadline) {
    noInterrupts();
    bool rdy = calDataReady;
    interrupts();
    if (rdy) {
      readCaliper();
      sum += lineDiameter;
      got++;
    }
    delay(1);
  }
  return got > 0 ? sum / got : lineDiameter;
}

void readCaliper() {
  noInterrupts();
  if (calDataReady) {
    lineDiameter = (float)(calRawValue * calSign) / 200.0f;  // protocol LSB = 0.005 mm
    calDataReady = false;
  }
  interrupts();
}

// -----------------------------
void handleCommand(String cmd) {
  if (cmd.startsWith("goto ")) {
    float target = cmd.substring(5).toFloat();
    goToPosition(target);
    return;
  }

  if (cmd.startsWith("motor")) {
    String arg = cmd;
    arg.replace("motor", "");
    arg.trim();
    arg.toLowerCase();
    if (arg == "scan") motorQueueTx("SCAN");
    else if (arg == "stop") motorQueueTx("STOP");
    else if (arg == "fast_s") motorQueueTx("FAST_S");
    else if (arg == "fast_o") motorQueueTx("FAST_O");
    else motorQueueTx("STATUS?");
    return;
  }

  if (cmd == "help") {
    Serial.println("Comandi disponibili:");
    Serial.println("  reset               - Azzera lunghezza encoder e dati");
    Serial.println("  goto <cm>           - Vai alla posizione specificata in cm");
    Serial.println("  setoffset <val>     - Imposta offset di compensazione");
    Serial.println("  setdisplayzero      - Imposta display corrente come zero");
    Serial.println("  readraw             - Mostra lettura grezza del calibro");
    Serial.println("  resetoffset         - Azzera offset a 0");
    Serial.println("  getparams           - Mostra parametri correnti");
    Serial.println("  debugdata           - Mostra statistiche dati");
    Serial.println("  resetwifi           - Reset WiFi");
    Serial.println("  scan_on / scan_off  - Abilita/disabilita ricezione punti");
    Serial.println("  motor scan|stop|fast_s|fast_o|status");
    return;
  }

  if (cmd.startsWith("setoffset ")) {
    caliperZeroOffset = cmd.substring(10).toFloat();
    EEPROM.put(0, caliperZeroOffset);
    EEPROM.commit();
    Serial.print("Offset impostato a: ");
    Serial.println(caliperZeroOffset, 6);
    sendParamsToClients();
    return;
  }

  if (cmd == "setdisplayzero") {
    setDisplayZero();
    return;
  }

  if (cmd == "scan_on") {
    if (!isGoToActive) {
      scanEnabled = true;
      String json = "{\"type\":\"scan_enabled\",\"value\":true}";
      webSocket.broadcastTXT(json);
      Serial.println("Scansione ABILITATA");
    } else {
      Serial.println("Impossibile abilitare scansione durante GOTOPOS");
    }
    return;
  }

  if (cmd == "scan_off") {
    scanEnabled = false;
    String json = "{\"type\":\"scan_enabled\",\"value\":false}";
    webSocket.broadcastTXT(json);
    Serial.println("Scansione DISABILITATA");
    return;
  }

  if (cmd == "reset") {
    encoderValue = 0;
    lastCm = -1;
    clearAllData();
    Serial.println("Lunghezza azzerata e dati resettati.");
    return;
  }

  if (cmd == "readraw") {
    float display = readCaliperDisplay();
    Serial.print("Display calibro: ");
    Serial.println(display, 2);
    return;
  }

  if (cmd == "resetoffset") {
    caliperZeroOffset = 0.0;
    EEPROM.put(0, caliperZeroOffset);
    EEPROM.commit();
    Serial.println("Offset azzerato.");
    sendParamsToClients();
    return;
  }

  if (cmd == "getparams") {
    Serial.print("Parametri correnti - Zero Display: ");
    Serial.print(displayZeroValue, 2);
    Serial.print(" mm, Offset: ");
    Serial.println(caliperZeroOffset, 6);
    return;
  }

  if (cmd == "debugdata") {
    Serial.print("Punti dati totali: ");
    Serial.println(getTotalDataPoints());
    return;
  }

  if (cmd == "resetwifi") {
    Serial.println("Reset WiFi...");
    wifiManager.resetSettings();
    delay(1000);
    ESP.restart();
    return;
  }
}

// -----------------------------
void setDisplayZero() {
  displayZeroValue = readCaliperDisplay();
  Serial.print("Zero display impostato a: ");
  Serial.println(displayZeroValue, 2);
  sendParamsToClients();
}