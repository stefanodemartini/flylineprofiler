# Task: Generazione PDF — il file per il produttore

Verificato leggendo `Services/FlyLinePdfExporter.cs` (908 righe) per intero e
`Views/MainWindow.xaml.cs` (`ExportPdf_Click`).

---

## 1. Struttura del documento — panoramica

Foglio A4 orizzontale: intestazione di riservatezza, logo + titolo progetto, blocco note
(peso/densità, istruzioni "non alterare"), riga di specifiche (tipo, formato, densità, lunghezze,
peso testa/totale, CoM/Rg, carattere del taper, classe AFFTA — o per un profilo compensato: velocità
target, acqua, avviso ρ minima), il grafico del profilo (identico a quello a schermo, coerente col
principio "quello che vedi è quello che è" del task 05), legenda materiali (ugelli/zone o materiali
C), eventuali note colore/laser mark, **tabella segmenti** completa (posizione, diametri, taper,
densità, massa, gr/ft, velocità di affondamento), riga totali, e un footer fisso a fondo pagina
(peso testa/totale, badge AFFTA, dicitura di riservatezza).

Verifiche di base superate: conversione cm→mm×10 corretta ovunque, formula taper mm/m coerente con
quella già validata nei task precedenti, il grafico incluso è il rendering live (non un secondo
ricalcolo), nessuna domanda "NC o C" residua (coerente col redesign compensazione).

---

## 2. Trovato — CRITICO: il peso in footer può essere sbagliato per un export compensato/a zone

Il footer (`Head: X gr · Total: Y gr`, presente su ogni pagina accanto al badge AFFTA) usa
`headMassGr`/`totalMassGr`, calcolati **una sola volta in cima alla funzione** da `seg.MassG` —
che a sua volta usa **sempre** `StartDiameterMm`/`EndDiameterMm`/`SpecWeightGCm3` (i campi di
disegno NC), **mai** i dati compensati (`CompSliceDiamsMm`/`CompSliceDensities`).

La **tabella dei segmenti**, invece, per un export compensato calcola correttamente
`compMassG` dalle fette compensate reali (righe 807-815) e lo somma in un totale separato, usato
solo per la riga TOTALE della tabella stessa.

**Risultato**: in un PDF compensato (fisico o a zone), la tabella in fondo mostra il peso vero
(calcolato dai dati compensati), mentre il footer in ogni pagina mostra il peso **come se fosse
tutta materiale base non compensato** — due numeri diversi per "peso totale" nello stesso
documento. Per una linea a zone (materiale diverso su parti della linea) lo scarto può essere
sostanziale, perché il footer ignora del tutto la densità reale delle zone.

**Perché è critico**: è esattamente il tipo di numero che un produttore userebbe per verificare o
tarare la produzione — due valori diversi sulla stessa pagina, uno sbagliato, è un rischio reale
di errore in produzione.

---

## 3. Trovato — SIGNIFICATIVO: la velocità di affondamento in tabella può essere vuota o sbagliata

La colonna "Sink in/s" per la riga compensata, e il blocco specifiche "Target sink" in testata,
usano entrambi lo stesso parametro globale `compTargetSpeedIns` passato da `MainWindow` — invece
di leggere il valore reale già presente su ogni segmento (`seg.CompSpeedText`).

Verificato che questo parametro **non è affidabile** in due casi concreti:

**a) Linea a zone (multi-materiale)**: `compTargetSpeedIns` viene passato a `0` (non esiste un
target unico condiviso, per definizione — ogni zona affonda alla propria velocità). Risultato: la
colonna Sink mostra **"—" su ogni riga della tabella**, anche se ogni segmento ha già la propria
velocità reale calcolata e disponibile (`seg.CompSpeedText`) — il produttore non vede nessuna
informazione di affondamento per una linea multi-materiale.

**b) Un file C salvato e poi riaperto in una sessione pulita**: il campo che tiene
`_compTargetSpeedIns` a livello di finestra (`current comp target in in/s`) **non viene mai
ripristinato** al caricamento di uno snapshot C — i file C nuovo-formato non salvano più una
"ricetta" con la velocità target (principio "C e NC sono due file distinti", vedi redesign
compensazione), quindi non c'è nulla da cui rileggerla. Il campo resta al suo valore di default
(**1.0 in/s**), oppure — più insidioso — a un **valore residuo di sessione**, se in precedenza
nella stessa sessione avevi lanciato Compensate su un progetto diverso: aprendo poi un file C
scorrelato ed esportando il PDF, la testata "Target sink" e ogni riga "Sink in/s" mostrerebbero
la velocità **dell'altro progetto**, non quella reale di questo file.

