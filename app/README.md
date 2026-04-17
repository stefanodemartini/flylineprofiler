# DiametroLineaDesktop

Base iniziale di applicazione Windows WPF che sostituisce la UI web mantenendo il backend ESP32 invariato.

## Struttura progetto
- `Views/MainWindow.xaml`: dashboard principale
- `ViewModels/MainViewModel.cs`: stato UI e parsing messaggi
- `Services/BackendClient.cs`: WebSocket client
- `Services/SettingsService.cs`: lettura/scrittura impostazioni JSON
- `Models/AppSettings.cs`: configurazione backend e grafico
- `appsettings.json`: valori configurabili

## Funzioni già predisposte
- Connessione a backend WebSocket ESP32
- Invio comandi principali
- Parsing eventi JSON principali
- Log diagnostico
- Export CSV locale
- Base MVVM leggera

## Da completare subito dopo
1. Finestra `SettingsWindow` con binding e salvataggio.
2. Integrazione reale di ScottPlot WPF al posto del placeholder grafico.
3. Reconnect robusto e gestione profili multipli.
4. Import CSV di confronto.

## Riferimenti tecnici
ScottPlot WPF fornisce una quickstart dedicata e un package NuGet specifico per grafici interattivi in WPF [web:55][web:58][web:61].
L'uso di ClientWebSocket e di un'impostazione MVVM in WPF è coerente con esempi e linee guida diffuse nell'ecosistema .NET desktop [web:51][web:53][web:54].
