# Task: Ricalco dei taper e creazione dei segmenti (Modalità Design)

Verificato leggendo il codice (`Views/MainWindow.xaml(.cs)`, `Models/ProjectSegment.cs`).

---

## 1. Obiettivo del task

Dopo uno scan (task 01), passi in **Design Mode** e ricalchi a occhio la curva scan piazzando dei
**nodi** (posizione cm, diametro mm) sul grafico. Ogni coppia di nodi adiacenti diventa
automaticamente un **segmento**: un tronco di cono, o un cilindro se i due diametri coincidono. Il
risultato — una sequenza di segmenti contigui — è elencato in una tabella (`SegmentsDataGrid`), a
fianco della tabella dei nodi grezzi (`NodesDataGrid`).

---

## 2. Prerequisiti

- **Design Mode ON** (toggle in alto a destra) — mostra i controlli di design e nasconde quelli di
  scan
- **Draw Segments ON** (menu, `MenuDrawSegments`) — necessario per **aggiungere** nodi cliccando sul
  grafico. Senza, il click sul grafico non fa nulla (il trascinamento di nodi già esistenti invece
  funziona comunque, vedi §4)
- Uno scan visibile sotto (facoltativo ma è lo scopo pratico del task): la curva scan resta a
  schermo come riferimento visivo mentre tracci i nodi sopra di essa

---

## 3. Procedura operativa passo-passo

1. **Design Mode ON**, poi attiva **Draw Segments**
2. **Click sinistro** su un punto vuoto del grafico → aggiunge un nodo lì
   - La posizione X viene **sempre arrotondata al cm intero** (`Math.Round(coords.X)`), qualunque
     sia lo zoom
   - Il diametro Y è quello del punto cliccato (`|coords.Y| × 2`, perché l'asse Y del grafico è il
     raggio, non il diametro)
3. **Shift + click sinistro** → il diametro del nuovo nodo viene **agganciato esattamente** al nodo
   esistente più vicino sull'asse X, invece che al punto cliccato — è il modo per garantire un
   **cilindro esatto** (stesso Ø di inizio e fine) senza doverlo ritoccare a mano dopo
4. **Click destro** su un punto → rimuove il nodo più vicino (solo se Draw Segments è attivo)
5. **Trascinamento** di un nodo esistente (click e tieni premuto, poi rilascia) → sposta quel nodo;
   **funziona anche con Draw Segments spento** — solo l'aggiunta di nodi nuovi richiede
   esplicitamente quella modalità
6. In alternativa/in aggiunta al disegno sul grafico: **editing diretto nella tabella Nodes**
   (pannello a sinistra della tabella Segments) — due colonne, Position (cm) e Diameter (mm),
   editabili a testo libero
7. Ogni volta che nodi/segmenti cambiano, la tabella **Segments** a destra si ricalcola da sola e
   mostra la sequenza di tronchi di cono/cilindri risultante

---

## 4. Cosa succede "dietro le quinte"

### 4.1 I segmenti non sono oggetti persistenti — vengono ricostruiti da zero ogni volta
`RefreshSegmentTable()` non modifica i segmenti esistenti: **svuota `ProjectSegments` e li ricrea
tutti**, ordinando i nodi per X e creando un segmento per ogni coppia adiacente
(`sorted[i] → sorted[i+1]`). Prima di svuotare, salva in una mappa temporanea (`_segmentMetadata`)
i campi che l'utente ha impilato a mano — **Nome, Sp. Weight (se non condivisa), flag Head** — e li
riattacca ai nuovi segmenti per indice di posizione (0, 1, 2…). Anche i dati di compensazione già
calcolati vengono salvati e riattaccati allo stesso modo (`compSnapshots`).

**Conseguenza pratica**: se inserisci un nodo *in mezzo* a una sequenza esistente (es. tra il
segmento 3 e il segmento 4), tutti i segmenti dal nuovo in poi **scalano di indice** — quello che
prima era S4 diventa S5, e la sua metadata (nome personalizzato, ecc.) resta associata all'indice
di posizione 4, non al segmento fisico: **il nome che avevi dato a "S4" finisce sul segmento
sbagliato** se ne inserisci uno prima. Non è un bug che ho corretto — è un comportamento reale del
codice attuale, da tenere presente quando rinomini i segmenti prima di finire di tracciare tutta
la linea.

