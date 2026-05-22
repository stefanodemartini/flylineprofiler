#include <FastAccelStepper.h>

// ===============================
// FW SLAVE - Controllo Motore Passo-Passo
// ===============================
#define FIRMWARE_VERSION "0.4.9"
#define FIRMWARE_DATE "2026-05-22"
#define FIRMWARE_FEATURES "UART Control + GOTOPOS millis-based smooth ramp + forceStop at target"

// -----------------------------
// Pin definitions
#define PUL_PIN 4
#define DIR_PIN 5
#define ENA_PIN 6
#define BTN_SCAN 10
#define BTN_FAST_S 11
#define BTN_FAST_O 12
#define UART_RX 21
#define UART_TX 18

// -----------------------------
// UART
HardwareSerial SerialMaster(2);

// -----------------------------
// Motor parameters
const uint32_t SCAN_HZ = 1500;       // Initial estimate for 2.0 cm/s; closed-loop from master fine-tunes this
const uint32_t FAST_HZ = 12000;
const uint32_t GOTOPOS_MAX_HZ = (uint32_t)(FAST_HZ * 0.70f);  // 8400 Hz — 30% slower than FAST for GOTOPOS
const uint32_t ACCEL = 1500;         // Acceleration for SCAN and normal moves
const uint32_t GOTOPOS_ACCEL = 6000; // Higher accel so motor tracks the speed ramp in GOTOPOS
const uint32_t FAST_STOP_DECEL = 24000;
const uint32_t MIN_SPEED_HZ = 300;   // Floor speed at the very end of approach (~0.5 cm/s)
const uint32_t STEPS_PER_ENC = 25;   // Stepper steps per encoder pulse (1500 Hz ≈ 60 enc/s)
const uint32_t SLOW_ZONE_ENC = 1000; // Linear ramp starts this many enc before target (~33 cm)

// -----------------------------
// Conversion constants (deve corrispondere al master)
const float PULSES_PER_CM = 30.0;

// -----------------------------
// State variables
enum Mode { STOPPED, SCAN, FAST_S, FAST_O, GOTOPOS };
Mode mode = STOPPED;
bool fwd = true;
int32_t targetPos = 0;
bool posActive = false;
uint32_t currentDynamicSpeed = 0;

// Encoder-fed position: updated by POS:<steps> messages from master.
// Used instead of stepper->getCurrentPosition() during GOTOPOS so that
// the wheel encoder is the sole authoritative position source.
int32_t encoderFedPos = 0;
bool encoderPosReceived = false;

// Millis-based distance estimate for smooth speed ramp.
// Updated every 25 ms — far smoother than POS updates (every 100 ms).
// On each POS update, gotoDistAtSync and gotoSyncMs are refreshed so the
// estimate stays accurate. Between POS updates it extrapolates linearly.
int32_t  gotoDistAtSync = 0;    // encoder distance at last sync point
unsigned long gotoSyncMs  = 0;  // millis() at last sync
unsigned long lastRampMs  = 0;  // millis() of last setSpeedInHz call

// -----------------------------
// FastAccelStepper objects
FastAccelStepperEngine engine;
FastAccelStepper* stepper = nullptr;

// -----------------------------
// UART buffers
char rxBuffer[64];
size_t rxLen = 0;
String txPending;
bool txPendingFlag = false;

// -----------------------------
// Button debounce
unsigned long lastDebounceScan = 0;
unsigned long lastDebounceFastS = 0;
unsigned long lastDebounceFastO = 0;
const unsigned long DEBOUNCE_MS = 250;

// -----------------------------
// Position monitoring
unsigned long lastPosCheck = 0;
int32_t lastPos = 0;
unsigned long lastMoveTime = 0;

// -----------------------------
// Forward declarations
const char* modeToString(Mode m);
void sendStatus();
void transmitPending();
void startMotion(bool forward, uint32_t speedHz, Mode newMode);
void stopMotion();
int32_t getDistanceToGo();
uint32_t calculateDynamicSpeed(int32_t distanceToGo);
void updateDynamicSpeed();
void processCommand(const char* command);
void receiveData();
void checkPositionReached();
void handleButtons();

