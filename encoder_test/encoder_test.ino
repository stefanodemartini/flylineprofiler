// encoder_test.ino
// Minimal diagnostic sketch: encoder only, simple web page showing cm.
// No caliper, no motor, no WebSocket.
// Uses WiFiManager — connects automatically with saved credentials.
// Board: ESP32

#include <WiFi.h>
#include <WebServer.h>
#include <WiFiManager.h>

// ── Hardware ──────────────────────────────────────────────────────────────────
#define ENCODER_DATA_PIN  12
#define ENCODER_CLOCK_PIN 13

// ── Encoder calibration ───────────────────────────────────────────────────────
#define ENCODER_PPR    600
#define WHEEL_CIRC_MM  200
const float PPC = (ENCODER_PPR * 10.0f) / WHEEL_CIRC_MM;  // 30.0 pulses/cm

// ── State ────────────────────────────────────────────────────────────────────
volatile long encoderValue = 0;
volatile int  lastEncoded  = 0;

WebServer server(80);

// ── Encoder ISR ──────────────────────────────────────────────────────────────
void IRAM_ATTR updateEncoder() {
  int MSB = digitalRead(ENCODER_DATA_PIN);
  int LSB = digitalRead(ENCODER_CLOCK_PIN);
  int encoded = (MSB << 1) | LSB;
  int sum = (lastEncoded << 2) | encoded;
  if (sum == 0b1000) encoderValue++;
  if (sum == 0b0010) encoderValue--;
  lastEncoded = encoded;
}

// ── Web page ─────────────────────────────────────────────────────────────────
void handleRoot() {
  server.send(200, "text/html", R"rawliteral(
<!DOCTYPE html>
<html>
<head>
<meta charset="UTF-8">
<title>Encoder Test</title>
<style>
  body { font-family: monospace; background:#111; color:#0f0;
         display:flex; flex-direction:column; align-items:center;
         justify-content:center; height:100vh; margin:0; }
  #cm  { font-size:120px; font-weight:bold; }
  #ppc { font-size:18px; color:#888; margin-top:16px; }
  button { margin-top:32px; font-size:20px; padding:12px 32px;
           cursor:pointer; background:#222; color:#0f0; border:1px solid #0f0; }
</style>
</head>
<body>
  <div id="cm">0.0</div>
  <div id="ppc">PPC: --</div>
  <button onclick="reset()">RESET</button>
<script>
  function poll() {
    fetch('/data').then(r=>r.json()).then(d=>{
      document.getElementById('cm').textContent = d.cm.toFixed(1);
      document.getElementById('ppc').textContent = 'PPC: ' + d.ppc.toFixed(4) + ' | ticks: ' + d.ticks;
    }).catch(()=>{});
    setTimeout(poll, 200);
  }
  function reset() {
    fetch('/reset').then(()=>{});
  }
  poll();
</script>
</body>
</html>
)rawliteral");
}

void handleData() {
  noInterrupts();
  long ticks = encoderValue;
  interrupts();
  float cm = (float)ticks / PPC;
  String json = "{\"cm\":" + String(cm, 2) +
                ",\"ticks\":" + String(ticks) +
                ",\"ppc\":" + String(PPC, 4) + "}";
  server.send(200, "application/json", json);
}

void handleReset() {
  noInterrupts();
  encoderValue = 0;
  interrupts();
  server.send(200, "text/plain", "OK");
}

// ── Setup / Loop ─────────────────────────────────────────────────────────────
void setup() {
  Serial.begin(115200);

  pinMode(ENCODER_DATA_PIN,  INPUT_PULLUP);
  pinMode(ENCODER_CLOCK_PIN, INPUT_PULLUP);
  attachInterrupt(digitalPinToInterrupt(ENCODER_DATA_PIN),  updateEncoder, CHANGE);
  attachInterrupt(digitalPinToInterrupt(ENCODER_CLOCK_PIN), updateEncoder, CHANGE);

  WiFiManager wm;
  wm.setConfigPortalTimeout(120);
  if (!wm.autoConnect("EncoderTest_Setup", "12345678")) {
    Serial.println("WiFi failed, restarting");
    ESP.restart();
  }
  Serial.print("IP: ");
  Serial.println(WiFi.localIP());

  server.on("/",      handleRoot);
  server.on("/data",  handleData);
  server.on("/reset", handleReset);
  server.begin();
}

void loop() {
  server.handleClient();

  // Print to Serial every second for logging
  static unsigned long lastPrint = 0;
  if (millis() - lastPrint >= 1000) {
    lastPrint = millis();
    noInterrupts();
    long ticks = encoderValue;
    interrupts();
    Serial.print("ticks=");
    Serial.print(ticks);
    Serial.print("  cm=");
    Serial.println((float)ticks / PPC, 2);
  }
}