---

## 4. Causa comune e proposta di correzione

Entrambi i problemi condividono la stessa causa: alcune parti dell'esportatore si fidano di un
**parametro globale passato dall'esterno** invece di leggere il dato **già corretto, presente su
ogni segmento** (`seg.MassG`-equivalente compensato, `seg.CompSpeedText`) — esattamente il
principio già affermato nel task 05 ("quello che vedo è quello che conta").

**Proposta**:
1. Footer: calcolare `headMassGr`/`totalMassGr` dalla massa compensata reale (`CompSliceDiamsMm` ×
   `CompSliceDensities`) quando il segmento ha compensazione, invece che sempre da `seg.MassG`
2. Colonna Sink per riga compensata: usare `seg.CompSpeedText` (il valore reale, sempre presente e
   corretto) invece di `compTargetSpeedIns`
3. Blocco specifiche "Target sink" in testata: derivarlo dai segmenti stessi — se tutti condividono
   la stessa velocità (compensazione fisica uniforme) mostrarla come oggi, altrimenti (zone) mostrare
   un range o "varia per zona" invece di "—"

### Corretto e verificato — 2026-09-08
Entrambi i punti implementati in `Services/FlyLinePdfExporter.cs`:
- Nuova funzione locale `EffectiveMassG(seg)`: massa dalle fette compensate reali quando presenti,
  altrimenti `seg.MassG` — usata sia per il footer (`headMassGr`/`totalMassGr`) sia per la riga
  TOTALE della tabella (che prima duplicava lo stesso calcolo in un accumulatore separato,
  `totalCompMassG`, ora rimosso — una sola fonte di verità)
- Colonna Sink per riga compensata: `seg.CompSpeedText` invece del parametro globale
- Blocco "Target sink" in testata e nota descrittiva: derivati da
  `segments.Where(s => s.HasCompensation).Select(s => s.CompensatedTargetSpeedMs)` — valore singolo
  se tutti i segmenti coincidono (compensazione fisica uniforme), range "X–Y in/s (per zone)"
  altrimenti. Anche l'etichetta "Type" ("uniform sink" / "per-zone sink") ora segue lo stesso dato
- Il parametro `compTargetSpeedIns` è stato **rimosso** dalla firma di `Export(...)` — non c'è più
  nulla, in nessun punto della funzione, che si fidi di un valore passato dall'esterno invece che
  letto dai segmenti stessi
- **Bonus trovato durante la verifica visiva**: la nota "Density varies along the line (X – Y
  g/cm³ g/cm³)" ripeteva l'unità due volte (bug preesistente, non introdotto in questa sessione) —
  corretto insieme, dato che era nella stessa riga di testo appena riscritta

**Verificato con un PDF reale**, non solo a lettura di codice: creato un harness di test a parte
(`PdfSmokeTest`, fuori dal repository, in scratchpad) che chiama `FlyLinePdfExporter.Export(...)`
direttamente con dati sintetici che riproducono lo scenario del bug — una shooting head con pancia
leggera (ρ 1.05) e punta affondante più pesante (ρ 1.50, 2 velocità diverse). PDF generato e letto:
footer **66.7 gr** coincide esattamente con il totale di tabella **66.7 gr** (prima del fix
sarebbero stati due numeri diversi), "Target sink" mostra **1.772–3.543 in/s (per zone)** invece di
"—", ogni riga della tabella ha la propria velocità reale. Verificato anche lo scenario a velocità
uniforme (footer e tabella coincidono su 42.3 gr, "Target sink" un solo valore 2.500 in/s) e un
export NC semplice come controllo, invariato. I tre PDF sono stati inviati a Stefano per verifica
diretta.

File: `Services/FlyLinePdfExporter.cs`, `Views/MainWindow.xaml.cs` (`ExportPdf_Click`). Verificato:
`dotnet build` pulito, avvio app OK, PDF di verifica generati e ispezionati.