// -----------------------------
// Helper functions
bool isButtonPressed(int pin) {
  return !digitalRead(pin);
}

const char* modeToString(Mode m) {
  switch (m) {
    case GOTOPOS: return "GOTOPOS";
    case SCAN: return "SCAN";
    case FAST_S: return "FAST_S";
    case FAST_O: return "FAST_O";
    default: return "STOP";
  }
}

// -----------------------------
// Motor control functions
void startMotion(bool forward, uint32_t speedHz, Mode newMode) {
  fwd = forward;
  stepper->setSpeedInHz(speedHz);
  stepper->setAcceleration(ACCEL);
  
  if (forward) {
    stepper->runForward();
  } else {
    stepper->runBackward();
  }
  
  mode = newMode;
  currentDynamicSpeed = speedHz;
  lastMoveTime = millis();
  
  if (newMode == GOTOPOS) {
    posActive = true;
  }
  
  Serial.print("Avvio movimento: ");
  Serial.print(modeToString(newMode));
  Serial.print(" - ");
  Serial.println(forward ? "AVANTI" : "INDIETRO");
}

void stopMotion() {
  if (stepper) {
    if (mode == FAST_S || mode == FAST_O) {
      stepper->setAcceleration(FAST_STOP_DECEL);
      stepper->stopMove();
    } else if (mode == GOTOPOS) {
      // forceStop() cuts power immediately — no deceleration ramp.
      // CRITICAL: stopMove() followed by setAcceleration(ACCEL) would override any
      // FAST_STOP_DECEL set during the slow-zone transition, reverting to 1500 Hz/s
      // and causing 64 cm of coast. forceStop() avoids this entirely.
      // Step loss is acceptable: encoder (not step counter) is the position reference.
      stepper->forceStop();
    } else {
      stepper->stopMove();
    }
    stepper->setAcceleration(ACCEL);  // safe: motor is stopped (forceStop) or decelerating (stopMove)
  }
  mode = STOPPED;
  posActive = false;
  currentDynamicSpeed = 0;
  encoderPosReceived = false;
  gotoDistAtSync = -1;  // mark ramp as inactive
  Serial.println("Motore STOP");
}

int32_t getDistanceToGo() {
  if (!encoderPosReceived) return INT32_MAX;  // position unknown, stay at full speed
  return abs(targetPos - encoderFedPos);
}

uint32_t calculateDynamicSpeed(int32_t distEnc) {
  if (distEnc <= 0) return MIN_SPEED_HZ;
  if ((uint32_t)distEnc >= SLOW_ZONE_ENC) return GOTOPOS_MAX_HZ;
  // Linear ramp: GOTOPOS_MAX_HZ at zone edge → MIN_SPEED_HZ at target
  float ratio = (float)distEnc / (float)SLOW_ZONE_ENC;
  float v = (float)MIN_SPEED_HZ + (float)(GOTOPOS_MAX_HZ - MIN_SPEED_HZ) * ratio;
  return (uint32_t)constrain(v, (float)MIN_SPEED_HZ, (float)GOTOPOS_MAX_HZ);
}

void updateDynamicSpeed() {
  if (mode != GOTOPOS || !posActive || gotoDistAtSync < 0) return;

  unsigned long now = millis();
  if (now - lastRampMs < 25) return;  // update at 40 Hz — smooth but not spamming stepper
  lastRampMs = now;

  // Estimate remaining distance by extrapolating from the last encoder sync point.
  // currentDynamicSpeed is the commanded speed; enc/s = Hz / STEPS_PER_ENC.
  unsigned long elapsed = now - gotoSyncMs;
  int32_t traveled = (int32_t)((uint32_t)(currentDynamicSpeed / STEPS_PER_ENC) * elapsed / 1000UL);
  int32_t distEnc = max((int32_t)0, gotoDistAtSync - traveled);

  uint32_t newSpeed = calculateDynamicSpeed(distEnc);

  if (newSpeed != currentDynamicSpeed) {
    if (currentDynamicSpeed == GOTOPOS_MAX_HZ && newSpeed < GOTOPOS_MAX_HZ) {
      stepper->setAcceleration(GOTOPOS_ACCEL);
    }
    currentDynamicSpeed = newSpeed;
    stepper->setSpeedInHz(currentDynamicSpeed);
  }
}

