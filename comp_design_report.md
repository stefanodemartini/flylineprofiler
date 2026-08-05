# Compensated Profile — Design Report

---

## 1. Single file vs separate files

### Opzione A — File unico `.flp` (NC + C embedded)

**Pro**
- Un file = un progetto. Nessun file orfano, nessuna gestione manuale.
- C è sempre allineato con NC: si ricalcola sullo stesso file quando NC cambia.
- Non esiste il rischio di mandare al produttore un file C basato su un NC ormai modificato.
- Il PDF di esportazione è già il canale per condividere solo il C con il produttore.

**Contro**
- Il file `.flp` cresce (i dati comp per ogni slice a 1 cm possono essere tanti).

### Opzione B — File separati (`design.flp` + `design_comp.flp`)

**Pro**
- Separazione netta: design brief vs scheda produzione.
- Possibilità di avere più varianti C (velocità diverse, acque diverse) per lo stesso NC.

**Contro**
- Sincronizzazione manuale: modifichi NC, devi ricordarti di rigenerare C e salvare il nuovo file.
- Se perdi il file NC, il C è inutile senza il contesto.
- UX più complessa (doppia gestione file, doppia finestra?).

### Verdetto: **file unico**

C è un risultato calcolato del NC, non un progetto indipendente. Il PDF serve già per il trasferimento al produttore. Il costo di storage extra (array di float) è trascurabile.

---

## 2. Auto-update di C quando NC cambia

**Strategia consigliata: ricalcolo automatico differito**

- Ogni volta che NC cambia (nodo spostato/aggiunto/rimosso, densità modificata, parametri acqua), i dati C vengono marcati come "stale" (obsoleti).
- Il ricalcolo avviene **al rilascio del mouse / LostFocus / cambio tab** — non ad ogni pixel di drag, per evitare lag.
- Il grafico mostra C aggiornato automaticamente; l'utente non deve premere nessun bottone.

**Cosa succede se NC cambia il numero di segmenti?**
- Se si aggiunge/rimuove un nodo, il numero di segmenti cambia: C viene invalidato e ricalcolato.
- L'overlay mostra il C aggiornato sul nuovo NC.

---

## 3. Variabilità della velocità target

### Il range fisicamente valido

Con densità uniforme ρ lungo il NC, ogni slice ha una velocità naturale proporzionale al suo diametro.
Il range utile per il target V è:

```
V_min  ≤  V_target  ≤  V_max
```

Dove:
- **V_min** = velocità della slice più lenta (tip, diametro minore)
- **V_max** = velocità della slice più veloce (belly, diametro maggiore)

- **V_target > V_max**: le slice più veloci dovrebbero accelerare oltre il loro naturale — fisicamente impossibile senza aumentare densità oltre ogni limite pratico.
- **V_target < V_min**: tutte le slice dovrebbero rallentare — richiederebbe densità < ρ_acqua per alcune, ovvero la slice galleggerebbe. Non ha senso per una sinking line.

### Cosa varia al variare di V_target

