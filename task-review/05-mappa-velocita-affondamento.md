# Task: Velocità di affondamento teorica e mappa nella main window

Verificato leggendo `Services/SinkingSpeedCalc.cs` e `Views/MainWindow.xaml.cs`
(`UpdateSinkingSpeeds`, `RenderOriginalSpeedMap`, `ShowSinkMap_Click`).

---

## 1. Conferma: l'algoritmo è quello giusto

`SinkingSpeedCalc.cs` è esplicitamente commentato come *"Ported from VBA FlyLineSinkSpeed by the
project author"* — è "il nostro algoritmo" a cui ti riferisci, non qualcosa reinventato per
quest'app. Modello fisico: bilancio di forze tra gravità/spinta e resistenza del cilindro
(Cd = 1 + 10/Re^(2/3)), risolto per bisezione. Il calcolo è **automatico**: `UpdateSinkingSpeeds()`
gira ogni volta che cambiano densità, geometria, temperatura o tipo di acqua — nessun bottone da
premere per aggiornare i numeri.

La mappa esiste ed è visualizzabile: toggle **Sink Map** nella toolbar (Sinking Tools, nascosto
per linee floating). Colora il profilo NC disegnato con un gradiente blu→rosso.

---

## 2. Un punto tecnico da conoscere: non è UN SOLO algoritmo, sono DUE modelli diversi

Ho trovato che il codice usa **due funzioni fisiche differenti** per due visualizzazioni diverse
della "velocità di affondamento", che rispondono a due domande fisicamente diverse:

### a) Colonna "Sink (in/s)" nella tabella Segments → `TaperedSegmentSinkSpeed`
Tratta **l'intero segmento come un unico corpo rigido**: integra tutte le sue sotto-fette (12cm)
in un unico bilancio di forze e trova **una sola velocità di equilibrio** per tutto il segmento.
Fisicamente corretto per "come affonda questo segmento, isolato da solo" — ma non tiene conto dei
segmenti prima e dopo (che in realtà sono fisicamente attaccati e si "negoziano" una velocità
comune, esattamente come fa `RigidBodySinkSpeed` per una zona a densità propria).

### b) Mappa colori sul grafico (Sink Map) → `CylinderSinkSpeed`
Tratta **ogni fetta da 12cm come un cilindro isolato e infinito**, al suo diametro locale in quel
punto — senza nemmeno il vincolo "fa parte dello stesso segmento". È un indicatore
**puramente locale e relativo**: mostra dove il profilo è localmente più spesso/denso (quindi
"vorrebbe" affondare più veloce se potesse muoversi per conto suo), non la velocità reale di quel
punto quando è fisicamente vincolato al resto della linea.

**Conseguenza pratica**: guardando lo stesso segmento, la colonna Sink mostra un numero singolo
(es. "1.850 in/s" per tutto il segmento), mentre la mappa sopra quello stesso segmento può mostrare
un gradiente di colore (se il segmento è un taper, non un cilindro) — sono **due quantità diverse**,
non un numero e la sua rappresentazione grafica. Non è un errore: sono scelte di modello legittime
per due scopi diversi (il numero in tabella = comportamento fisico reale del segmento; la mappa =
diagnostica visiva di "dove il materiale è localmente più denso/spesso lungo tutto il profilo").
Il MANUALE.md lo dice correttamente ("colore = velocità locale relativa"), ma vale la pena
confermare che è la lettura che ti aspetti — altrimenti si presta a essere letta come "il colore
in un punto = la velocità che vedo nella tabella per quel segmento", il che non è vero.

---

## 3. Bug trovato: la mappa non si aggiorna cambiando acqua/temperatura

`WaterIsSalt` e `WaterTempC` (setter) chiamano `UpdateSinkingSpeeds()` (che aggiorna correttamente
il numero nella colonna Sink, perché è un dato bindato) ma **non chiamano mai `RefreshPlot()`**.
Risultato: se hai la Sink Map attiva e cambi acqua dolce/salata o la temperatura, **la colonna
numerica si aggiorna subito, ma i colori sulla mappa restano quelli vecchi** finché non succede
qualcos'altro che ridisegna il grafico (aggiungere/spostare un nodo, aprire/chiudere un pannello,
ecc.). Con la mappa spenta il problema non si vede — si manifesta solo se la tieni accesa mentre
cambi le condizioni d'acqua.

---

## 4. Chiarito con Stefano (2026-09-08) — CHIUSO

**§2 — quale modello deve vincere**: risposta netta — "quello che vedo in grafico è quello che
conta". Il modello locale isolato (quello della mappa) è quindi promosso a riferimento anche per
la tabella, non il contrario.

### Modifica implementata

La colonna **Sink** dei segmenti a materiale uniforme non usa più `TaperedSegmentSinkSpeed`
(corpo rigido, un solo numero per l'intero segmento) — usa lo stesso `CylinderSinkSpeed` (cilindro
isolato locale) già usato dalla mappa, valutato ai **due estremi** del segmento (Ø iniziale e Ø
finale):
- **Cilindro** → un solo valore (i due estremi coincidono)
- **Tronco di cono** → range direzionale, es. `1.10 → 1.95 in/s` — esattamente i due estremi del
  gradiente colorato che la mappa disegna su quel segmento

**Non toccato**: i segmenti con zona a densità propria (materiale realmente diverso, fisicamente
un unico pezzo) continuano a usare il modello a corpo rigido (`RigidBodySinkSpeed`, via
`CompensatedTargetSpeedMs`) — lì è il modello corretto, non quello della mappa.

**Effetto collaterale, in meglio**: il range min/max offerto dallo slider del dialogo Compensate
(`_compMinSpeedMs`/`_compMaxSpeedMs`) ora considera gli estremi Start **e** End di ogni segmento
uniforme (prima era un solo numero per segmento) — range più preciso, coerente con la vera
variazione locale visibile sul grafico.

**§3 — mappa non si aggiornava cambiando acqua/temperatura** → **corretto insieme**, perché ora
che tabella e mappa condividono lo stesso identico modello, lasciarle disallineate su un cambio
di condizioni d'acqua sarebbe stato ancora più evidente. Aggiunto `RefreshPlot()` nei setter di
`WaterIsSalt`/`WaterTempC`, solo quando la Sink Map è effettivamente attiva (nessun ridisegno
inutile se è spenta).

File: `Models/ProjectSegment.cs` (`SinkSpeedStartMs`, `SinkSpeedEndMs`, `SinkSpeedText`),
`Views/MainWindow.xaml.cs` (`UpdateSinkingSpeeds`, `WaterIsSalt`, `WaterTempC`). Verificato:
`dotnet build` pulito (due volte), avvio app OK dopo entrambe le modifiche.