### 4.2 Come viene classificato Taper vs Cylinder
`ProjectSegment.IsCylinder` confronta `StartDiameterMm` e `EndDiameterMm` con una tolleranza di
**0.001 mm**. Se uguali (entro tolleranza) → `Shape = "Cylinder"`, `Taper (mm/m) = "—"`. Altrimenti
→ `Shape = "Taper"` e la colonna Taper mostra la pendenza in **mm per metro**, con segno (positivo
= si allarga verso la fine, negativo = si assottiglia).

Con lo Shift-click il match è **esatto** (stesso valore Y copiato dal nodo vicino, non solo vicino
per arrotondamento) — è il modo affidabile per ottenere `Shape = Cylinder`; cliccando a mano due
punti "che sembrano uguali" sul grafico c'è il rischio di un residuo > 0.001mm che li classifica
come Taper impercettibile invece che Cylinder.

### 4.3 Editing diretto nella tabella Segments — cosa succede ai nodi
Le colonne **Start Ø**, **End Ø** e **Length** sono editabili direttamente nella tabella Segments,
ma non modificano il segmento in isolamento: **riscrivono il nodo condiviso** nell'elenco grezzo
(`_segmentNodes`), poi richiamano `RefreshSegmentTable()`:
- **Start Ø** di un segmento → cambia lo Y del nodo di inizio
- **End Ø** → cambia lo Y del nodo di fine — che è **anche** il nodo di inizio del segmento
  successivo: modificarlo **cambia anche il segmento successivo**, non solo quello che stai
  editando
- **Length** → sposta solo la X del nodo di fine (`StartCm + nuova lunghezza`). Questo **allunga o
  accorcia il segmento corrente e quello successivo di conseguenza** (perché condividono il nodo),
  ma **non trasla** tutti i segmenti a valle: la loro posizione assoluta resta quella che era, il
  segmento successivo semplicemente diventa più corto o più lungo per compensare

Le colonne **Name** e **Sp. Weight** invece non toccano nodi — sono proprietà proprie del segmento,
scritte direttamente (`colIdx == 2 || colIdx == 9` gestite a parte, nessuna sincronizzazione con
`_segmentNodes`).

### 4.4 Precisione posizione: chart vs tabella — un'asimmetria da conoscere
- **Click sul grafico** → X sempre arrotondata al **cm intero**
- **Editing nella tabella Nodes** (`Position (cm)`, formato `0.0`) → puoi scrivere un valore con
  **un decimale**, nessun arrotondamento forzato
Quindi il grafico impone una granularità di 1 cm per nuovi nodi, mentre la tabella permette mezzo
centimetro di precisione. Se ti serve posizionare un nodo a una X frazionaria, va fatto da tabella,
non da click.

### 4.5 Un solo nodo per posizione X
Se clicchi (o inserisci in tabella) un nodo a una X dove ce n'è già uno, **quello vecchio viene
sostituito**, non si crea un nodo affiancato — è il modo per "correggere" un nodo già piazzato:
riclicca alla stessa X con il diametro giusto.

---

## 5. Cosa produce (la finestra Segments)

La tabella `SegmentsDataGrid` mostra, per ogni segmento, in quest'ordine (le colonne C/NC in più
appaiono solo quando esiste un profilo compensato — vedi task-review sulla compensazione):

| Colonna | Editabile | Origine |
|---|---|---|
| **#** | No | Indice di posizione (1, 2, 3…) — non un ID stabile, vedi §4.1 |
| **Head** | Sì (checkbox) | Segna il segmento come parte della "testa" (per calcolo AFFTA/peso). Per una shooting head l'app forza `IsHead = true` su **tutti** i segmenti, checkbox incluso |
| **Name** | Sì (testo) | Default `S{indice}`, sovrascrivibile |
| **Length (cm)** | Sì | `EndCm − StartCm`; editarla sposta il nodo di confine (§4.3) |
| **Volume (cm³)** | No | Calcolato: cilindro `π·r²·L`, tronco di cono `π·L/3·(r1²+r1·r2+r2²)` |
| **Mass (g)** | No | `Volume × Sp.Weight`, mostra "—" se la densità non è impostata |
| **Start Ø / End Ø (mm)** | Sì | Diametri agli estremi — editarli sposta lo Y del nodo condiviso |
| **Shape** | No | "Cylinder" o "Taper", vedi §4.2 |
| **Taper (mm/m)** | No | Pendenza con segno, "—" per i cilindri |
| **Sp.W. (g/cm³)** | Sì | Densità del materiale — condivisa su tutta la linea o per segmento, secondo modalità |
| **Sink (in/s)** | No | Velocità di affondamento calcolata da `SinkingSpeedCalc` |

