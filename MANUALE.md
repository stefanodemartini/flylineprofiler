# FlyLine Profiler — Manuale Utente

**Versione**: corrente (giugno 2026)
**Piattaforma**: Windows 10/11 (x64), .NET 8

---

## Avvio

Aprire `FlyLineProfiler.exe` oppure fare doppio-clic su un file `.flp` per aprire direttamente un progetto.

La finestra si apre in **Modalità Design** (toggle "Design Mode" attivo in alto a destra).

---

## Interfaccia Principale

```
┌─────────────────────────────────────────────────────────────────┐
│ FlyLine Profiler — [NomeProgetto]                    [Design Mode]│
├─────────┬───────────────────────────────────────────────────────┤
│         │                                                         │
│ Pannello│              GRAFICO PRINCIPALE (ScottPlot)            │
│ Sinistra│                                                         │
│         │                                                         │
├─────────┴───────────────────────────────────────────────────────┤
│              GRAFICO ANALISI MASSA (solo Design)                 │
├─────────────────────────────────────────────────────────────────┤
│ Status Bar: info contestuali                        AFFTA badge │
└─────────────────────────────────────────────────────────────────┘
```

### Pannello sinistro (Design Mode)
- **Project**: gestione file, nomi, info
- **Design Tools**: disegno nodi, reverse, undo, clear
- **Nodes DataGrid**: tabella editabile dei nodi
- **Nozzles**: 4 ugelli (M1–M4) con colore e densità
- **Nozzle Zones**: assegnazione zone colore
- **Segments DataGrid**: tabella segmenti calcolata automaticamente
- **Sinking Tools**: fisica acqua, compensazione

---

## 1. Gestione Progetti

### Creare un nuovo progetto
1. Menu **File → New Project**
2. Inserire un nome nel dialogo
3. Il progetto parte vuoto

### Aprire un progetto
- **File → Open** → selezionare file `.flp`
- **File → Recent** → lista file recenti con popup
- **Doppio-clic** su un file `.flp` da Esplora File

### Salvare
- **File → Save** (`Ctrl+S`): salva sul file corrente
- **File → Save As**: sceglie un nuovo percorso; adotta il nome del file come nome del progetto
- Al salvataggio con serie importate attive, viene chiesto se includerle nel file

### Chiudere
- L'app avvisa se ci sono modifiche non salvate (Yes = salva, No = scarta, Cancel = annulla)

---

## 2. Modalità SCAN (acquisizione hardware)

Attivare **Design Mode OFF** con il toggle in alto a destra. Appaiono i pannelli di connessione e i controlli del motore.

### Connessione all'ESP32
1. Aprire **Settings** (ingranaggio) e verificare IP e porte:
   - Host: `192.168.1.50` (default)
   - WebSocket Port: `81`
   - HTTP Port: `80`
2. Premere **Connect** — il LED status diventa verde
3. Se **AutoConnect** è attivo nelle impostazioni, la connessione parte automaticamente all'avvio

### Avviare uno Scan
1. Premere **SCAN** — invia `motor scan` + `scan_on`
2. Il motore trasporta la linea attraverso il sensore laser
3. I punti appaiono in tempo reale sul grafico
4. Premere **STOP** per fermare il motore e disabilitare la ricezione

### Controlli Motore

| Pulsante | Comando inviato | Effetto |
|---|---|---|
| SCAN | `motor scan` + `scan_on` | Avvia scan completo |
| STOP | `motor stop` + `scan_off` | Ferma tutto |
| Fast → | `motor fast_s` | Avanzamento rapido stessa direzione |
| Fast ← | `motor fast_o` | Avanzamento rapido direzione opposta |
| GOTO | `goto X.X` | Va alla posizione X cm |
| Motor Status | `motor status` | Legge stato motore dal firmware |

### Calibrazione
- **Set Display Zero**: azzera il valore display attuale
- **Reset Offset**: azzera l'offset di misura
- **Set Offset**: imposta un offset manuale in mm
- **Read Raw**: legge il valore grezzo del sensore (output sulla seriale del dispositivo)

### Smoothing

- **EMA ON/OFF**: abilita/disabilita il filtro Exponential Moving Average
- **Alpha slider**: `0.00` = nessun filtro (raw), `1.00` = massimo livellamento; default `0.10`
- **Raw series**: mostra/nasconde la curva non filtrata in arancione

