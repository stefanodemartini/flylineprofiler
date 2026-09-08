# Task: Scan di una linea esistente

Verificato leggendo il codice (`ViewModels/MainViewModel.cs`, `Services/BackendClient.cs`,
`Views/MainWindow.xaml(.cs)`, `Models/AppSettings.cs`) — non solo dal MANUALE.md, che su questo
punto è corretto ma incompleto su alcuni dettagli tecnici riportati qui sotto.

---

## 1. Obiettivo del task

Misurare fisicamente una lenza da mosca già esistente (una linea reale, in mano) facendola passare
nel dispositivo di scansione (ESP32 + sensore laser + motore), per ottenere il suo profilo di
diametro reale lungo la lunghezza — da confrontare con un design, o da esportare come dato grezzo.

**Importante da subito**: lo scan **non produce automaticamente un design**. Produce una serie di
punti (lunghezza cm → diametro mm) che restano un layer a sé — il "Scan" — separato dai nodi di
design che tu disegni a mano. Non esiste un pulsante "converti scan in design". Se vuoi un profilo
disegnabile/compensabile a partire da una linea scansionata, lo ricostruisci tu a occhio, tracciando
nodi sopra la curva scan visibile a schermo. Se questo non è quello che ti aspettavi, dimmelo — è il
primo punto su cui volevo la tua conferma.

---

## 2. Prerequisiti

- ESP32 con firmware di scan acceso e raggiungibile in rete (stessa LAN del PC)
- **Settings** (icona ingranaggio) configurate correttamente:
  - Host / IP (default `192.168.1.50`)
  - WebSocket Port (default `81`)
  - HTTP Port (default `80`, usata solo per `/export`, vedi §4.2)
  - AutoConnect: se attivo, l'app si connette da sola all'avvio
- **Design Mode OFF** (toggle in alto a destra) — con Design Mode ON i pannelli e i pulsanti di
  scan sono nascosti (`ScanButtonsPanel`/`ConnectionPanel` collassati)

---

## 3. Procedura operativa passo-passo

1. **Design Mode OFF** — appaiono il pannello di connessione e la toolbar motore/scan
2. **Connect** — avvia la connessione WebSocket a `ws://<host>:<port>/`
   - Il pallino di stato (`ConnLed`) passa da rosso a verde solo quando `ConnectionStatus`
     contiene "Connesso" o "OK"
   - Se **AutoConnect** è attivo nelle impostazioni, questo passo può già essere avvenuto da solo
     all'avvio dell'app
3. **Posizionare la linea** nel dispositivo secondo la procedura hardware (fuori dallo scope di
   questo documento software)
4. **(Opzionale) Calibrazione prima di scansionare**:
   - **Set Zero** → azzera il valore di display corrente (`setdisplayzero`)
   - **Reset Offset** → azzera l'offset di misura (`resetoffset`)
   - **Set Offset** → chiede un valore in mm e lo invia (`setoffset X.XXX`)
   - **Read Raw** → richiede una lettura grezza; **la risposta non appare nell'app**, solo sulla
     seriale del dispositivo (nota già presente nel codice, non un bug)
5. **SCAN** — invia in sequenza `motor scan` poi `scan_on`: il motore parte e la ricezione dei dati
   si attiva insieme
6. La linea attraversa il sensore; i punti misurati arrivano via WebSocket e **appaiono sul
   grafico in tempo reale**, punto per punto
7. **STOP** quando la scansione è completa (o se serve interrompere) — invia `motor stop` poi
   `scan_off`: ferma il motore e disattiva la ricezione
8. A questo punto i dati sono nella sessione (non ancora salvati su file — vedi §5.3)

### Controlli motore accessori (non necessari per uno scan base)
| Pulsante | Comando | Effetto | Abilitato quando |
|---|---|---|---|
| RX ON / RX OFF | `scan_on` / `scan_off` | Attiva/disattiva solo la ricezione dati (senza muovere il motore) | RX ON se non già ricevendo e non in GOTO; RX OFF solo se già ricevendo |
| FAST < / FAST > | `motor fast_o` / `motor fast_s` | Avanzamento rapido manuale, direzione opposta/stessa | Solo se non in GOTO |
| GOTO | `goto X.X` | Porta il motore a una posizione assoluta in cm | Solo se non già in GOTO |
| Status | `motor status` | Richiede lo stato motore al firmware | Sempre |

---

## 4. Cosa succede "dietro le quinte" a ogni passo

### 4.1 Alla connessione (`Connect`)
`MainViewModel.ConnectAsync()`:
1. Apre il WebSocket
2. Se `LoadParamsOnConnect` è attivo (impostazioni), invia `getparams` → il firmware risponde con
   `displayZero`/`offset` correnti, mostrati nei campi corrispondenti
