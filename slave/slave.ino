#include <FastAccelStepper.h>

// ===============================
// PIN TB6600
// ===============================
const int PUL_PIN = 4;
const int DIR_PIN = 5;
const int ENA_PIN = 6;

// ===============================
// PIN PULSANTI (ATTIVI LOW)
// ===============================
const int BTN_SCAN   = 10;
const int BTN_FAST_S = 11;
const int BTN_FAST_O = 12;

// ===============================
// UART2 BIDIREZIONALE (16/17)
// ===============================
static const int UART_RX_PIN = 16;  // RX2
static const int UART_TX_PIN = 17;  // TX2
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
FastAccelStepperEngine engine;
FastAccelStepper *stepper = nullptr;

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

void sendStatus() {
  // STATUS:<MODE>:<DIR>
  String s = "STATUS:";
  s += modeToStr(mode);
  s += ":";
  s += (currentDirForward ? "FWD" : "BWD");
  SerialMaster.println(s);
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

void handleRemoteCommand(const String& cmd) {
  if (cmd == "SCAN") {
    applyAndRun(currentDirForward, SCAN_SPEED_HZ);
    mode = MODE_SCAN;
    sendStatus();
    return;
  }
  if (cmd == "STOP") {
    stopMotor();
    sendStatus();
    return;
  }
  if (cmd == "FAST_S") {
    applyAndRun(currentDirForward, FAST_SPEED_HZ);
    mode = MODE_FAST_SAME;
    sendStatus();
    return;
  }
  if (cmd == "FAST_O") {
    applyAndRun(!currentDirForward, FAST_SPEED_HZ);
    mode = MODE_FAST_OPP;
    sendStatus();
    return;
  }
  if (cmd == "STATUS?") {
    sendStatus();
    return;
  }

  // sconosciuto: rispondo comunque con stato
  sendStatus();
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
  stepper->setEnablePin(ENA_PIN);
  stepper->setAutoEnable(true);

  applyAndRun(true, SCAN_SPEED_HZ);
  stopMotor();

  sendStatus();
}

void loop() {
  const unsigned long now = millis();

  // UART RX da master
  if (SerialMaster.available()) {
    String cmd = SerialMaster.readStringUntil('\n');
    cmd.trim();
    if (cmd.length()) handleRemoteCommand(cmd);
  }

  // Pulsanti fisici (rimangono)
  if (pressed(BTN_SCAN) && (now - lastScanMs > debounceDelay)) {
    if (mode == MODE_SCAN) stopMotor();
    else { applyAndRun(currentDirForward, SCAN_SPEED_HZ); mode = MODE_SCAN; }
    lastScanMs = now;
    sendStatus();
  }

  if (pressed(BTN_FAST_S) && (now - lastFastSMs > debounceDelay)) {
    if (mode == MODE_FAST_SAME) stopMotor();
    else { applyAndRun(currentDirForward, FAST_SPEED_HZ); mode = MODE_FAST_SAME; }
    lastFastSMs = now;
    sendStatus();
  }

  if (pressed(BTN_FAST_O) && (now - lastFastOMs > debounceDelay)) {
    if (mode == MODE_FAST_OPP) stopMotor();
    else { applyAndRun(!currentDirForward, FAST_SPEED_HZ); mode = MODE_FAST_OPP; }
    lastFastOMs = now;
    sendStatus();
  }

  delay(5);
}
