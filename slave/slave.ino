// ===============================
// FW SLAVE - Controllo Motore Passo-Passo (LEDC)
// ===============================
#define FIRMWARE_VERSION "0.5.0"
#define FIRMWARE_DATE    "2026-04-30"
#define FIRMWARE_FEATURES "LEDC constant-speed stepper, instant start/stop, encoder-driven from master"

// -----------------------------
// Pin definitions
#define PUL_PIN    4
#define DIR_PIN    5
#define ENA_PIN    6
#define BTN_SCAN   10
#define BTN_FAST_S 11
#define BTN_FAST_O 12
#define UART_RX    21
#define UART_TX    18

// -----------------------------
// LEDC (ESP32 Arduino core v3 pin-based API)
#define STEP_LEDC_RES 8     // 8-bit resolution
#define STEP_DUTY_50  128   // 50% duty → continuous step pulses
#define STEP_DUTY_OFF 0

// -----------------------------
// UART
HardwareSerial SerialMaster(2);

// -----------------------------
// Motor speeds (Hz)
const uint32_t SCAN_HZ      = 1500;
const uint32_t FAST_HZ      = 12000;
const uint32_t STEP_SCAN_HZ = 800;   // ~1s per cm; increase if motor starts cleanly

// Failsafe: stop GOTOPOS if master goes silent
const unsigned long GOTOPOS_TIMEOUT_MS = 30000;

// -----------------------------
// State
enum Mode { STOPPED, SCAN, FAST_S, FAST_O, GOTOPOS };
Mode mode             = STOPPED;
bool fwd              = true;
unsigned long gotoStartMs = 0;

// -----------------------------
// UART buffers
char   rxBuffer[64];
size_t rxLen = 0;
String txPending;
bool   txPendingFlag = false;

// -----------------------------
// Button debounce
unsigned long lastDebounceScan  = 0;
unsigned long lastDebounceFastS = 0;
unsigned long lastDebounceFastO = 0;
const unsigned long DEBOUNCE_MS = 250;

// -----------------------------
// Forward declarations
const char* modeToString(Mode m);
void sendStatus();
void transmitPending();
void motorStart(bool forward, uint32_t hz, Mode newMode);
void motorStop();
void processCommand(const char* cmd);
void receiveData();
void handleButtons();
void checkGotoTimeout();

// -----------------------------
bool isButtonPressed(int pin) { return !digitalRead(pin); }

const char* modeToString(Mode m) {
  switch (m) {
    case GOTOPOS: return "GOTOPOS";
    case SCAN:    return "SCAN";
    case FAST_S:  return "FAST_S";
    case FAST_O:  return "FAST_O";
    default:      return "STOP";
  }
}

// -----------------------------
// Motor control — pure LEDC, instant start/stop
void motorStart(bool forward, uint32_t hz, Mode newMode) {
  fwd = forward;
  digitalWrite(DIR_PIN, forward ? HIGH : LOW);
  digitalWrite(ENA_PIN, LOW);              // enable driver (active LOW)
  delayMicroseconds(100);                  // DIR + ENA setup time for TB6600
  ledcChangeFrequency(PUL_PIN, hz, STEP_LEDC_RES);
  ledcWrite(PUL_PIN, STEP_DUTY_50);
  mode = newMode;
  Serial.printf("Motor START %s %s @ %u Hz\n",
                modeToString(newMode), forward ? "FWD" : "BWD", hz);
}

void motorStop() {
  ledcWrite(PUL_PIN, STEP_DUTY_OFF);   // instant stop
  digitalWrite(ENA_PIN, HIGH);  // disable driver
  mode = STOPPED;
  Serial.println("Motor STOP");
}

// -----------------------------
// UART communication
void sendStatus() {
  txPending     = "STATUS:" + String(modeToString(mode)) + ":" +
                  String(fwd ? "FWD" : "BWD") + "\n";
  txPendingFlag = true;
}

void transmitPending() {
  if (!txPendingFlag) return;
  if (SerialMaster.availableForWrite() >= (int)txPending.length()) {
    SerialMaster.print(txPending);
    txPendingFlag = false;
    Serial.print("TX: ");
    Serial.print(txPending);
  }
}