3. Se `LoadMotorStatusOnConnect` è attivo, invia `motor status`

**Poi, sempre**, appena l'evento `Connected` scatta, `MainWindow` chiama
`_vm.LoadHistoryAsync()`: scarica `http://<host>:<port_http>/export` (HTTP, non WebSocket) e
**carica nel grafico tutto lo storico già presente sul dispositivo**, prima ancora di premere SCAN.
Quindi se il dispositivo ha già dati da uno scan precedente non ancora cancellato lato firmware, li
vedrai comparire subito alla connessione. Se questo non è il comportamento che ti aspetti (es. vuoi
sempre ripartire da zero), è un punto da discutere.

### 4.2 Durante lo scan (messaggi WebSocket in arrivo)
Ogni messaggio JSON ricevuto viene smistato in `MainViewModel.OnRawMessage()` per tipo:

| `type` | Contenuto | Effetto in UI |
|---|---|---|
| `params` | `displayZero`, `offset` | Aggiorna i due campi calibrazione |
| `speed` | `speed` (cm/s) | Aggiorna badge velocità: <0.5 "Troppo lenta", >2.5 "Troppo alta", altrimenti "Ottimale" |
| `motor` | `mode`, `dir` | Aggiorna stato motore mostrato |
| `scan_enabled` | `value` (bool) | Aggiorna `ScanReceiving` (governa quali pulsanti sono abilitati) |
| `goto_status` | `active`, `completed`, `target`, `current` | Aggiorna stato GOTOPOS |
| `goto_progress` | `current_cm`, `target_cm`, `remaining_cm` | Aggiorna avanzamento GOTOPOS in tempo reale |
| *(nessun `type`)* | `cm`, `diameter`, `rawDisplay`, `totalPoints` | **Questo è il punto di misura** — vedi sotto |

Un punto di misura senza campo `type` contiene tre valori distinti per la stessa posizione:
- `cm` → posizione lungo la linea (asse X)
- `diameter` → diametro calcolato dal firmware → salvato come `FilteredY`
- `rawDisplay` → valore grezzo del sensore, prima dell'elaborazione firmware → salvato come `RawY`

Se arriva un secondo punto sulla stessa posizione X (tolleranza 0.0001 cm), **sostituisce** quello
precedente invece di aggiungersi (evita duplicati se il motore torna sulla stessa posizione).

### 4.3 I tre livelli di "diametro" per ogni punto — da tenere a mente
Non è un solo numero, sono tre passaggi in cascata:
1. **`RawY`** (`rawDisplay`) — output grezzo del sensore, nessuna elaborazione. Visibile solo se
   attivi la serie "Raw" (arancione) nel grafico. Non viene mai filtrato lato client.
2. **`FilteredY`** (`diameter`) — valore già elaborato dal *firmware* per ogni singolo punto (non è
   uno storico, non c'entra con lo slider Alpha dell'app).
3. **Serie effettivamente disegnata a schermo / esportata / usata nel confronto** —
   `GetDisplayedSeries()` applica un **secondo filtro EMA lato client**, sopra `FilteredY`, usando
   `Smoothing EMA ON/OFF` e lo slider **Alpha** (default 0.10). Se lo smoothing è OFF, questa serie
   coincide con `FilteredY` grezzo (senza ulteriore lisciatura).

Quindi "Smoothing EMA" nell'app **non tocca il firmware**: è un filtro puramente visivo/di export
applicato ai valori che il firmware ha già mandato.

**Nota sullo storico caricato da `/export`**: il CSV storico ha solo due colonne (cm, valore) — non
distingue raw da filtered. Il loader (`LoadHistoryAsync`) assegna lo stesso valore sia a `RawY` sia
a `FilteredY`. Risultato pratico: per i punti caricati dallo storico, la serie "Raw" (se attivata)
coincide esattamente con quella filtrata finché non applichi lo smoothing EMA lato client — non è un
bug, ma può confondere se ti aspetti di vedere due curve diverse su dati storici.

---

## 5. Cosa produce (output)

### 5.1 A schermo, in tempo reale
- Curva scan che cresce punto per punto durante lo SCAN
- Contatore punti (`PointsCount`), lunghezza corrente, diametro corrente, valore display corrente
- Badge velocità di avanzamento (troppo lenta / ottimale / troppo alta)
- Se passi il mouse sul grafico e c'è anche un design disegnato: tooltip con `Design Ø`, `Scan Ø`
  (punto scan più vicino alla posizione del cursore) e **Δ%** tra i due — utile per verificare
  quanto una linea reale si discosta dal design nominale