Ogni riga con **Head = true** viene evidenziata (sfondo blu scuro, testo azzurro) grazie a un
`DataTrigger` sullo stile della riga — colpo d'occhio immediato su quali segmenti fanno parte della
testa.

In parallelo, ad ogni modifica si aggiornano automaticamente: i totali di volume/massa
(`RefreshTotals`), le velocità di affondamento (`UpdateSinkingSpeeds`), il badge AFFTA
(`RefreshAfftaBadge`) e il grafico di analisi massa sotto al grafico principale
(`RefreshAnalysisPlot`) — nessuno di questi richiede un salvataggio o un ricalcolo manuale.

---

## 6. Punti confermati con Stefano (2026-09-08)

1. **§4.1 — la metadata seguiva l'indice, non il segmento fisico** → confermato problema, **corretto**.
   `_segmentMetadata` (e lo snapshot interno della compensazione, stessa causa) ora usano come chiave
   la **StartCm del segmento** (X del suo nodo sinistro) invece dell'indice di posizione — stesso
   pattern già in uso per gli offset delle etichette (`_nodeLabelOffsets`/`RemapLabelOffset`).
   Risultato: inserire un nodo in mezzo a una sequenza già nominata sposta il nome solo sul pezzo
   che è realmente cambiato — tutti gli altri segmenti, prima e dopo, mantengono nome/peso/flag Head
   intatti.
   **Effetto collaterale gestito**: trascinare un nodo cambia la sua X per tutta la durata del
   gesto, quindi la chiave della metadata deve "seguirlo" — aggiunta `RemapSegmentMetadata()`,
   agganciata negli stessi punti dove già esisteva `RemapLabelOffset()` (fine trascinamento nodo,
   doppio-click per modificare le coordinate). Durante il trascinamento stesso può esserci un
   piccolo sfarfallio del nome (torna a "S{n}" per un istante) — è lo stesso comportamento già
   presente per le etichette, si sistema da solo al rilascio del mouse.
   **Aggiornamento 2026-09-08 — chiuso anche il varco tabella**: confermato da Stefano ("comportamento
   biunivoco" tabella↔grafico). I valori (posizione/diametro) erano già sincronizzati bidirezionalmente
   in entrambe le direzioni — grafico→tabella via `SyncDesignNodesToList()`, tabella→grafico via
   `SyncListFromDesignNodes()` + `RefreshPlot()`, già presenti prima di questa sessione. Quello che
   mancava era la sola sopravvivenza di nome/peso/Head quando lo spostamento di un nodo arrivava
   dalla tabella invece che dal trascinamento sul grafico:
   - **Tabella Nodes**, colonna Position: `NodesDataGrid_CellEditEnding` ora cattura la X prima
     della modifica e richiama `RemapLabelOffset` + `RemapSegmentMetadata` verso la nuova X, esattamente
     come già avveniva per un trascinamento
   - **Tabella Segments**, colonna Length: stesso identico problema trovato e corretto —
     modificare la lunghezza sposta la X del nodo di confine condiviso; `SegmentsDataGrid_CellEditEnding`
     ora fa lo stesso remap prima di ricostruire la tabella
   File: `Views/MainWindow.xaml.cs` (`_segmentMetadata`, `RefreshSegmentTable`,
   `LoadProjectFromFile`, `ReverseNodes_Click`, `RemapSegmentMetadata`,
   `NodesDataGrid_CellEditEnding`, `SegmentsDataGrid_CellEditEnding`). Verificato: `dotnet build`
   pulito (due volte, dopo ciascuna delle due modifiche), avvio app OK.

2. **§4.4 — granularità 1cm dal grafico vs decimale da tabella** → in sospeso, non ancora risposto.
3. **Resto del flusso (Shift-click, click destro, editing tabella Segments)** → in sospeso, non
   ancora risposto.
