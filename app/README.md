# DiametroLineaDesktop

Applicazione Windows WPF che sostituisce la UI web mantenendo il backend ESP32 invariato.
Allineata con il firmware master v0.4.15.

## Struttura progetto
- `Views/MainWindow.xaml` — dashboard principale (Fluent Ribbon + ScottPlot)
- `Views/MainWindow.xaml.cs` — logica UI, grafico, import/export
- `Views/SettingsWindow.xaml` — finestra impostazioni connessione e grafico
- `ViewModels/MainViewModel.cs` — stato UI e parsing messaggi WebSocket
- `Services/BackendClient.cs` — WebSocket client (`ws://<host>:81/`)
- `Services/SettingsService.cs` — lettura/scrittura `appsettings.json`
- `Models/AppSettings.cs` — configurazione backend e grafico
- `appsettings.json` — host, porte, preferenze grafico

## Funzioni implementate
- Connessione/disconnessione WebSocket con auto-reconnect
- Parsing completo di tutti i messaggi JSON del firmware:
  `params`, `speed`, `motor`, `scan_enabled`, `goto_status`, `goto_progress`, punti live
- **Grafico profilo simmetrico** (uguale alla UI web): top = +raggio, bottom = −raggio,
  con linea di mezzeria; le serie importate vengono anch'esse specchiate
- **Annotazione di scansione live**: etichetta dimensionale `Ø X.XX mm` all'ultimo punto
  ricevuto quando la ricezione è attiva (come la web UI v0.4.11+)
- **Hover sulle coordinate**: status bar mostra `Ø X.XXX mm @ X.X cm` sul punto più vicino
- Smoothing EMA desktop-side con alpha regolabile (in aggiunta all'EMA firmware)
- Import CSV confronto: gestisce i formati `Lunghezza cm,Diametro mm` (2 colonne),
  `Dataset,Lunghezza cm,Diametro mm,Display mm` (4 colonne) e multi-dataset
- Export CSV: formato `Lunghezza cm,Diametro mm` compatibile con la web UI
- Export PNG grafico ad alta risoluzione (1400×800)
- Disegno segmenti interattivo con nodi draggabili, undo, salvataggio/caricamento
- Controllo motore: SCAN, STOP, FAST, GOTO, calibrazione zero e offset
- Finestra Impostazioni completa con salvataggio persistente

## Comandi WebSocket inviati al firmware
| Azione | Comando |
|---|---|
| Avvia scansione | `motor scan` + `scan_on` |
| Stop motore | `motor stop` + `scan_off` |
| Attiva ricezione | `scan_on` |
| Disattiva ricezione | `scan_off` |
| Fast stessa dir | `motor fast_s` |
| Fast opposta dir | `motor fast_o` |
| Goto posizione | `goto <cm>` |
| Stato motore | `motor status` |
| Reset tutto | `reset` |
| Zero display | `setdisplayzero` |
| Reset offset | `resetoffset` |
| Imposta offset | `setoffset <mm>` |
| Lettura raw | `readraw` |

## Build & Run
```sh
cd app
dotnet build
dotnet run
```

## Dipendenze NuGet
- `ScottPlot.WPF` 5.0.52
- `Fluent.Ribbon` 10.0.3
- `CommunityToolkit.Mvvm` 8.4.0