| V_target | Effetto sulle slice lente (tip) | Effetto sulle slice veloci (belly) |
|----------|----------------------------------|-------------------------------------|
| = V_max  | diametro ↑↑, densità ↓↓ (molto diverso dall'orig) | invariate |
| = V_mid  | diametro ↑, densità ↓ (moderato) | diametro ↓, densità ↑ (moderato) |
| = V_min  | invariate | diametro ↓↓, densità ↑↑ (molto diverso dall'orig) |

**V_max** (attuale default) → trasforma tutto al ritmo della sezione più veloce.
La sezione lenta (tip) si allarga molto e diventa meno densa.

**V_mid** → bilanciato: variazioni di diametro/densità più contenute su entrambe le estremità.
Può produrre una distribuzione di densità più omogenea e quindi **più facile da produrre**.

**V_min** → tutto al ritmo del tip. La sezione più veloce (belly) si restringe e diventa densa.
Caso più estremo, raro in pratica.

### Utilità pratica

La scelta di V_target determina la **classe AFTM** della linea risultante:

```
Class I  : 1.25–2.00 in/s
Class II : 2.00–3.00 in/s
Class III: 2.50–3.50 in/s
...
```

Poter scegliere V_target nel range [V_min, V_max] permette al designer di:
1. Scegliere a quale classe di affondamento apparterrà il prodotto finale.
2. Ottimizzare la distribuzione delle densità per la producibilità (meno variazione = meno materiali diversi).
3. Valutare trade-off: velocità più bassa → variazioni di densità più estreme sulle sezioni veloci.

### Verdetto: **sì, vale la pena**

Un **slider o campo numerico** da V_min a V_max (con indicazione della classe AFTM corrispondente)
dà controllo significativo senza aggiungere complessità di fisica — il codice `CompensateProfile`
già supporta qualsiasi V_target, basta passare il valore scelto.

**Default**: V_max (comportamento attuale, corrisponde a "porta tutto al ritmo massimo").

---

## 4. Overlay NC + C

**Molto utile.** Mostrare NC e C sovrapposti nello stesso grafico permette di vedere:
- Quanto il C si discosta dal NC in ogni punto (differenza di diametro).
- Dove le variazioni sono più marcate (le sezioni che richiedono il salto di densità maggiore).
- La continuità del profilo C rispetto al design originale.

**Proposta visiva**:
- NC: linea sottile grigia/traslucida (ghost), con gradiente velocità di affondamento.
- C: linea principale colorata con gradiente densità (blu→rosso per i 4 materiali).
- Toggle on/off dell'overlay NC.

---

---

## 5. Floating profile — compensation not available

If a project is declared as **floating** (line density < water density by design),
the entire compensation section must be hidden/disabled:

- "Compensate" button hidden
- Comp mode toggle unavailable
- Comp columns in segment table not shown
- PDF export: no comp section

**Why**: a floating line has no sinking speed to equalise. Compensation is
physically meaningless in this context.

---

## 6. Density floor in compensation (ρ_min = 0.94 g/cm³)

When the target speed is low, some slices may require a diameter so large
that the computed ρ_new drops below the water density (~1.0 g/cm³).
This would cause the slice to float, defeating the purpose.

**Rule**: clamp ρ_new to a minimum of **0.94 g/cm³**.

Note: 0.94 g/cm³ < water → that slice will float. This is an acknowledged
side effect when V_target is too low relative to a given slice's natural speed.

**Required behaviour**:
- Apply the floor: if ρ_new < 0.94, set ρ_new = 0.94 and recompute d_new from mass conservation.
- Flag the affected slices visually (e.g. coloured marker, warning icon).
- Show a warning in the UI: "Some sections cannot reach the target speed — density clamped to 0.94 g/cm³. These sections will float."
- In the PDF, mark affected segments clearly.

---

## 7. PDF export for C profile

The C profile PDF must use **C-specific content throughout**. Nothing from the
NC profile should bleed into the C export.

### What must change vs the current NC PDF

| Element | NC PDF | C PDF |
|---------|--------|-------|
| Diameters | original d(x) | compensated d_new(x) per slice |
| Density column | original ρ per segment | ρ_new per slice / section |
| Density legend | NC material | C material assignments |
| Spec text / recommendations | NC guidance | C-specific guidance |
| Language | (currently mixed) | **English throughout** |

### C-specific spec text

The comp PDF should describe:
- Target sink speed (in/s or cm/s) used for compensation
- Water type and temperature used
- Density range of the compensated material (min–max ρ_new)
- Warning if any section was clamped to ρ_min = 0.94 g/cm³
- Note: "This is a compensated profile. Diameters and densities vary per section
  to achieve uniform sink speed along the entire line."

**Language**: all text in the PDF must be in English.

---

## Riepilogo decisioni

| Tema | Decisione |
|------|-----------|
| File unico o separati | **File unico** |
| Auto-update C | **Sì, ricalcolo differito al rilascio edit** |
| C read-only | **Sì** |
| Velocità target variabile | **Sì, slider V_min → V_max con label classe AFTM** |
| Default velocità target | **V_max** |
| Densità minima | **0.94 g/cm³ (floor), slice interessate evidenziate** |
| Floating profile | **Compensation completamente nascosta** |
| Overlay NC + C | **Sì, NC ghost + C colorato per densità** |
| PDF profilo C | **Dati C corretti, testo spec specifico per C, tutto in inglese** |
