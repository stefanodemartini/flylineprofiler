#include <FastAccelStepper.h>

// ===============================
// FW SLAVE - Controllo Motore Passo-Passo
// ===============================
#define FIRMWARE_VERSION "0.3.0"
#define FIRMWARE_DATE "2026-03-27"
#define FIRMWARE_FEATURES "UART Control + GOTOPOS + Dynamic Speed + Buttons"

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
const uint32_t SCAN_HZ = 1500;
const uint32_t FAST_HZ = 12000;
const uint32_t ACCEL = 1500;
const uint32_t MIN_SPEED_HZ = 300;      // Velocità minima in prossimità del target
const uint32_t DISTANCE_THRESHOLD = 500; // Passi a cui iniziare a ridurre velocità

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
    stepper->stopMove();
  }
  mode = STOPPED;
  posActive = false;
  currentDynamicSpeed = 0;
  Serial.println("Motore STOP");
}

int32_t getDistanceToGo() {
  if (!stepper) return 0;
  int32_t currentPos = stepper->getCurrentPosition();
  return abs(targetPos - currentPos);
}

uint32_t calculateDynamicSpeed(int32_t distanceToGo) {
  uint32_t absDist = abs(distanceToGo);
  
  if (absDist <= DISTANCE_THRESHOLD) {
    // Vicino al target: velocità proporzionale alla distanza
    // Mappa [0..DISTANCE_THRESHOLD] -> [MIN_SPEED_HZ..FAST_HZ]
    float ratio = (float)absDist / DISTANCE_THRESHOLD;
    // Usa una curva quadratica per una decelerazione più morbida
    ratio = ratio * ratio;
    uint32_t speed = MIN_SPEED_HZ + (uint32_t)((FAST_HZ - MIN_SPEED_HZ) * ratio);
    return constrain(speed, MIN_SPEED_HZ, FAST_HZ);
  } else {
    // Lontano dal target: velocità massima
    return FAST_HZ;
  }
}

void updateDynamicSpeed() {
  if (mode != GOTOPOS || !posActive) return;
  
  int32_t distanceToGo = getDistanceToGo();
  uint32_t newSpeed = calculateDynamicSpeed(distanceToGo);
  
  // Cambia velocità solo se necessario (evita aggiornamenti continui)
  if (newSpeed != currentDynamicSpeed && distanceToGo > 0) {
    currentDynamicSpeed = newSpeed;
    stepper->setSpeedInHz(currentDynamicSpeed);
    // FW-02: re-issue moveTo with updated speed instead of runForward/runBackward
    // which would cancel the position target and cause unbounded overshoot.
    stepper->moveTo(targetPos);
    
    Serial.print("Velocità aggiornata: ");
    Serial.print(currentDynamicSpeed);
    Serial.print(" Hz - Distanza rimanente: ");
    Serial.println(distanceToGo);
  }
}

void checkPositionReached() {
  if (mode != GOTOPOS || !posActive) return;
  
  int32_t distanceToGo = getDistanceToGo();
  
  // Se la distanza è 0 o molto piccola, considera raggiunto
  if (distanceToGo <= 5) {  // Margine di 5 passi per evitare overshoot
    // FW-03: set targetPos = currentPos so sendStatus() reports remaining = 0,
    // then send BEFORE stopMotion() changes mode to STOPPED.
    // Master checks remaining == 0 to trigger goto_status:completed:true.
    targetPos = stepper->getCurrentPosition();
    sendStatus();  // sends STATUS:GOTOPOS:DIR:0
    stopMotion();
    Serial.println("✓ Target raggiunto!");
  }
}

// -----------------------------
// UART communication
void sendStatus() {
  txPending = "STATUS:" + String(modeToString(mode)) + ":" + String(fwd ? "FWD" : "BWD");
  if (mode == GOTOPOS && posActive) {
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
  
  // Comando GOTOPOS: GOTOPOS:target_cm:velocita_max:direction
  // Esempio: GOTOPOS:150:12000:F
  if (strncmp(command, "GOTOPOS:", 8) == 0) {
    char* ptr = (char*)command + 8;
    
    // Estrai target cm
    float targetCm = atof(ptr);
    
    // Converti cm in passi (usa stessa costante del master)
    targetPos = (int32_t)(targetCm * PULSES_PER_CM);
    
    // Cerca il prossimo separatore per la velocità
    char* speedPtr = strchr(ptr, ':');
    if (speedPtr) {
      speedPtr++;
      // Velocità max (opzionale, può essere ignorata)
      uint32_t maxSpeed = atoi(speedPtr);
      if (maxSpeed > 0 && maxSpeed <= FAST_HZ) {
        // Si potrebbe usare maxSpeed, ma manteniamo FAST_HZ per semplicità
      }
    }
    
    // Determina direzione
    char* dirPtr = strrchr(ptr, ':');
    if (dirPtr) {
      dirPtr++;
      fwd = (*dirPtr == 'F' || *dirPtr == 'f');
    } else {
      // Determina automaticamente la direzione in base alla posizione attuale
      int32_t currentPos = stepper->getCurrentPosition();
      fwd = (targetPos > currentPos);
    }
    
    // Calcola distanza da percorrere
    int32_t currentPos = stepper->getCurrentPosition();
    int32_t distance = abs(targetPos - currentPos);
    
    if (distance == 0) {
      // Già a target
      Serial.println("Già alla posizione target");
      stopMotion();
      sendStatus();
      return;
    }
    
    Serial.print("GOTOPOS: target=");
    Serial.print(targetCm);
    Serial.print(" cm (");
    Serial.print(targetPos);
    Serial.print(" passi), distanza=");
    Serial.print(distance);
    Serial.print(" passi, direzione=");
    Serial.println(fwd ? "AVANTI" : "INDIETRO");
    
    // Avvia movimento verso target
    mode = GOTOPOS;
    posActive = true;
    
    // Imposta velocità iniziale
    currentDynamicSpeed = FAST_HZ;
    stepper->setSpeedInHz(currentDynamicSpeed);
    stepper->setAcceleration(ACCEL);
    
    // FW-06: both branches were identical (dead code). moveTo() handles direction internally.
    stepper->moveTo(targetPos);
    
    sendStatus();
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
      int32_t currentPos = stepper->getCurrentPosition();
      if (currentPos != lastPos) {
        lastPos = currentPos;
        int32_t remaining = abs(targetPos - currentPos);
        Serial.print("Posizione: ");
        Serial.print(currentPos);
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