void checkPositionReached() {
  if (mode != GOTOPOS || !posActive || !encoderPosReceived) return;

  int32_t signedDist = targetPos - encoderFedPos;
  bool overshot = fwd ? (signedDist < 0) : (signedDist > 0);
  int32_t distanceToGo = abs(signedDist);

  if (overshot || distanceToGo <= 5) {
    if (overshot) Serial.println("⚠ Overshoot rilevato — stop forzato");
    // forceStop() cuts power immediately — no deceleration ramp.
    // Safe here because we are already at MIN_SPEED_HZ (300 Hz ≈ 0.5 cm/s).
    stepper->forceStop();
    stepper->setAcceleration(ACCEL);  // restore normal accel (FAST_STOP_DECEL was set during slow-zone transition)
    mode = STOPPED;
    posActive = false;
    currentDynamicSpeed = 0;
    encoderPosReceived = false;
    sendStatus();
    Serial.println("✓ Target raggiunto!");
  }
}

// -----------------------------
// UART communication
void sendStatus() {
  txPending = "STATUS:" + String(modeToString(mode)) + ":" + String(fwd ? "FWD" : "BWD");
  if (mode == GOTOPOS && posActive && encoderPosReceived) {
    int32_t distanceToGo = getDistanceToGo();
    txPending += ":" + String(distanceToGo);
  }
  txPending += "\n";
  txPendingFlag = true;
}

void transmitPending() {
  if (!txPendingFlag) return;
  if (SerialMaster.availableForWrite() >= (int)txPending.length()) {
    SerialMaster.print(txPending);
    txPendingFlag = false;
    Serial.print("Inviato: ");
    Serial.print(txPending);
  }
}

