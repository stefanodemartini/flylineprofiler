# Task: Gestione ugelli — materiale (colore + densità) indipendente dai tapers

Verificato leggendo `Models/LineColorSectionVm.cs` (`NozzleZoneVm`), `Views/MainWindow.xaml.cs`
(`ApplyZoneDensities`, `RenderNozzleZones`, `ReverseNodes_Click`, i generatori di famiglia).

---

## 1. Requisito

Fino a 4 ugelli (M1–M4) per progetto, ciascuno con **colore e densità propri**. L'assegnazione di
un ugello a un tratto di linea (una "zona") può cambiare in **qualunque punto** della linea, senza
dover coincidere con un nodo o un cambio di pendenza del taper. Questo deve restare vero in
qualsiasi operazione sul progetto.

---

## 2. Verificato: le zone sono davvero indipendenti dai nodi

`NozzleZoneVm.StartCm`/`EndCm` sono numeri decimali liberi (non riferimenti a indici di nodo, non
vincolati a coincidere con una X esistente in `_segmentNodes`). Confermato in tre punti diversi del
codice che una zona funziona correttamente anche a cavallo di un cambio di pendenza:

- **`ApplyZoneDensities()`**: affetta ogni segmento in **micro-fette da 1cm** indipendentemente
  dai suoi nodi, e per ciascuna verifica in quale zona ricade il suo punto medio — quindi una zona
  che inizia a metà di un taper viene applicata correttamente dal centimetro esatto, non solo dal
  prossimo nodo
- **`RenderNozzleZones()`**: calcola il diametro esatto al bordo della zona con
  `InterpolateProfileY(sorted, zone.StartCm/EndCm)` — interpola sul taper anche se il bordo zona
  cade a metà di un segmento, non solo sui nodi esistenti
- **`BuildEffectiveSegmentsForFamilySource()`** (usato da AFFTA/generazione famiglie, task 07):
  spezza un segmento esattamente al bordo della zona, ovunque cada, assegnando a ciascun pezzo la
  densità corretta

**Confermato**: il requisito di base è già rispettato dall'architettura attuale.

---

## 3. Bug trovato e corretto: "⇄ Reverse" non spostava le zone ugello

`ReverseNodes_Click` (il pulsante che inverte la linea, punta↔coda) rispecchia correttamente le
posizioni dei nodi e la metadata dei segmenti (nome, peso, Head — vedi task 02) — ma **non
toccava `NozzleZones`**. Le zone restavano agli stessi numeri assoluti di cm, che dopo
l'inversione della geometria puntavano a un **tratto fisico completamente diverso** della linea.

Esempio concreto: linea di 1000cm con una zona "punta pesante" a 900–1000cm. Dopo Reverse (punta e
coda si scambiano), quel materiale doveva restare sul tratto fisico che ora si trova a 0–100cm —
invece restava dichiarato a 900–1000cm, che ora è la coda leggera: il materiale pesante si sarebbe
trovato, silenziosamente, dalla parte sbagliata della lenza.

**Corretto**: `ReverseNodes_Click` ora rispecchia anche `NozzleZones`, con la stessa trasformazione
usata per i nodi — `[Start,End) → [totalLen-End, totalLen-Start)`. File: `Views/MainWindow.xaml.cs`.
Verificato: `dotnet build` pulito, avvio app OK.

---

## 4. Un comportamento da conoscere (non ancora una decisione presa): zone sovrapposte

Se due zone si sovrappongono sullo stesso tratto di linea, sia `ApplyZoneDensities()` sia
`RenderNozzleZones()` non se ne accorgono — usano semplicemente **la prima zona della lista** che
copre quel punto (`FirstOrDefault`), in base all'ordine in cui le zone sono state aggiunte/caricate.
Nessun avviso, nessun blocco alla creazione di una zona sovrapposta a un'altra esistente.

### Confermato con Stefano e corretto — 2026-09-08
Due zone non possono sovrapporsi "in linea teorica" (un tratto fisico di lenza è un solo materiale
alla volta) — quindi una sovrapposizione è sempre un errore da segnalare, non un caso ambiguo da
risolvere in silenzio. Aggiunto `WarnIfZonesOverlap()`: controlla tutte le coppie di zone, e se due
si sovrappongono mostra un avviso con i numeri esatti delle due zone in conflitto (non blocca né
annulla la modifica — i numeri restano visibili in tabella da correggere a mano). Richiamato:
- dopo ogni modifica a Start/End nella griglia Nozzle Zones
- al caricamento di un progetto (un file più vecchio o modificato a mano potrebbe già contenere
  una sovrapposizione)

Non richiamato dopo "⇄ Reverse": rispecchiare le posizioni è un'isometria, non può introdurre una
sovrapposizione che non esisteva già prima.

File: `Views/MainWindow.xaml.cs` (`WarnIfZonesOverlap`, `NozzleZonesGrid_CellEditEnding`,
`LoadProjectFromFile`). Verificato: `dotnet build` pulito, avvio app OK.