### Esportare i Dati Scan
- **Export CSV**: esporta i punti (cm, mm) in formato CSV
- **Export PNG**: salva il grafico come immagine

### Clear Scan
Cancella solo i dati di scan acquisiti (non il design). Utile per rieseguire uno scan.

---

## 3. Modalità DESIGN

Attivare **Design Mode ON**. Il pannello sinistro mostra i controlli di design; il pannello scan è nascosto.

### Disegnare un Profilo

#### Attivare la modalità di disegno
- Menu **Design → Draw Segments** oppure checkbox nella toolbar
- Il cursore diventa una croce `+`

#### Aggiungere nodi
- **Click sinistro** sul grafico → aggiunge un nodo
  - X = cm (arrotondato all'intero più vicino)
  - Y = diametro in mm (dalla posizione verticale del cursore)
- **Shift + Click**: segmento cilindrico — blocca il diametro al nodo esistente più vicino

#### Rimuovere nodi
- **Click destro** vicino a un nodo (in Draw mode) → rimuove il nodo più vicino

#### Spostare nodi (Drag)
- **Click e trascina** su un nodo esistente (anche senza Draw mode attivo)
- Il profilo si aggiorna in tempo reale; il nodo si fissa al rilascio

#### Editare un nodo con precisione
- **Doppio clic** sul nodo → dialogo "Edit Node"
- Inserire posizione esatta in cm e diametro in mm
- OK per confermare

#### Tabella Nodi
- La **Nodes DataGrid** mostra tutti i nodi in ordine
- Cliccare su una cella per editare posizione o diametro direttamente
- **Add Node** (pulsante "+") → aggiunge un nodo in fondo alla lista con valori di default
- **Delete** su una riga selezionata → rimuove il nodo

#### Undo
- **Ctrl+Z** — annulla l'ultima modifica ai nodi (max 100 step)
- Funziona per: aggiunta, rimozione, drag, edit, reverse, clear

#### Invertire il Profilo
- **Reverse** — specchia le posizioni X mantenendo i diametri
- Utile per impostare l'orientamento corretto (tip=0, reel=max)

#### Cancellare il Profilo
- **Clear Segments** — rimuove tutti i nodi (undoable)

---

## 4. Segmenti e Geometria

### Tabella Segmenti
Con almeno 2 nodi la tabella **Segments** si popola automaticamente:

| Colonna | Descrizione |
|---|---|
| # | Indice segmento (S1, S2…) |
| Name | Nome editabile |
| Start cm / End cm | Posizioni dei nodi |
| Start Ø / End Ø | Diametri in mm (editabili) |
| Length | Lunghezza in cm (editabile — sposta il nodo finale) |
| Shape | Cylinder / Taper (rilevato automaticamente) |
| Taper mm/m | Variazione diametro per metro |
| Sp.W. g/cm³ | Densità specifica |
| Sink m/s | Velocità di affondamento calcolata |

### Totali
Sotto la tabella appare la riga **Totals**:
- Volume totale (cm³) e massa totale (g) se la densità è impostata
- Volume/Massa della testa (se marcata con flag Head)
- **CoM** % e **Rg** % — Centro di Massa e Raggio di Girabilità
- Classificazione del taper (hover sul testo per la descrizione completa)

---

## 5. Ugelli e Colori (Nozzle System)

### Definizione degli Ugelli (M1–M4)
- **M1**: materiale di base — definisce il colore del profilo di design e la sua densità è sempre quella impostata in "Material ρ" (sezione 6). La cella densità di M1 nella griglia è di sola lettura: si modifica solo da "Material ρ" / From Weight / From Sink Speed, per evitare disallineamenti
- Ogni ugello ha: **colore** + **densità** (g/cm³) + **etichetta**
- **La griglia mostra solo gli ugelli realmente in uso**: con un solo materiale si vede solo M1; M2–M4 compaiono automaticamente solo quando servono davvero (densità propria, zona colore assegnata, o attivati manualmente)
- Pulsante **＋ Material** → attiva deliberatamente il prossimo ugello nascosto, per poterlo configurare prima di usarlo in una zona
- Il badge "x/4" indica quanti ugelli sono attivi

### Cambiare Colore
1. Cliccare il swatch colorato accanto a M1/M2/M3/M4
2. Popup con:
   - Palette di colori rapidi (swatches predefiniti)
   - Campo hex manuale (6 caratteri, Enter per applicare)
   - Pulsante "Pick Color" per il dialogo Windows completo

### Zone Ugello
- **Add Zone** → aggiunge una zona al profilo (start cm, end cm, ugello assegnato)
- La zona viene colorata con Lambert cylindrical shading (aspetto 3D solido)
- **Delete** su riga selezionata → elimina la zona

### Zona con Densità Propria (materiale diverso da M1)
Se la zona appena aggiunta (o un ugello già assegnato a una zona) ha una densità che differisce da quella di M1 di più di 0.02 g/cm³, l'app considera la zona un materiale realmente diverso — non solo un colore — e chiede:

> *"Adjust the diameters in those zones to preserve their current mass?"*
> **Yes** (default) — i diametri della zona vengono ricalcolati per mantenere la massa attuale (formula a massa costante ρ·d² = cost.)
> **No** — i diametri restano quelli disegnati; il peso della zona cambia di conseguenza

Dopo la conferma:
- La tabella segmenti mostra la velocità di affondamento reale di ogni segmento (non un target unico — ogni zona affonda alla propria velocità fisica)
- In **Show C** il profilo mostra i colori reali scelti per ciascun ugello/zona (non il gradiente automatico usato dalla compensazione fisica)
- Vedi sezione 7 per come questa variante viene salvata

---

## 6. Materiale e Fisica dell'Acqua

### Impostare la Densità

**Metodo 1 — Dalla pesatura:**
1. Pulsante **From Weight** → dialog
2. Scegliere scope: Intera linea / Solo testa / Segmenti personalizzati
3. Inserire il peso misurato in grammi (accetta `.` o `,` come decimale)
4. Densità calcolata mostrata in anteprima
5. OK per applicare

**Metodo 2 — Dalla velocità target:**
1. Pulsante **From Sink Speed** → dialog
2. Inserire la velocità target in in/s (es. `1.5`)
3. Il calcolo inverso determina la densità richiesta
4. Warning se superiore a 2.5 g/cm³ (limite pratico del tungsteno)
5. OK per applicare

**Metodo diretto:**
- Campo **Density (g/cm³)** nella sezione Sinking Tools → inserire il valore e premere Enter

### Parametri Acqua
- **Water type**: Fresh / Salt
- **Temperature**: 0–40 °C
- Modificare questi parametri ricalcola automaticamente tutte le velocità

### Floating / Sinking
- Il flag **Sinking / Floating** si aggiorna automaticamente in base alla fisica
- Per linee floating gli strumenti di compensazione sono nascosti

### Full Line vs Shooting Head
- **Full Line**: attiva la colonna "Head" nella tabella segmenti
- Il CoM e i calcoli AFFTA usano solo i segmenti testa quando attivo

### Mappa Velocità Affondamento
- **Sink Map** toggle → sovrappone una heatmap colorata al profilo
- Colore = velocità locale relativa (blu=lento → rosso=veloce)

---

## 7. Compensazione del Profilo (NC → C)

La compensazione risolve il problema fisico: sezioni di diametri diversi affondano a velocità diverse. Il profilo Compensato (C) modifica i diametri affinché ogni sezione affondi alla stessa velocità target.

### Workflow
1. Impostare la densità del materiale (sezione 6)
2. Verificare che la linea sia classificata come "Sinking"
3. Cliccare **⚖ Compensate**
4. Il profilo C viene calcolato con gradiente di densità
5. Lo **Speed Slider** appare con range min/max della linea NC

### Speed Slider
- Trascina per scegliere la velocità target (in/s)
- La compensazione si ricalcola automaticamente
- Min/Max mostrati ai lati dello slider

### Visualizzazioni
- **Show C** toggle → alterna tra profilo NC e profilo C
- **Show NC ghost** → sovrappone il profilo originale in grigio trasparente
- Gradiente colori sul profilo C = densità del materiale (blu=leggero, rosso=pesante)
- Legenda densità automatica in legenda grafico

### Ugelli in Modalità C
- M1–M4 vengono auto-popolati con le densità quantizzate — **solo quante ne servono davvero** (1 a 4: i livelli più vicini di 0.02 g/cm³ vengono uniti in un solo ugello, così una linea quasi mono-densità non mostra 4 materiali finti)
- Colore corrispondente al gradiente densità; label `ρ X.XX`
- Al ritorno alla modalità NC i colori/label originali vengono ripristinati
- Questo vale solo per la compensazione fisica (Compensate/target speed). Se la modalità C proviene invece da una **zona con densità propria** (sezione 5), gli ugelli restano esattamente quelli configurati a mano — non vengono ricalcolati — e lo slider "Target" resta nascosto perché non esiste un'unica velocità target

### Salvataggio del Profilo Compensato — C e NC sono due file distinti
Il profilo C non viene mai salvato nello stesso file del progetto NC originale, e non è una "vista" ricostruita al volo: **è un file a sé, con la propria geometria indipendente**. Se si salva (`Ctrl+S` o Save As) mentre esiste una compensazione (fisica o da zona):
- Viene creato automaticamente un file separato con lo snapshot compensato: `NomeProgetto C X.XXins.flp` per la compensazione fisica (X.XX = velocità target in in/s), oppure `NomeProgetto C zones.flp` per una compensazione derivata da zone a densità propria
- Quel file salva un nodo ogni ~1cm (uno per ogni tratto fisico calcolato), ciascuno con la propria densità reale — non la geometria NC originale con una "ricetta" da rieseguire. Riaprirlo non richiede il file NC di origine né alcun ricalcolo: i dati sono già lì, definitivi
- Di conseguenza la tabella Segments di un file C ha molte più righe (una ogni ~1cm) di quella di un file NC — è il prezzo della fedeltà esatta al calcolo fisico
- Il file NC originale salva solo il proprio profilo NC, a densità unica — resta sempre "puro" e ricompensabile
- Il file compensato è **bloccato** (`IsCompensatedDerivative`): il pulsante "⚖ Compensate", i controlli densità e la cella densità di M1 sono disabilitati. Per cambiare la compensazione occorre tornare al file NC originale e ricompensare/riassegnare le zone da lì

---

## 8. Grafico Analisi Massa

Visibile solo in Design Mode, sotto il grafico principale.

| Elemento | Significato |
|---|---|
| Barre **rosse** | Segmenti della testa |
| Barre **blu** | Segmenti di running line |
| **◆ diamante** | Centro di massa del singolo segmento |
| **Linea tratteggiata colorata** | Centro di massa totale (colore = taper character) |
| **Linea gialla tratteggiata** | Limite 30 ft AFFTA (914.4 cm) |
| **Curva verde** | Peso cumulativo scalato sull'asse |

---

## 9. Badge AFFTA

Mostrato nella status bar in basso a destra:

```
AFFTA  LW 5   142.3 gr   ✓
```

- **LW**: Line Weight AFFTA (1–14)
- **X.X gr**: peso dei primi 30 ft (914.4 cm) in grains
- **✓/✗**: entro ±6 gr dalla classe nominale
- Se è attiva una zona con densità propria (sezione 5), il peso tiene conto della vera densità di quella zona invece di assumere materiale uniforme su tutta la linea — stesso calcolo usato dalla generazione famiglia (sezioni 10-11)

---

## 10. Generazione Famiglia Pesi Linea (#1–#14)

Dato un profilo disegnato (o ricostruito da scan), è possibile generare automaticamente le varianti dello stesso profilo per le altre classi AFTMA (#1–#14), mantenendo la stessa forma di taper e lo stesso materiale.

### Come funziona
- Il pulsante **Generate Family…** (toolbar Design Tools) apre un dialogo con una checkbox per ciascuna classe #1–#14; la classe corrente del progetto è disabilitata
- Per ogni classe selezionata, **tutti** i diametri della linea (testa, transizione e running line) vengono scalati con lo stesso fattore, a parità di lunghezza — è quello che preserva la "forma" del profilo, running line inclusa
- Il fattore di scala si ricava in forma chiusa (la massa nei primi 30 ft scala esattamente come il quadrato del fattore, a parità di lunghezze), quindi **ogni classe è sempre raggiungibile**, per costruzione
- Nessuna posizione cambia (le lunghezze dei segmenti restano identiche), quindi zone ugello e laser mark non hanno bisogno di essere riposizionati
- La densità (materiale) non viene mai toccata — resta identica in ogni classe generata, zona a densità propria compresa (se presente, viene correttamente pesata invece di essere ignorata)

### File generati
Ogni classe selezionata produce un nuovo file `NomeProgetto #N.flp` accanto al progetto originale (che deve essere già stato salvato in precedenza). Il progetto originale non viene mai modificato.

---

## 11. Generazione Famiglia per Velocità di Affondamento

L'opposto della generazione per peso linea (§10): stessa geometria (stesso taper, stessa classe AFFTA), densità uniforme diversa per ogni velocità di affondamento target.

### Come funziona
- Il pulsante **Generate by Sink Speed…** (toolbar Design Tools) apre un dialogo con uno slider da **Floating** a **10.0 in/s**
- Si trascina lo slider sulla velocità desiderata e si preme **+ Add to list**; si ripete per ogni variante voluta, poi **Generate**
- Per ogni velocità, densità (uniforme) e diametro (fattore di scala uniforme su tutta la linea) vengono risolti **insieme**: la densità si ricava con lo stesso metodo del pulsante "⏬ From sink speed…" (`SinkingSpeedCalc.DensityForTargetSinkSpeed`), il diametro si scala per compensarla in modo che la **massa nei primi 30 ft resti identica a quella della linea sorgente** — nessuna compensazione per-slice, ma nemmeno densità libera di far variare il peso
- Risultato: tutte le varianti generate restano nella **stessa classe AFFTA** della sorgente, esattamente come nelle linee reali in famiglia (es. WF6F / WF6I / WF6S3 / WF6S5 sono tutte "WF6"). Per andare più veloci a parità di peso, il diametro si riduce (materiale più denso, meno volume); per la variante Floating (densità fissata a `RhoFloor` = 0.94 g/cm³, che galleggia), il diametro invece aumenta
- Se una velocità richiesta supera quella raggiungibile a ρ = 2.5 g/cm³ (limite pratico) a parità di massa, il file viene generato comunque alla densità limite e segnalato nel riepilogo come "non esattamente raggiungibile"

### File generati
Ogni velocità produce un file `NomeProgetto X.XXips.flp` (o `NomeProgetto Floating.flp` per lo 0) accanto al progetto originale (che deve essere già stato salvato).

---

## 12. Confronto e Overlay

### Importare una Serie di Confronto
- **Import CSV** → selezionare file (2 colonne cm/mm, o formato Dataset)
- La serie appare sul grafico in colore diverso

### Overlay di un Progetto Esterno
- **Overlay Project** → selezionare un file `.flp`
- Se il progetto contiene sia design nodes sia scan points, viene chiesto quale/i importare: **Design profile only**, **Scan profile only** o **Both profiles**
- Se il progetto ha solo uno dei due tipi di dati, viene importato direttamente senza chiedere
- Le serie importate appaiono in legenda

### Gestire le Overlay
- **Clear Overlays** → rimuove tutte le serie importate (con conferma se più di una)
- In Design Mode le overlay restano visibili anche quando il layer scan è nascosto

### Show/Hide Scan
- **Show Scan / Hide Scan** → alterna la visibilità del layer scan
- In Design Mode il scan è nascosto di default

---

## 13. Export

| Funzione | Formato | Come accedere |
|---|---|---|
| Export Scan CSV | `.csv` | Menu File → Export CSV |
| Export Design Segments | `.csv` | Menu File → Export Segments |
| Save Nodes | `.nodes.csv` | Menu File → Save Nodes |
| Load Nodes | `.nodes.csv/.csv` | Menu File → Load Nodes |
| Export Chart PNG | `.png` | Menu File → Export PNG |
| Export PDF | `.pdf` | Menu File → Export PDF |

### Export PDF — Contenuto
1. **Header**: nome progetto, data, tipo profilo (NC o C)
2. **Grafico profilo** (3200×600 px) con etichette nodi e segmenti
3. **Tabella segmenti** completa
4. **Card informazioni**: tipo core, laser mark, note colore
5. **Nozzle swatches**: colori materiali con densità
6. **Badge AFFTA + CoM/Rg + classificazione taper**

Quando il progetto ha un profilo C salvato, al click Export PDF viene chiesto se esportare NC o C. Il nome del file include automaticamente il suffisso `_NC` o `_C`.

---

## 14. Impostazioni

**Settings** (pulsante ingranaggio) → finestra modale.

### Backend (Connessione)

| Campo | Default | Descrizione |
|---|---|---|
| Profile Name | ESP32 Lab | Nome descrittivo del profilo |
| Host | 192.168.1.50 | IP dell'ESP32 |
| WebSocket Port | 81 | Porta WebSocket |
| HTTP Port | 80 | Porta HTTP |
| Auto-Connect | ON | Connette all'avvio |
| Reconnect (s) | 3 | Secondi tra tentativi automatici |
| Connect Timeout (s) | 5 | Timeout connessione iniziale |
| Load Params on Connect | ON | Legge `getparams` alla connessione |
| Load Motor Status on Connect | ON | Legge `motor status` alla connessione |

### Chart

| Campo | Default | Descrizione |
|---|---|---|
| Show Filtered Series | ON | Mostra curva filtrata (EMA) |
| Show Raw Series | OFF | Mostra curva raw |
| Auto Fit | ON | Adatta automaticamente gli assi |
| Smoothing Alpha | 0.10 | Fattore EMA |
| Line Width | 2 | Spessore linea scan |
| Filtered Opacity | 1.0 | Opacità serie filtrata |
| Raw Opacity | 0.65 | Opacità serie raw |

---

## 15. Interazione con il Grafico

### Navigazione
- **Scroll mouse**: zoom in/out
- **Click e trascina** (fuori dai nodi): pan
- **Fit** button: riadatta la vista ai dati

### Hover Readout
Muovendo il mouse sul grafico, la status bar mostra:

```
S3  Design Ø 3.40 mm   |   Scan Ø 3.35 mm   |   Δ -1.5%   @   245.0 cm
```

Un'annotazione contestuale appare anche sul grafico (soppressa durante Draw mode).

### Drag Etichette
In Design Mode è possibile trascinare il box dell'etichetta di ogni nodo. La posizione viene salvata nel file `.flp`.

---

## 16. Shortcut Tastiera

| Shortcut | Azione |
|---|---|
| **Ctrl+Z** | Undo (solo Design Mode, non durante edit testo) |
| **Shift + Click** | Aggiungi nodo livellato (segmento cilindrico) |
| **Delete** su riga DataGrid | Elimina il nodo / la zona ugello selezionata |
| **Enter** | Conferma in dialoghi e field input |
| **Escape** | Chiude i popup colore |

---

## 17. Formati File

### `.flp` (FlyLine Project)

JSON con i campi principali:

```json
{
  "Version": 1,
  "Name": "Nome Progetto",
  "ScanPoints": [...],
  "DesignNodes": [...],
  "NozzleDefinitions": [...],
  "NozzleZones": [...],
  "SharedDensityGCm3": 1.25,
  "IsSinking": true,
  "IsFullLine": false,
  "WaterType": "fresh",
  "WaterTempC": 15.0,
  "CompTargetSpeedMs": 0.042,
  "ShowCompProfile": true,
  "IsCompensatedDerivative": false
}
```

### `.nodes.csv`

```
Position cm,Diameter mm
0.0,2.850
120.0,3.100
...
```

---

## Appendice — Terminologia

| Termine | Significato |
|---|---|
| **NC** | Non Compensato — profilo originale di design |
| **C** | Compensato — profilo modificato per velocità uniforme |
| **CoM** | Centro di Massa (% dalla punta della testa) |
| **Rg** | Raggio di Girabilità (% della lunghezza testa) |
| **AFFTA LW** | Line Weight AFFTA (classe standard peso linea da mosca) |
| **Nozzle / Ugello** | Estrusore con cui viene prodotto il tratto di linea |
| **Sink speed** | Velocità terminale di affondamento in acqua |
| **EMA** | Exponential Moving Average — filtro digitale smoothing |
| **Frustum** | Tronco di cono — forma geometrica di un segmento conico |
| **ρ (rho)** | Densità del materiale in g/cm³ |
| **Snapshot compensato** | File `.flp` forkato al salvataggio da un profilo C — non ricompensabile |