void processCommand(const char* command) {
  Serial.print("Ricevuto comando: ");
  Serial.println(command);
  
  // Comandi esistenti
  if (strcmp(command, "SCAN") == 0) {
    startMotion(fwd, SCAN_HZ, SCAN);
    sendStatus();
    return;
  }
  
  if (strcmp(command, "STOP") == 0) {
    stopMotion();
    sendStatus();
    return;
  }
  
  if (strcmp(command, "FAST_S") == 0) {
    startMotion(fwd, FAST_HZ, FAST_S);
    sendStatus();
    return;
  }
  
  if (strcmp(command, "FAST_O") == 0) {
    startMotion(!fwd, FAST_HZ, FAST_O);
    sendStatus();
    return;
  }
  
  if (strcmp(command, "STATUS?") == 0) {
    sendStatus();
    return;
  }

  // POS:<steps> — encoder position fed by master every ~100ms during GOTOPOS.
  // This is the sole authoritative position source; step counter is not used.
  if (strncmp(command, "POS:", 4) == 0) {
    encoderFedPos = (int32_t)atol(command + 4);
    encoderPosReceived = true;
    // Resync the millis-based ramp estimate to the fresh encoder reading.
    // This keeps the extrapolation accurate between POS updates.
    if (mode == GOTOPOS && posActive) {
      gotoDistAtSync = abs(targetPos - encoderFedPos);
      gotoSyncMs = millis();
      checkPositionReached();
    }
    return;
  }

  // Comando GOTOPOS:<target_cm>:<velocita_max>:<encoder_pos>:<F|B>
  // Esempio: GOTOPOS:150:12000:4560:F
  // encoder_pos = current encoder value sent by master so slave knows distance from step 0
  if (strncmp(command, "GOTOPOS:", 8) == 0) {
    char* ptr = (char*)command + 8;
    
    float targetCm = atof(ptr);
    // Convert cm to encoder steps (same constant as master)
    targetPos = (int32_t)(targetCm * PULSES_PER_CM);
    
    // Parse initial encoder position (3rd field after targetCm and maxHz)
    // Format: targetCm:maxHz:encoderPos:dir
    char* p1 = strchr(ptr, ':');                       // → ":maxHz:encoderPos:dir"
    char* p2 = p1 ? strchr(p1 + 1, ':') : nullptr;    // → ":encoderPos:dir"
    if (p2) {
      encoderFedPos = (int32_t)atol(p2 + 1);  // atol stops at ':', safe
      encoderPosReceived = true;
    } else {
      encoderPosReceived = false;  // fallback: wait for first POS update
    }
    
    // Direction from last field (strrchr finds last ':' → ":F" or ":B")
    char* dirPtr = strrchr(ptr, ':');
    if (dirPtr) {
      dirPtr++;
      fwd = (*dirPtr == 'F' || *dirPtr == 'f');
    } else {
      fwd = true;
    }
    
    int32_t initialDist = getDistanceToGo();

    Serial.print("GOTOPOS: target=");
    Serial.print(targetCm);
    Serial.print(" cm (");
    Serial.print(targetPos);
    Serial.print(" enc), encoder iniziale=");
    Serial.print(encoderFedPos);
    Serial.print(", dist=");
    Serial.print(initialDist);
    Serial.print(", direzione=");
    Serial.println(fwd ? "AVANTI" : "INDIETRO");
    
    mode = GOTOPOS;
    posActive = true;

    // Initialise millis-based ramp sync point
    gotoDistAtSync = initialDist;
    gotoSyncMs     = millis();
    lastRampMs     = 0;  // force immediate first update

    currentDynamicSpeed = calculateDynamicSpeed(initialDist);
    stepper->setSpeedInHz(currentDynamicSpeed);
    stepper->setAcceleration(GOTOPOS_ACCEL);
    if (fwd) stepper->runForward(); else stepper->runBackward();
    
    sendStatus();
    return;
  }
  
  // SETHZ:<hz> — dynamic scan speed adjustment sent by master closed-loop controller.
  // Only applied during SCAN; ignored in other modes.
  if (strncmp(command, "SETHZ:", 6) == 0) {
    uint32_t newHz = (uint32_t)atol(command + 6);
    if (mode == SCAN && newHz >= 200 && newHz <= 3000) {
      stepper->setSpeedInHz(newHz);
      currentDynamicSpeed = newHz;
      Serial.print("SETHZ: ");
      Serial.println(newHz);
    }
    return;
  }

  // Comando non riconosciuto
  Serial.print("Comando sconosciuto: ");
  Serial.println(command);
  sendStatus();
}

void receiveData() {
  while (SerialMaster.available()) {
    char ch = SerialMaster.read();
    if (ch == '\r') continue;
    
    if (ch == '\n') {
      rxBuffer[rxLen] = '\0';
      if (rxLen > 0) {
        processCommand(rxBuffer);
      }
      rxLen = 0;
      continue;
    }
    
    if (rxLen < sizeof(rxBuffer) - 1) {
      rxBuffer[rxLen++] = ch;
    } else {
      // Buffer overflow, reset
      rxLen = 0;
    }
  }
}

