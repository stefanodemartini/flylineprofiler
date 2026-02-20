#include <FastAccelStepper.h>

// ===============================
// FW
// ===============================
#define FIRMWARE_VERSION "0.2.0"
#define FIRMWARE_DATE "2026-02-20"
#define FIRMWARE_FEATURES "UART Controlled Motor"

// ===============================
// PIN TB6600
// ===============================
const int PUL_PIN = 4;
const int DIR_PIN = 5;
const int ENA_PIN = 6;

// ===============================
// PIN PULSANTI (ATTIVI LOW)
// ===============================
const int BTN_SCAN   = 10;  // toggle SCAN
const int BTN_FAST_S = 11;  // toggle FAST stessa direzione
const int BTN_FAST_O = 12;  // toggle FAST direzione opposta

// ===============================
// UART2 BIDIREZIONALE (VERSO MASTER)
// ===============================
static const int UART_RX_PIN = 21;  // RX slave
static const int UART_TX_PIN = 18;  // TX slave
HardwareSerial SerialMaster(2);

// ===============================
// PARAMETRI (1/32 microstepping)
// ===============================
const uint32_t SCAN_SPEED_HZ = 1500;
const uint32_t FAST_SPEED_HZ = SCAN_SPEED_HZ * 8;
const int32_t  ACCELERATION  = 1500;

// ===============================
// DEBOUNCE
// ===============================
const unsigned long debounceDelay = 250;
unsigned long lastScanMs = 0, lastFastSMs = 0, lastFastOMs = 0;

// ===============================
// STATO
// ===============================
enum Mode { MODE_STOPPED, MODE_SCAN, MODE_FAST_SAME, MODE_FAST_OPP };
Mode mode = MODE_STOPPED;
bool currentDirForward = true;

// ===============================
// FASTACCELSTEPPER
// ===============================
FastAccelStepperEngine engine = FastAccelStepperEngine();
FastAccelStepper *stepper = nullptr;

// ===============================
// UART RX line buffer (non bloccante)
// ===============================
static char rxLine[32];
static size_t rxLen = 0;

// ===============================
// UART TX status queue (no-loss)
// ===============================
static String pendingTx;
static bool txPending = false;

// ===============================
// UTILS
// ===============================
bool pressed(int pin) { return digitalRead(pin) == LOW; }

const char* modeToStr(Mode m) {
  switch (m) {
    case MODE_STOPPED:   return "STOPPED";
    case MODE_SCAN:      return "SCAN";
    case MODE_FAST_SAME: return "FAST_SAME";
    case MODE_FAST_OPP:  return "FAST_OPP";
  }
  return "UNKNOWN";
}

void queueStatus() {
  pendingTx = String("STATUS:") + modeToStr(mode) + ":" + (currentDirForward ? "FWD" : "BWD") + "\n";
  txPending = true;
}

void pumpUartTx() {
  if (!txPending) return;
  int freeBytes = SerialMaster.availableForWrite();
  if (freeBytes >= (int)pendingTx.length()) {
    SerialMaster.print(pendingTx);
    txPending = false;
  }
}

void applyAndRun(bool forward, uint32_t speed_hz) {
  currentDirForward = forward;
  stepper->setSpeedInHz(speed_hz);
  stepper->setAcceleration(ACCELERATION);
  if (forward) stepper->runForward();
  else stepper->runBackward();
}

void stopMotor() {
  stepper->stopMove();
  mode = MODE_STOPPED;
}

// ===============================
// UART command handler
// ===============================
void handleRemoteCommand(const char* cmd) {
  if (strcmp(cmd, "SCAN") == 0) {
    applyAndRun(currentDirForward, SCAN_SPEED_HZ);
    mode = MODE_SCAN;
    queueStatus();
    return;
  }
  if (strcmp(cmd, "STOP") == 0) {
    stopMotor();
    queueStatus();
    return;
  }
  if (strcmp(cmd, "FAST_S") == 0) {
    applyAndRun(currentDirForward, FAST_SPEED_HZ);
    mode = MODE_FAST_SAME;
    queueStatus();
    return;
  }
  if (strcmp(cmd, "FAST_O") == 0) {
    applyAndRun(!currentDirForward, FAST_SPEED_HZ);
    mode = MODE_FAST_OPP;
    queueStatus();
    return;
  }
  if (strcmp(cmd, "STATUS?") == 0) {
    queueStatus();
    return;
  }

  // sconosciuto: rispondo comunque
  queueStatus();
}

void pumpUartRx() {
  while (SerialMaster.available() > 0) {
    char c = (char)SerialMaster.read();
    if (c == '\r') continue;

    if (c == '\n') {
      rxLine[rxLen] = '\0';
      if (rxLen > 0) handleRemoteCommand(rxLine);
      rxLen = 0;
      continue;
    }

    if (rxLen < sizeof(rxLine) - 1) rxLine[rxLen++] = c;
    else rxLen = 0;
  }
}

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(BTN_SCAN,   INPUT_PULLUP);
  pinMode(BTN_FAST_S, INPUT_PULLUP);
  pinMode(BTN_FAST_O, INPUT_PULLUP);

  SerialMaster.begin(115200, SERIAL_8N1, UART_RX_PIN, UART_TX_PIN);

  engine.init();
  stepper = engine.stepperConnectToPin(PUL_PIN);
  if (!stepper) {
    Serial.println("ERRORE: stepperConnectToPin fallita");
    while (true) delay(1000);
  }

  stepper->setDirectionPin(DIR_PIN);

  // IDENTICO al tuo codice funzionante:
  stepper->setEnablePin(ENA_PIN);
  stepper->setAutoEnable(true);

  // IDENTICO al tuo codice funzionante:
  applyAndRun(true, SCAN_SPEED_HZ);
  stopMotor();

  queueStatus();

  Serial.println("Pronto: bottoni + UART (SCAN/STOP/FAST_S/FAST_O/STATUS?)");
}

void loop() {
  const unsigned long now = millis();

  // UART
  pumpUartRx();
  pumpUartTx();

  // BTN_SCAN (toggle)
  if (pressed(BTN_SCAN) && (now - lastScanMs > debounceDelay)) {
    if (mode == MODE_SCAN) stopMotor();
    else { applyAndRun(currentDirForward, SCAN_SPEED_HZ); mode = MODE_SCAN; }
    lastScanMs = now;
    queueStatus();
  }

  // BTN_FAST_S (toggle)
  if (pressed(BTN_FAST_S) && (now - lastFastSMs > debounceDelay)) {
    if (mode == MODE_FAST_SAME) stopMotor();
    else { applyAndRun(currentDirForward, FAST_SPEED_HZ); mode = MODE_FAST_SAME; }
    lastFastSMs = now;
    queueStatus();
  }

  // BTN_FAST_O (toggle)
  if (pressed(BTN_FAST_O) && (now - lastFastOMs > debounceDelay)) {
    if (mode == MODE_FAST_OPP) stopMotor();
    else { applyAndRun(!currentDirForward, FAST_SPEED_HZ); mode = MODE_FAST_OPP; }
    lastFastOMs = now;
    queueStatus();
  }

  delay(5);
}