// -----------------------------
void processCommand(const char* cmd) {
  Serial.print("RX: ");
  Serial.println(cmd);

  if (strcmp(cmd, "SCAN") == 0) {
    motorStart(fwd, SCAN_HZ, SCAN);
    sendStatus();
    return;
  }

  // STEPSCAN: slow constant-speed run; master sends STOP via encoder
  if (strcmp(cmd, "STEPSCAN") == 0) {
    motorStart(fwd, STEP_SCAN_HZ, SCAN);
    sendStatus();
    return;
  }

  if (strcmp(cmd, "STOP") == 0) {
    motorStop();
    sendStatus();
    return;
  }

  if (strcmp(cmd, "FAST_S") == 0) {
    motorStart(fwd, FAST_HZ, FAST_S);
    sendStatus();
    return;
  }

  if (strcmp(cmd, "FAST_O") == 0) {
    motorStart(!fwd, FAST_HZ, FAST_O);
    sendStatus();
    return;
  }

  if (strcmp(cmd, "STATUS?") == 0) {
    sendStatus();
    return;
  }

  // GOTOPOS:<cm>:<hz>:<F|B>
  // Slave runs at FAST_HZ; master stops via encoder when target reached.
  // Slave adds a 30s failsafe in case master goes silent.
  if (strncmp(cmd, "GOTOPOS:", 8) == 0) {
    const char* dirPtr = strrchr(cmd, ':');
    bool forward = fwd;
    if (dirPtr) {
      dirPtr++;
      forward = (*dirPtr == 'F' || *dirPtr == 'f');
    }
    motorStart(forward, FAST_HZ, GOTOPOS);
    gotoStartMs = millis();
    sendStatus();
    return;
  }

  // Unknown commands (e.g. legacy SYNCPOS, SYNCSTEP) are silently ignored
  Serial.print("Unknown command ignored: ");
  Serial.println(cmd);
}

// -----------------------------
void receiveData() {
  while (SerialMaster.available()) {
    char ch = SerialMaster.read();
    if (ch == '\r') continue;
    if (ch == '\n') {
      rxBuffer[rxLen] = '\0';
      if (rxLen > 0) processCommand(rxBuffer);
      rxLen = 0;
      continue;
    }
    if (rxLen < sizeof(rxBuffer) - 1) rxBuffer[rxLen++] = ch;
    else rxLen = 0;  // buffer overflow — reset
  }
}

// -----------------------------
// Failsafe: auto-stop GOTOPOS if master goes silent
void checkGotoTimeout() {
  if (mode != GOTOPOS) return;
  if (millis() - gotoStartMs > GOTOPOS_TIMEOUT_MS) {
    Serial.println("GOTOPOS timeout — stopping motor");
    motorStop();
    sendStatus();
  }
}

// -----------------------------
void handleButtons() {
  unsigned long now = millis();

  if (isButtonPressed(BTN_SCAN) && (now - lastDebounceScan) > DEBOUNCE_MS) {
    if (mode == SCAN) motorStop(); else motorStart(fwd, SCAN_HZ, SCAN);
    lastDebounceScan = now;
    sendStatus();
    Serial.println("BTN SCAN");
  }

  if (isButtonPressed(BTN_FAST_S) && (now - lastDebounceFastS) > DEBOUNCE_MS) {
    if (mode == FAST_S) motorStop(); else motorStart(fwd, FAST_HZ, FAST_S);
    lastDebounceFastS = now;
    sendStatus();
    Serial.println("BTN FAST_S");
  }

  if (isButtonPressed(BTN_FAST_O) && (now - lastDebounceFastO) > DEBOUNCE_MS) {
    if (mode == FAST_O) motorStop(); else motorStart(!fwd, FAST_HZ, FAST_O);
    lastDebounceFastO = now;
    sendStatus();
    Serial.println("BTN FAST_O");
  }
}

// -----------------------------
void setup() {
  Serial.begin(115200);
  SerialMaster.begin(115200, SERIAL_8N1, UART_RX, UART_TX);

  pinMode(BTN_SCAN,   INPUT_PULLUP);
  pinMode(BTN_FAST_S, INPUT_PULLUP);
  pinMode(BTN_FAST_O, INPUT_PULLUP);
  pinMode(DIR_PIN,    OUTPUT);
  pinMode(ENA_PIN,    OUTPUT);

  digitalWrite(DIR_PIN, HIGH);  // forward
  digitalWrite(ENA_PIN, HIGH);  // disabled at startup

  // Attach LEDC to PUL pin; start with duty=0 (no pulses)
  ledcAttach(PUL_PIN, SCAN_HZ, STEP_LEDC_RES);
  ledcWrite(PUL_PIN, STEP_DUTY_OFF);

  sendStatus();

  Serial.println("=== SLAVE MOTOR CONTROLLER (LEDC) ===");
  Serial.printf("Version: %s  Date: %s\n", FIRMWARE_VERSION, FIRMWARE_DATE);
  Serial.println("Commands: SCAN STEPSCAN STOP FAST_S FAST_O STATUS? GOTOPOS:cm:hz:dir");
  Serial.println("======================================");
}

// -----------------------------
void loop() {
  receiveData();
  transmitPending();
  checkGotoTimeout();
  handleButtons();
  delay(1);
}