// -----------------------------
// Button handling
void handleButtons() {
  if (mode == GOTOPOS) return;  // FW-05: ignore buttons during GOTOPOS to prevent isGoToActive lock
  unsigned long now = millis();
  
  // Pulsante SCAN
  if (isButtonPressed(BTN_SCAN) && (now - lastDebounceScan) > DEBOUNCE_MS) {
    if (mode == SCAN) {
      stopMotion();
    } else {
      startMotion(fwd, SCAN_HZ, SCAN);
    }
    lastDebounceScan = now;
    sendStatus();
    Serial.println("Pulsante SCAN premuto");
  }
  
  // Pulsante FAST_S (stessa direzione)
  if (isButtonPressed(BTN_FAST_S) && (now - lastDebounceFastS) > DEBOUNCE_MS) {
    if (mode == FAST_S) {
      stopMotion();
    } else {
      startMotion(fwd, FAST_HZ, FAST_S);
    }
    lastDebounceFastS = now;
    sendStatus();
    Serial.println("Pulsante FAST_S premuto");
  }
  
  // Pulsante FAST_O (direzione opposta)
  if (isButtonPressed(BTN_FAST_O) && (now - lastDebounceFastO) > DEBOUNCE_MS) {
    if (mode == FAST_O) {
      stopMotion();
    } else {
      startMotion(!fwd, FAST_HZ, FAST_O);
    }
    lastDebounceFastO = now;
    sendStatus();
    Serial.println("Pulsante FAST_O premuto");
  }
}

// -----------------------------
// Setup
void setup() {
  // Serial console per debug
  Serial.begin(115200);
  
  // UART per comunicazione con master
  SerialMaster.begin(115200, SERIAL_8N1, UART_RX, UART_TX);
  
  // Configura pulsanti
  pinMode(BTN_SCAN, INPUT_PULLUP);
  pinMode(BTN_FAST_S, INPUT_PULLUP);
  pinMode(BTN_FAST_O, INPUT_PULLUP);
  
  // Inizializza stepper
  engine.init();
  stepper = engine.stepperConnectToPin(PUL_PIN);
  
  if (!stepper) {
    Serial.println("ERRORE: Impossibile inizializzare lo stepper!");
    while (1) {
      delay(1000);
    }
  }
  
  stepper->setDirectionPin(DIR_PIN);
  stepper->setEnablePin(ENA_PIN);
  stepper->setAutoEnable(true);
  
  // Imposta posizione iniziale a 0
  stepper->setCurrentPosition(0);
  
  // Ferma motore all'avvio
  stopMotion();
  
  // Invia stato iniziale
  sendStatus();
  
  // Stampa informazioni di avvio
  Serial.println("========================================");
  Serial.println("=== SLAVE CONTROLLO MOTORE ===");
  Serial.print("Versione: ");
  Serial.println(FIRMWARE_VERSION);
  Serial.print("Data: ");
  Serial.println(FIRMWARE_DATE);
  Serial.print("Caratteristiche: ");
  Serial.println(FIRMWARE_FEATURES);
  Serial.println("========================================");
  Serial.println("Comandi disponibili:");
  Serial.println("  SCAN           - Avvia scansione (velocità lenta)");
  Serial.println("  STOP           - Ferma motore");
  Serial.println("  FAST_S         - Avanti veloce (stessa direzione)");
  Serial.println("  FAST_O         - Indietro veloce (direzione opposta)");
  Serial.println("  STATUS?        - Richiedi stato");
  Serial.println("  GOTOPOS:cm:max_hz:dir - Vai a posizione");
  Serial.println("    Esempio: GOTOPOS:150:12000:F");
  Serial.println("========================================");
}

// -----------------------------
// Main loop
void loop() {
  // Gestione comunicazione UART
  receiveData();
  transmitPending();
  
  // Gestione movimento GOTOPOS
  if (mode == GOTOPOS && posActive) {
    updateDynamicSpeed();
    checkPositionReached();
    
    // Monitoraggio posizione per debug (ogni 500ms)
    unsigned long now = millis();
    if (now - lastPosCheck > 500) {
      lastPosCheck = now;
      if (encoderPosReceived && encoderFedPos != lastPos) {
        lastPos = encoderFedPos;
        int32_t remaining = getDistanceToGo();
        Serial.print("Encoder pos: ");
        Serial.print(encoderFedPos);
        Serial.print(" passi, rimanenti: ");
        Serial.println(remaining);
      }
    }
  }
  
  // Gestione pulsanti fisici
  handleButtons();
  
  // Piccolo delay per non sovraccaricare la CPU
  delay(1);
}