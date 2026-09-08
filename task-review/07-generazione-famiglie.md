# Task: Generazione famiglie da una linea finita (per code AFFTA #1-14 e per velocità di affondamento)

Verificato leggendo `Services/LineWeightFamilyCalc.cs`, `Views/GenerateLineFamilyDialog.xaml(.cs)`,
`Views/GenerateSinkSpeedFamilyDialog.xaml(.cs)`, `Views/MainWindow.xaml.cs`
(`GenerateLineFamily_Click`, `GenerateSinkSpeedFamily_Click`, `SaveFamilyVariants`,
`BuildFamilyMemberProject`).

---

## 1. Obiettivo del task

Da una linea completa (nodi tracciati, densità impostata), generare automaticamente una famiglia
di file sorelle:
- **Generate Family…** → stessa forma di taper, stesso materiale, diametri scalati per colpire il
  peso-classe AFFTA target (#1–#14)
- **Generate by Sink Speed…** → stessa forma di taper, densità (uniforme) e diametro risolti
  insieme per colpire una velocità di affondamento target, mantenendo lo stesso peso (quindi la
  stessa classe AFFTA nominale) del progetto sorgente — esattamente come una linea commerciale
  mantiene "WF6" invariato tra le varianti F/I/S3/S5

---

## 2. Procedura operativa

### Generate Family (per classe AFFTA)
1. **Generate Family…** (Design Tools) — richiede densità impostata e progetto già salvato
2. Dialogo con 14 checkbox (#1–#14); la classe AFFTA attuale del progetto è rilevata e mostrata
   disabilitata ("(current)") — non puoi generare una copia di te stesso
3. Spunti le classi desiderate → **Generate**
4. Un file per classe, salvato accanto al progetto sorgente, nome `{progetto} #{n}.flp`

### Generate by Sink Speed
1. **Generate by Sink Speed…** — stessi prerequisiti
2. Dialogo con uno slider da **Floating** a **10.0 in/s**; **+ Add to list** per accumulare più
   velocità target, poi **Generate**
3. Un file per velocità, nome `{progetto} {ips}ips.flp` (o `{progetto} Floating.flp`)

Entrambi i flussi condividono `SaveFamilyVariants`: avvisano una volta sola se dei file
esistenti verrebbero sovrascritti, salvano, e mostrano un riepilogo con eventuali note (classe non
raggiungibile, velocità clampata alla densità limite pratica 0.94–2.5 g/cm³).

---

## 3. Verifiche matematiche fatte

### Generate Family — scala per peso
- Tabella pesi target: standard AFTMA in grani (#1=60gr … #14=500gr) — **verificata, corrisponde
  ai valori standard di settore**
- Finestra di calcolo: primi **30 ft = 914.4 cm** dalla punta — standard AFTMA corretto
- **Densità mai toccata** — solo i diametri scalano, per un fattore unico `s = √(targetGrammi /
  grammiSorgente)`. Essendo il volume quadratico nel raggio, la massa scala esattamente come s² a
  parità di densità e lunghezze — quindi `s` si risolve in forma chiusa (nessuna bisezione, sempre
  raggiungibile per qualunque target positivo). Corretto.
- Le posizioni dei nodi non cambiano (`RemapCm = x => x`) → zone ugello e laser mark non hanno
  bisogno di essere rimappati, restano validi automaticamente

### Generate by Sink Speed — scala + densità risolte insieme
- Per ogni velocità target, risolve **contemporaneamente** un fattore di scala diametro `s` e una
  densità uniforme tali che: (a) la linea scalata a quella densità affondi esattamente al target
  (`SinkingSpeedCalc.DensityForTargetSinkSpeed`), e (b) la massa nei primi 30ft risultante coincida
  con quella della linea sorgente (stessa classe AFFTA nominale). Bisezione su `s` (80 iterazioni),
  verificata monotona tramite bracket `[0.1, 10.0]` — corretto
- **Floating** (velocità ≤ 0): densità fissata al **pavimento pratico 0.94 g/cm³** (`RhoFloor`,
  stessa costante fisica già verificata nei task precedenti), risolto solo il diametro con lo
  stesso metodo in forma chiusa di "Generate Family" — corretto e coerente

### Zone a densità propria nella linea sorgente
- **Generate Family (per peso)**: usa `BuildEffectiveSegmentsForFamilySource()` (segmenti spezzati
  ai confini di zona, ciascun pezzo con la propria densità reale — già verificato nel redesign
  compensazione) sia per il calcolo della massa sia come geometria di base. La densità di ogni
  pezzo **non viene mai toccata**, solo scalata in diametro insieme al resto → le zone a materiale
  proprio sopravvivono correttamente nel file generato, con densità intatte
- **Generate by Sink Speed**: stesso punto di partenza (segmenti spezzati per zona), ma qui la
  densità **per definizione** diventa un unico valore nuovo uniforme per tutta la linea (è lo scopo
  della funzione — vedi §1). Trovato un dettaglio da conoscere, non un errore di calcolo:

---

## 4. Punto da conoscere: le zone ugello sopravvivono "vuote" nei file sink-speed

Quando la linea sorgente ha zone con materiale proprio, il file generato da **Generate by Sink
Speed** copia comunque le `NozzleZones` (stesse posizioni, stesso `NozzleIndex`) — ma il codice
sovrascrive **tutte** le densità di `NozzleDefinitions` (M1–M4) con l'unico nuovo valore risolto:

```csharp
double newDensity = result.Segments.Count > 0 ? result.Segments[0].SpecWeightGCm3 : 0;
foreach (var n in project.NozzleDefinitions) n.DensityGCm3 = newDensity;
```

Risultato: il file generato ha ancora le zone colorate diversamente nella griglia "Nozzle Zones"
(posizioni e colori preservati), ma **tutte puntano a materiali con la stessa identica densità** —
la distinzione di materiale è fisicamente corretta da eliminare (l'intera linea ha *un* nuovo
materiale per centrare la velocità target, coerente con §1), ma visivamente le zone restano lì
come se avessero ancora un ruolo, il che può confondere aprendo il file dopo.

**Non è un errore di fisica o di massa/velocità** — quei numeri restano corretti. Chiarito con un
esempio concreto (linea con punta M2 più pesante → variante sink-speed a densità uniforme): il
grafico del file generato **non** si disegna a zone colorate (dato che tutte le densità coincidono,
`anyRealZone` risulta falso al caricamento) — il residuo si vede *solo* aprendo il pannello
"Nozzle Zones" e guardando la tabella, senza alcun effetto su fisica o rendering.

### Corretto — 2026-09-08
Confermato da Stefano, **implementato**: il postProcess di `GenerateSinkSpeedFamily_Click` ora
chiama `project.NozzleZones.Clear()` oltre a uniformare le densità dei nozzle — solo per questo
generatore, non per "Generate Family" (dove le zone restano correttamente valide, vedi sopra).
File: `Views/MainWindow.xaml.cs`. Verificato: `dotnet build` pulito, avvio app OK.

---

## 5. Verifiche operative fatte

- Overwrite protection: un solo avviso cumulativo prima di sovrascrivere file esistenti,
  verificato
- La classe AFFTA corrente viene rilevata correttamente e disabilitata nel dialogo (non puoi
  generare te stesso)
- Laser mark: se `LaserMarkFromTipMm` è un numero valido, viene rimappato con la stessa funzione
  di posizione (`x => x` per questi due generatori → resta invariato, corretto perché le posizioni
  non cambiano mai in nessuno dei due flussi)
