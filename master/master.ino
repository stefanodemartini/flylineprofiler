#include <WiFi.h>
#include <WebServer.h>
#include <WebSocketsServer.h>
#include <EEPROM.h>
#include <WiFiManager.h> // https://github.com/tzapu/WiFiManager
#include <HardwareSerial.h>

#define FIRMWARE_VERSION "2.3.0"
#define FIRMWARE_DATE "2026-02-07"
#define FIRMWARE_FEATURES "WiFi Manager + EMA + 0.01mm + UART Motor"

// -----------------------------
// Pin Encoder
// -----------------------------
#define ENCODER_DATA_PIN 12
#define ENCODER_CLOCK_PIN 13

// -----------------------------
// Pin Calibro
// -----------------------------
#define CALIPER_DATA_PIN 27
#define CALIPER_CLOCK_PIN 26

// -----------------------------
// Pin WiFi Reset
// -----------------------------
#define WIFI_RESET_PIN 14

// -----------------------------
#define PULSES_PER_REV 600
#define PULSES_PER_CM 30
#define EEPROM_SIZE 512

// Variabili globali
float lineDiameter = 0.0;

// Filtro esponenziale
#define ALPHA 0.2
float smoothedDiameter = 0.0;
bool filterInitialized = false;

// -----------------------------
// UART2 verso ESP32 motore (bidirezionale)
// -----------------------------
static const int MOTOR_UART_RX_PIN = 16;  // RX2
static const int MOTOR_UART_TX_PIN = 17;  // TX2
HardwareSerial SerialMotor(2);

String motorMode = "UNKNOWN";
String motorDir  = "UNKNOWN";
unsigned long motorLastSeenMs = 0;

// Struttura per gestione dati illimitati
struct DataPoint {
  int cm;
  float diameter;    // Diametro misurato (compensato con offset)
  float rawDisplay;  // Valore mostrato sul display del calibro
  DataPoint* next;
};

DataPoint* firstDataPoint = nullptr;
DataPoint* lastDataPoint = nullptr;
int totalDataPoints = 0;

volatile long encoderValue = 0;
volatile int lastEncoded = 0;

float caliperZeroOffset = 0.0;
float displayZeroValue = 0.0;  // Valore mostrato sul display del calibro come riferimento 0

int lastCm = -1;

// Variabili per il monitoraggio velocità
unsigned long lastSpeedTime = 0;
long lastSpeedEncoder = 0;
float currentSpeed = 0.0;

WiFiManager wifiManager;
WebServer server(80);
WebSocketsServer webSocket(81);

// -----------------------------
// Forward declarations
// -----------------------------
void readCaliper();
void decodeCaliperReadings();
float readCaliperAverage(int samples = 3);
float readCaliperDisplay();  // Legge il valore mostrato sul display
void handleCommand(String cmd);
void sendParamsToClients();
void calculateSpeed();
void addDataPoint(int cm, float diameter, float rawDisplay);
void clearAllData();
int getTotalDataPoints();
void setDisplayZero();

