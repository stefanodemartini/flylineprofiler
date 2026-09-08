# Task: Derivare la densità del materiale pesando la flyline

Verificato leggendo `Views/WeightToDensityDialog.xaml(.cs)` e `Views/MainWindow.xaml.cs`
(`FromWeight_Click`, `ApplySharedDensity`).

---

## 1. Obiettivo del task

Una volta ricalcato il profilo (dimensioni/diametri noti per ogni segmento), pesare fisicamente
la lenza (o una sua porzione) con una bilancia e usare quel peso per **calcolare a ritroso** la
densità del materiale: ρ = massa / volume. Il volume lo calcola l'app dai segmenti disegnati; tu
fornisci solo il peso misurato.

---

## 2. Procedura operativa

1. Aver già tracciato i nodi/segmenti (§ task 02) — serve un volume su cui basare il calcolo
2. **⚖ From weight…** (pannello Sinking Tools)
3. Nel dialogo, sotto **"WHAT DID YOU WEIGH?"**, scegli su cosa hai basato la pesata:
   - **Whole line** (default) — volume di tutta la linea
   - **Head segments only** — solo i segmenti marcati come Head (checkbox nella tabella Segments)
   - **Custom segment selection** — spunta a mano quali segmenti includere, con un elenco che
     mostra nome e volume di ciascuno
4. Il **Volume of selection** si aggiorna live in base alla scelta
5. Scrivi il peso misurato in grammi in **Measured weight**
6. **Calculated density** mostra live ρ = peso / volume selezionato
7. **Apply** → applica quella densità

---

## 3. Cosa succede "dietro le quinte" — un punto da chiarire con te

Il volume usato nel calcolo dipende dallo scope scelto (§2.3) — fin qui coerente. **Ma il
risultato del calcolo viene sempre applicato a tutta la linea**, indipendentemente dallo scope
scelto: `FromWeight_Click` in `MainWindow.xaml.cs` legge solo `dlg.ResultDensity` (il numero ρ) e
chiama `ApplySharedDensity()`, che scrive quella densità su **ogni** segmento del progetto:

```csharp
private void ApplySharedDensity()
{
    foreach (var seg in ProjectSegments)
        seg.SpecWeightGCm3 = _sharedDensity;   // TUTTI i segmenti, non solo quelli pesati
    ...
}
```

Il dialogo espone anche `IsWholeLineScope` e `AffectedIndices` (quali segmenti erano selezionati)
— ma **nessun chiamante li legge mai**. Sono proprietà calcolate e non utilizzate (verificato con
una ricerca nel codice: compaiono solo nella loro stessa definizione).

**In pratica questo significa**: se scegli "Head segments only" o una selezione custom, l'etichetta
"WHAT DID YOU WEIGH?" descrive correttamente cosa succede — quella selezione serve **solo a
scegliere il volume su cui basare il calcolo di ρ**, non a limitare dove viene applicato il
risultato. Il caso d'uso implicito è: *"ho pesato solo la testa perché è più comodo isolarla sulla
bilancia, ma il materiale è lo stesso su tutta la linea, quindi il ρ calcolato va bene ovunque"*.
Se invece ti aspettavi che scegliendo "Head only" la nuova densità si applicasse **solo** alla
testa (lasciando il resto della linea con la sua densità precedente, per una linea a materiali
misti), oggi non succede: l'intera linea viene sovrascritta con lo stesso numero.

Questo è coerente col modello attuale della densità "condivisa" (`_useSharedDensity` = un solo
materiale per tutta la linea NC) — le zone con materiale proprio sono un meccanismo separato (vedi
il redesign della compensazione), non toccato da questo dialogo.

---

## 4. Verifiche fatte

- **Formula**: ρ = peso (g) / volume (cm³), corretta e diretta — nessun calcolo intermedio
- **Volume dei segmenti**: usa `ProjectSegment.VolumeCm3` (tronco di cono/cilindro, già verificato
  nel task 02)
- **Simmetria con l'altro metodo di stima densità** (⏬ From sink speed…,
  `SinkSpeedToDensityDialog`): quel dialogo **non ha scope selector** — è sempre whole-line. Solo
  "From weight" ha la scelta "cosa hai pesato", quindi solo lì esiste l'ambiguità descritta sopra
- **A valle dell'Apply**: `ApplySharedDensity()` aggiorna densità di ogni segmento, ricalcola
  totali e velocità di affondamento, e specchia il valore su M1 (se non si è in modalità
  compensata) — comportamento coerente e verificato

---

## 5. Chiarito con Stefano (2026-09-08) — CHIUSO, nessuna modifica

Non è un bug: è il comportamento voluto. Il criterio reale è "cosa riesco fisicamente a pesare",
non "a cosa deve applicarsi la densità" — in entrambi i casi si assume **densità costante** su
tutto ciò che si sta modellando:
- **Shooting head** → si pesa solo la testa (la running/shooting line non fa parte del modello)
- **Linea integrale** → si pesa tutta la linea

Nota tecnica che rende la scelta "Head only" ancora più teorica per una shooting head: quando
`_isFullLine = false`, `RefreshSegmentTable()` forza già `IsHead = true` su **tutti** i segmenti
("Shooting head: all segments are head by definition") — quindi in quel caso "Head segments only"
e "Whole line" selezionano esattamente lo stesso volume, stesso risultato. "Custom segment
selection" resta utile solo come comodo pratico (pesare un tratto campione con una bilancia
piccola), sapendo che il materiale è lo stesso ovunque — l'ipotesi originale (§3) era corretta.

Le proprietà `IsWholeLineScope`/`AffectedIndices` restano codice morto (calcolate, mai lette da
nessun chiamante) ma **innocue** — non serve rimuoverle, il loro non-utilizzo è coerente col fatto
che lo scope non deve limitare l'applicazione. Nessuna azione.