### 5.2 Export CSV (`Export CSV`)
- Dialogo di salvataggio, default `flyline_scan_export.csv`
- Contenuto: intestazione `Lunghezza cm,Diametro mm`, poi una riga per punto **ordinato per X**,
  col valore preso da `GetDisplayedSeries()` — cioè **con lo smoothing EMA applicato se attivo**,
  non i valori grezzi. Se ti serve il dato grezzo per un confronto esterno, disattiva prima lo
  Smoothing EMA.

### 5.3 Salvataggio nel progetto (`.flp`)
I punti scan **non si salvano da soli** — fanno parte del progetto e vengono scritti solo quando
salvi il progetto (`Ctrl+S` / Save As), dentro il campo `ScanPoints`:
```json
"ScanPoints": [
  { "X": 0.0, "RawY": 3.42, "FilteredY": 3.40 },
  { "X": 1.0, "RawY": 3.41, "FilteredY": 3.39 },
  ...
]
```
Vengono salvati **entrambi** RawY e FilteredY grezzi (non la serie con smoothing EMA — quella è
calcolata al volo ogni volta che serve, mai persistita). Riaprendo il file, i punti tornano
esattamente come erano; lo smoothing visivo si riapplica in base alle impostazioni correnti
dell'app (non è legato al progetto).

### 5.4 Confronto con un altro progetto (`Overlay Project`)
Non è output diretto dello scan, ma lo riguarda: puoi caricare un **altro** file `.flp` come
overlay di confronto — scegliendo se importarne il design, lo scan, o entrambi. Questo aggiunge
curve statiche di paragone (`_importedSeries`), separate dallo scan "vivo" della sessione corrente.

---

## 6. Distinzioni operative da non confondere

| Pulsante | Comando al dispositivo | Cancella i punti scan? | Tocca il design? |
|---|---|---|---|
| **Clear Scan** | Nessuno (solo locale) | Sì, con conferma ("Discard the current scan...") | No, esplicitamente preservato |
| **Clear Chart** (in toolbar generale) | `reset` (posizione azzerata sul dispositivo) | Sì, **senza chiedere conferma** | No |
| **STOP** | `motor stop` + `scan_off` | No — ferma solo motore e ricezione | No |

`Clear Chart` invia un comando reale al motore (azzera la posizione) oltre a svuotare i dati locali,
e lo fa senza dialogo di conferma — a differenza di `Clear Scan` che è puramente locale ma chiede
conferma. Se questa asimmetria non è voluta, va segnalata: oggi è così nel codice
(`Reset_Click` vs `ClearScan_Click` in `MainWindow.xaml.cs`).

---

## 7. Punti confermati con Stefano (2026-09-08) — CHIUSO

1. **Nessuna conversione scan→design automatica** → **voluto**. Non è stato trovato un algoritmo
   affidabile per individuare da soli i segmenti (taper o cilindrici) dentro la curva di scan
   globale, quindi la ricostruzione resta manuale a occhio. Nessuna azione.

2. **Storico auto-caricato alla connessione** → approfondito e **corretto**. Il caricamento da
   `/export` è agganciato all'evento `Connected`, che scatta non solo all'avvio ma **anche a ogni
   riconnessione automatica dopo una caduta di rete**. `LoadHistoryAsync()` non svuotava mai
   `Points` né deduplicava per X prima di riaggiungere lo storico (il dedup esiste solo nel
   percorso dei punti live in arrivo, `AddLivePoint`). Risultato: una riconnessione a metà
   scansione (o un Connect manuale a sessione già popolata) riaggiungeva sopra tutto lo storico del
   dispositivo, duplicando/mescolando la curva.
   **Fix applicato**: `LoadHistoryAsync()` ora esce subito se `Points` non è vuota, con un
   messaggio nel log ("Storico da /export non ricaricato — punti già presenti in sessione").
   Il caricamento storico continua a funzionare al primo connect di una sessione pulita (avvio con
   AutoConnect, o Connect manuale a grafico vuoto); su ogni riconnessione o Connect successivo con
   dati già presenti, viene saltato invece di sovrapporsi.
   File: `ViewModels/MainViewModel.cs`. Verificato: `dotnet build` pulito.

3. **`Clear Chart` invia `reset` senza conferma** → **nessuna azione**. Non serve una richiesta di
   conferma per ogni azione potenzialmente dannosa; l'asimmetria con `Clear Scan` resta com'è.

4. **CSV export con smoothing EMA applicato** → confermato **OK così**, nessuna azione.