// -----------------------------
// Gestione Dati Illimitati
// -----------------------------
void addDataPoint(int cm, float diameter, float rawDisplay) {
  DataPoint* newPoint = new DataPoint();
  newPoint->cm = cm;
  newPoint->diameter = diameter;
  newPoint->rawDisplay = rawDisplay;
  newPoint->next = nullptr;

  if (firstDataPoint == nullptr) {
    firstDataPoint = newPoint;
    lastDataPoint = newPoint;
  } else {
    // Verifica se il punto già esiste (per aggiornamento)
    DataPoint* current = firstDataPoint;
    DataPoint* prev = nullptr;
    while (current != nullptr) {
      if (current->cm == cm) {
        // Aggiorna il punto esistente
        current->diameter = diameter;
        current->rawDisplay = rawDisplay;
        delete newPoint;
        return;
      }
      if (current->cm > cm) {
        // Inserisci in ordine
        newPoint->next = current;
        if (prev == nullptr) firstDataPoint = newPoint;
        else prev->next = newPoint;
        totalDataPoints++;
        return;
      }
      prev = current;
      current = current->next;
    }

    // Inserisci alla fine
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
}

int getTotalDataPoints() {
  return totalDataPoints;
}

// -----------------------------
// Encoder interrupt
// -----------------------------
void IRAM_ATTR updateEncoder() {
  int MSB = digitalRead(ENCODER_DATA_PIN);
  int LSB = digitalRead(ENCODER_CLOCK_PIN);
  int encoded = (MSB << 1) | LSB;
  int sum = (lastEncoded << 2) | encoded;
  if (sum == 0b1000) encoderValue++;
  if (sum == 0b0010) encoderValue--;
  lastEncoded = encoded;
}

// -----------------------------
void setup() {
  Serial.begin(115200);
  EEPROM.begin(EEPROM_SIZE);

  // UART motore
  SerialMotor.begin(115200, SERIAL_8N1, MOTOR_UART_RX_PIN, MOTOR_UART_TX_PIN);

  pinMode(ENCODER_DATA_PIN, INPUT_PULLUP);
  pinMode(ENCODER_CLOCK_PIN, INPUT_PULLUP);
  pinMode(CALIPER_CLOCK_PIN, INPUT);
  pinMode(CALIPER_DATA_PIN, INPUT);

  attachInterrupt(digitalPinToInterrupt(ENCODER_DATA_PIN), updateEncoder, CHANGE);
  attachInterrupt(digitalPinToInterrupt(ENCODER_CLOCK_PIN), updateEncoder, CHANGE);

  // Carica offset da EEPROM
  EEPROM.get(0, caliperZeroOffset);
  if (isnan(caliperZeroOffset)) caliperZeroOffset = 0.0;

  // Inizializza il monitoraggio velocità
  lastSpeedTime = millis();
  lastSpeedEncoder = encoderValue;

  // WiFi Manager
  Serial.println("\n=== WiFi Manager v2.3.0 ===");
  pinMode(WIFI_RESET_PIN, INPUT_PULLUP);
  if (digitalRead(WIFI_RESET_PIN) == LOW) {
    Serial.println("Reset WiFi!");
    wifiManager.resetSettings();
    delay(1000);
  }
  wifiManager.setConfigPortalTimeout(180);
  wifiManager.setAPCallback([](WiFiManager *m) {
    Serial.println("AP: " + String(m->getConfigPortalSSID()));
    Serial.println("Pwd: 12345678 | IP: 192.168.4.1");
  });
  if (!wifiManager.autoConnect("DiametroLinea_Setup", "12345678")) {
    Serial.println("Timeout!");
    delay(3000);
    ESP.restart();
  }
  Serial.println("\nWiFi OK! IP: " + WiFi.localIP().toString());

  // Web server endpoints
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
        body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; }
        .container { max-width: 1200px; margin: 0 auto; background: white; padding: 20px; border-radius: 10px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .header { text-align: center; margin-bottom: 30px; padding-bottom: 20px; border-bottom: 2px solid #eee; }
        .status-panel { display: flex; justify-content: space-around; margin-bottom: 20px; padding: 15px; background: #f8f9fa; border-radius: 8px; }
        .status-item { text-align: center; }
        .status-value { font-size: 1.2em; font-weight: bold; color: #007bff; }
        .status-item .speed-optimal { color: #28a745; font-weight: bold; }
        .status-item .speed-too-slow { color: #ffc107; font-weight: bold; }
        .status-item .speed-too-fast { color: #dc3545; font-weight: bold; }
        .chart-wrapper { position: relative; margin-bottom: 20px; }
        .chart-container { position: relative; height: 500px; border: 1px solid #ddd; border-radius: 5px; background: white; }
        .chart-controls { display: flex; justify-content: center; align-items: center; margin-top: 10px; padding: 10px; background: #f8f9fa; border-radius: 5px; }
        .zoom-controls { display: flex; gap: 10px; }
        .controls { text-align: center; margin-top: 20px; }
        .control-group { margin: 10px 0; padding: 10px; background: #f8f9fa; border-radius: 5px; }
        .control-group h3 { margin-top: 0; color: #333; }
        button { padding: 8px 16px; margin: 5px; background: #007bff; color: white; border: none; border-radius: 5px; cursor: pointer; font-size: 14px; }
        button:hover { background: #0056b3; }
        button.danger { background: #dc3545; }
        button.danger:hover { background: #c82333; }
        button.success { background: #28a745; }
        button.success:hover { background: #218838; }
        button.warning { background: #ffc107; color: #212529; }
        button.warning:hover { background: #e0a800; }
        .auto-control { display: inline-block; margin: 0 10px; }
        .auto-control input { padding: 8px; margin: 0 5px; border: 1px solid #ddd; border-radius: 4px; width: 80px; }
        .param-display { display: inline-block; margin: 0 15px; padding: 5px 10px; background: #e9ecef; border-radius: 4px; font-family: monospace; }
        .upload-section { margin: 20px 0; padding: 15px; background: #f8f9fa; border-radius: 8px; border: 2px dashed #dee2e6; }
        .upload-area { text-align: center; padding: 20px; }
        .file-input { margin: 10px 0; }
        .upload-info { margin-top: 10px; font-size: 0.9em; color: #6c757d; }
        .dataset-list { margin-top: 10px; max-height: 200px; overflow-y: auto; border: 1px solid #ddd; border-radius: 5px; padding: 10px; background: white; }
        .dataset-item { display: flex; justify-content: space-between; align-items: center; padding: 5px; margin: 2px 0; border-radius: 3px; background: #f8f9fa; }
        .dataset-item:hover { background: #e9ecef; }
        .dataset-color { width: 20px; height: 20px; border-radius: 3px; margin-right: 10px; }
        .dataset-name { flex-grow: 1; font-size: 0.9em; }
        .dataset-controls { display: flex; gap: 5px; }
        .color-palette { display: flex; gap: 5px; margin: 10px 0; flex-wrap: wrap; }
        .color-option { width: 25px; height: 25px; border-radius: 3px; cursor: pointer; border: 2px solid transparent; }
        .color-option.selected { border-color: #000; }
        .info-badge { display: inline-block; padding: 2px 8px; background: #007bff; color: white; border-radius: 12px; font-size: 0.85em; margin-left: 10px; }
        .display-info { background: #e8f4f8; padding: 10px; border-radius: 5px; margin: 10px 0; }
    </style>
</head>
<body>
    <div class="container">
        <div class="header">
            <h1>Sistema Monitoraggio Diametro Linea</h1>
            <p>Monitoraggio in tempo reale del diametro <span class="info-badge">CALIBRO FISSO</span></p>
        </div>

        <div class="status-panel">
            <div class="status-item"><div>Lunghezza Attuale</div><div class="status-value" id="currentLength">0 cm</div></div>
            <div class="status-item"><div>Diametro Compensato</div><div class="status-value" id="currentDiameter">0.00 mm</div></div>
            <div class="status-item"><div>Display Calibro</div><div class="status-value" id="currentDisplay">0.00 mm</div></div>
            <div class="status-item"><div>Velocit&agrave;</div><div class="status-value" id="currentSpeed">0.00 cm/s</div></div>
            <div class="status-item"><div>Stato Velocit&agrave;</div><div class="status-value" id="speedStatus">Ottimale</div></div>
            <div class="status-item"><div>Punti Registrati</div><div class="status-value" id="dataPointsCount">0</div></div>
            <div class="status-item"><div>Stato Connessione</div><div class="status-value" id="connectionStatus">Connesso</div></div>
        </div>

        <div class="display-info">
            <strong>Configurazione:</strong> Calibro fisso (punto di misura 20mm dietro encoder) |
            <strong>Zero Display:</strong> <span id="displayZeroValue">0.00</span> mm |
            <strong>Offset:</strong> <span id="currentOffset">0.00</span> mm |
            <strong>Motore:</strong> <span id="motorState">--</span>
        </div>

        <div class="chart-wrapper">
            <div class="chart-container"><canvas id="lineChart"></canvas></div>
            <div class="chart-controls">
                <div class="zoom-controls">
                    <button onclick="zoomIn()">Zoom +</button>
                    <button onclick="zoomOut()">Zoom -</button>
                    <button onclick="resetZoom()">Reset Zoom</button>
                    <button onclick="fitToData()">Adatta ai Dati</button>
                </div>
            </div>
        </div>

        <div class="controls">
            <div class="control-group">
                <h3>Controlli Motore Trascinamento</h3>
                <button class="success" onclick="sendCommand('motor scan')">START SCAN</button>
                <button class="danger"  onclick="sendCommand('motor stop')">STOP</button>
                <button class="warning" onclick="sendCommand('motor fast_s')">FAST stessa dir</button>
                <button class="warning" onclick="sendCommand('motor fast_o')">FAST opposta dir</button>
                <button onclick="sendCommand('motor status')">Aggiorna Stato</button>
            </div>

            <div class="control-group">
                <h3>Controlli Grafico</h3>
                <button onclick="resetChart()">Reset Grafico</button>
                <button onclick="exportAllData()">Esporta Tutti i Dati CSV</button>
                <button onclick="toggleAutoScale()">Auto-Fit: <span id="autoScaleStatus">ON</span></button>
            </div>

            <div class="control-group">
                <h3>Controlli Sistema</h3>
                <button onclick="sendCommand('reset')">Reset Lunghezza e Dati</button>
                <button onclick="sendCommand('readraw')">Lettura Display Calibro</button>
                <button onclick="sendCommand('setdisplayzero')">Imposta Zero da Display</button>
            </div>

            <div class="control-group">
                <h3>Calibrazione Offset</h3>
                <div class="auto-control">
                    <input type="number" id="offsetValue" placeholder="offset mm" step="0.01">
                    <button onclick="setOffset()">Imposta Offset</button>
                </div>
                <button onclick="sendCommand('resetoffset')">Resetta Offset a 0</button>
            </div>

            <div class="control-group">
                <h3>Parametri Correnti</h3>
                <div>
                    <span>Zero Display: </span><span class="param-display" id="currentDisplayZero">0.00</span>
                    <span> mm | Offset: </span><span class="param-display" id="currentOffsetValue">0.00</span><span> mm</span>
                </div>
            </div>
        </div>

        <div style="margin-top: 20px; text-align: center; color: #666;">
            <p>Ultimo aggiornamento: <span id="lastUpdate">--</span></p>
            <p id="commandStatus"></p>
            <hr style="border: none; border-top: 1px solid #ddd; margin: 15px 0;">
            <p style="font-size: 0.85em; color: #999;">
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

    <script>
        let chart;
        let dataPoints = [];
        let autoScaleEnabled = true;
        let ws;
        let zeroPoint = {x: 0, y: 0};
        let currentDisplayZero = 0.0;
        let currentOffset = 0.0;

        const OPTIMAL_SPEED_MIN = 0.5;
        const OPTIMAL_SPEED_MAX = 2.0;

        function initializeChart() {
            const ctx = document.getElementById('lineChart').getContext('2d');
            chart = new Chart(ctx, {
                type: 'line',
                data: {
                    datasets: [{
                        label: 'Diametro Compensato (mm)',
                        data: dataPoints,
                        borderColor: '#007bff',
                        backgroundColor: 'rgba(0, 123, 255, 0.1)',
                        borderWidth: 2,
                        fill: false,
                        pointRadius: 3,
                        pointBackgroundColor: '#007bff',
                        pointBorderColor: '#ffffff',
                        pointBorderWidth: 1
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: { duration: 0 },
                    scales: {
                        x: { type: 'linear', title: { display: true, text: 'Lunghezza (cm)', font: { size: 14, weight: 'bold' } }, grid: { color: 'rgba(0,0,0,0.1)' } },
                        y: { title: { display: true, text: 'Diametro Compensato (mm)', font: { size: 14, weight: 'bold' } }, grid: { color: 'rgba(0,0,0,0.1)' } }
                    },
                    plugins: {
                        legend: { display: true, position: 'top' },
                        tooltip: { mode: 'index', intersect: false,
                            callbacks: { label: function(context) { return `${context.dataset.label}: ${context.parsed.y.toFixed(3)} mm a ${context.parsed.x} cm`; } }
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
                    } else if (msg.cm !== undefined) {
                        updateDisplay(msg);
                    }
                } catch(e) {
                    console.error('Errore parsing JSON:', e);
                }
            };
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
            const point = {x: x, y: y};
            ensureZeroPoint();

            const existingIndex = dataPoints.findIndex(p => p.x === x);
            if (existingIndex !== -1) dataPoints[existingIndex] = point;
            else dataPoints.push(point);

            dataPoints.sort((a, b) => a.x - b.x);
            chart.data.datasets[0].data = dataPoints;

            document.getElementById('dataPointsCount').textContent = dataPoints.length;

            if (autoScaleEnabled) fitToData();
            else chart.update('none');
        }

        function zoomIn() { chart.zoom(1.1); }
        function zoomOut() { chart.zoom(0.9); }
        function resetZoom() { chart.resetZoom(); if (autoScaleEnabled) fitToData(); }

        function fitToData() {
            if (dataPoints.length === 0) return;
            const xValues = dataPoints.map(p => p.x);
            const yValues = dataPoints.map(p => p.y);

            let minX = Math.min(...xValues), maxX = Math.max(...xValues);
            let minY = Math.min(...yValues), maxY = Math.max(...yValues);

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
            if (autoScaleEnabled) fitToData();
            else chart.update();
            document.getElementById('dataPointsCount').textContent = '0';
            showCommandStatus('Grafico resettato');
        }

        function exportAllData() {
            showCommandStatus('Usa il tasto Export dal firmware (endpoint /export) o la funzione confronto CSV');
        }

        function toggleAutoScale() {
            autoScaleEnabled = !autoScaleEnabled;
            document.getElementById('autoScaleStatus').textContent = autoScaleEnabled ? 'ON' : 'OFF';
            if (autoScaleEnabled) fitToData();
            else chart.update();
            showCommandStatus('Auto-Fit: ' + (autoScaleEnabled ? 'ON' : 'OFF'));
        }

        function sendCommand(command) {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(command);
                showCommandStatus('Comando inviato: ' + command);
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

        document.addEventListener('DOMContentLoaded', function() {
            initializeChart();
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
    String csv = "Dataset,Lunghezza (cm),Diametro (mm),Display (mm)\n";

    DataPoint* current = firstDataPoint;
    while (current != nullptr) {
      csv += "\"Live\"," + String(current->cm) + "," + String(current->diameter, 3) + "," + String(current->rawDisplay, 3) + "\n";
      current = current->next;
    }

    server.send(200, "text/csv", csv);
    server.sendHeader("Content-Disposition", "attachment; filename=diametro_linea_completo.csv");
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

  // chiedo subito stato motore (manuale: non avvio nulla)
  SerialMotor.println("STATUS?");
}

// -----------------------------
// CALCOLO VELOCITÀ
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

// -----------------------------
// Lettura valore display calibro
// -----------------------------
float readCaliperDisplay() {
  readCaliper();
  return lineDiameter;
}

// -----------------------------
void loop() {
  server.handleClient();
  webSocket.loop();

  // RX stato motore via UART -> invio al browser
  if (SerialMotor.available()) {
    String line = SerialMotor.readStringUntil('\n');
    line.trim();

    if (line.startsWith("STATUS:")) {
      // STATUS:<MODE>:<DIR>
      int p1 = line.indexOf(':');           // dopo STATUS
      int p2 = line.indexOf(':', p1 + 1);   // separatore mode/dir
      if (p2 > 0) {
        motorMode = line.substring(p1 + 1, p2);
        motorDir  = line.substring(p2 + 1);
        motorLastSeenMs = millis();

        String json = "{\"type\":\"motor\",\"mode\":\"" + motorMode + "\",\"dir\":\"" + motorDir + "\"}";
        webSocket.broadcastTXT(json);
      }
    }
  }

  calculateSpeed();

  // MODIFICA: Sottrai 2 cm per compensare la distanza calibro-encoder
  int currentCm = (encoderValue / PULSES_PER_CM) - 2;

  if (currentCm != lastCm && currentCm >= 0) {
    lastCm = currentCm;

    float displayValue = readCaliperDisplay();

    if (!filterInitialized) {
      smoothedDiameter = displayValue;
      filterInitialized = true;
    } else {
      smoothedDiameter = (ALPHA * displayValue) + ((1.0 - ALPHA) * smoothedDiameter);
    }

    displayValue = smoothedDiameter;

    float compensatedDiameter = (displayValue - displayZeroValue) - caliperZeroOffset;
    compensatedDiameter = round(compensatedDiameter * 1000.0) / 1000.0;

    addDataPoint(currentCm, compensatedDiameter, displayValue);

    String json = "{\"cm\":" + String(currentCm) +
                  ",\"diameter\":" + String(compensatedDiameter, 2) +
                  ",\"rawDisplay\":" + String(displayValue, 2) +
                  ",\"totalPoints\":" + String(getTotalDataPoints()) + "}";
    webSocket.broadcastTXT(json);

    Serial.print(currentCm);
    Serial.print(",");
    Serial.print(compensatedDiameter, 2);
    Serial.print(",");
    Serial.println(displayValue, 2);
  }

  if (Serial.available()) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();
    handleCommand(cmd);
  }
}

// -----------------------------
// LETTURA CALIBRO (INVARIATA)
// -----------------------------
float readCaliperAverage(int samples) {
  float sum = 0;
  for (int i = 0; i < samples; i++) {
    readCaliper();
    sum += lineDiameter;
    delay(10);
  }
  return sum / samples;
}

void readCaliper() {
  while (digitalRead(CALIPER_CLOCK_PIN) == LOW) {}
  long tmpMicros = micros();
  while (digitalRead(CALIPER_CLOCK_PIN) == HIGH) {}
  if ((micros() - tmpMicros) > 500) decodeCaliperReadings();
}

void decodeCaliperReadings() {
  long value = 0;
  int sign = 1;
  for (int i = 0; i < 24; i++) {
    while (digitalRead(CALIPER_CLOCK_PIN) == LOW) {}
    while (digitalRead(CALIPER_CLOCK_PIN) == HIGH) {}
    if (digitalRead(CALIPER_DATA_PIN)) {
      if (i < 20) value |= (1 << i);
      if (i == 20) sign = -1;
    }
  }
  lineDiameter = (value * sign) / 100.0;
}

// -----------------------------
// COMANDI SERIALI + MOTORE
// -----------------------------
void handleCommand(String cmd) {
  // ---- COMANDI MOTORE via UART (manuali via web) ----
  // motor scan | motor stop | motor fast_s | motor fast_o | motor status
  if (cmd.startsWith("motor")) {
    String arg = cmd;
    arg.replace("motor", "");
    arg.trim();
    arg.toLowerCase();

    if (arg == "scan") SerialMotor.println("SCAN");
    else if (arg == "stop") SerialMotor.println("STOP");
    else if (arg == "fast_s") SerialMotor.println("FAST_S");
    else if (arg == "fast_o") SerialMotor.println("FAST_O");
    else SerialMotor.println("STATUS?");

    return;
  }

  if (cmd == "help") {
    Serial.println("Comandi disponibili:");
    Serial.println("  reset               - Azzera la lunghezza (encoder) e dati");
    Serial.println("  setoffset <val>     - Imposta offset di compensazione");
    Serial.println("  setdisplayzero      - Imposta display corrente come zero riferimento");
    Serial.println("  readraw             - Mostra lettura grezza del calibro (display)");
    Serial.println("  resetoffset         - Azzera offset (a 0)");
    Serial.println("  getparams           - Mostra parametri correnti");
    Serial.println("  debugdata           - Mostra statistiche dati");
    Serial.println("  resetwifi           - Reset WiFi");
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

  if (cmd == "reset") {
    encoderValue = 0;
    lastCm = -1;
    clearAllData();
    Serial.println("Lunghezza azzerata e dati resetati.");
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
// Imposta zero dal display
// -----------------------------
void setDisplayZero() {
  displayZeroValue = readCaliperDisplay();
  Serial.print("Zero display impostato a: ");
  Serial.println(displayZeroValue, 2);
  sendParamsToClients();
}
