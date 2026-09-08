# Task: Nomenclatura dei file generati dalle famiglie

Verificato leggendo `Views/MainWindow.xaml.cs` (`SaveFamilyVariants`, `GenerateLineFamily_Click`,
`GenerateSinkSpeedFamily_Click`, `Compensate_Click`, `SanitizeFileName`, `SaveProjectAs_Click`,
`LoadProjectFromFile`).

---

## 1. Schema di nomenclatura — verificato per ciascun generatore

Ogni file generato prende il nome del progetto sorgente (dal **nome del file su disco**, non dal
campo "Name" interno — vedi §3) più un suffisso specifico:

| Generatore | Suffisso | Esempio file | Esempio "Name" interno |
|---|---|---|---|
| Generate Family (AFFTA) | ` #{classe}` | `MyLine #6.flp` | `MyLine #6` |
| Generate by Sink Speed | ` {ips:0.00}ips` o ` Floating` | `MyLine 3.00ips.flp` | `MyLine 3.00ips` |
| Compensate (task comp redesign) | ` C {ips:0.00}ins` | `MyLine C 3.00ins.flp` | `MyLine C 3.00ins` |

Caratteri non validi per un nome file Windows (`Path.GetInvalidFileNameChars()`) vengono
sostituiti con `_` (`SanitizeFileName`) — corretto e sufficiente per l'uso normale.

**Protezione sovrascrittura**: verificata nel task 07 — un solo avviso cumulativo prima di
sovrascrivere file già esistenti, per entrambi i generatori di famiglia.

---

## 2. Trovato: suffisso incoerente per la stessa unità fisica

**Generate by Sink Speed** usa il suffisso `ips` (inches per second), **Compensate** usa `ins` —
stessa identica unità fisica (in/s), due abbreviazioni diverse nel nome file. Sfogliando una
cartella con entrambi i tipi di file (es. `MyLine 3.00ips.flp` e `MyLine C 3.00ins.flp`) l'unità
non è immediatamente riconoscibile come la stessa cosa. Non è un errore funzionale — è
un'incoerenza cosmetica tra due funzioni scritte in momenti diversi della stessa sessione.

### Corretto — 2026-09-08
Uniformato su `ips` ovunque (era già la scelta con più precedenti — usata da Generate by Sink
Speed). Cambiato solo il suffisso testuale in `Compensate_Click`: `MyLine C 3.00ins.flp` →
`MyLine C 3.00ips.flp`. Le variabili interne (`targetIns`, `_compTargetSpeedIns`, ecc.) non sono
state toccate — sono nomi di codice, non testo visibile.

---

## 3. Trovato: il nome del file generato può disallinearsi dal nome interno del progetto

I file generati prendono il nome dal **file su disco** (`baseName =
Path.GetFileNameWithoutExtension(_currentProjectPath)`), ma il campo **"Name" interno** del
progetto sorgente (`_projectName`, mostrato in titolo finestra e scritto dentro ogni file
generato) segue un percorso diverso:
- Viene sincronizzato col nome del file **solo quando salvi con "Save As"** dall'app
  (`_projectName = Path.GetFileNameWithoutExtension(dlg.FileName)`)
- Al **caricamento** di un file, `_projectName` viene preso dal campo `Name` scritto *dentro* il
  JSON al momento dell'ultimo salvataggio (`_projectName = project.Name`) — **mai confrontato o
  riallineato** con il nome reale del file che stai aprendo in quel momento

**Conseguenza pratica**: se rinomini un file `.flp` da **Esplora File di Windows** (un'azione del
tutto normale, non richiede l'app) invece che da "Save As" dentro l'app, il file sul disco ha il
nuovo nome, ma il campo `Name` scritto al suo interno resta quello vecchio. Alla riapertura,
`_currentProjectPath` punta al file col nome nuovo, ma `_projectName` mostra ancora quello vecchio
— e ogni famiglia generata da quel punto in poi avrà **nome del file corretto** (basato sul file
reale) ma **campo "Name" interno vecchio** (scritto dentro ogni file generato, e mostrato in
titolo finestra quando li apri).

Non è un errore di calcolo — è un disallineamento cosmetico che si nota solo se rinomini file
manualmente fuori dall'app.

### Corretto — 2026-09-08
`LoadProjectFromFile` non legge più `_projectName` dal campo `Name` scritto dentro il JSON —
lo deriva sempre dal **nome reale del file appena aperto** (`Path.GetFileNameWithoutExtension(path)`),
esattamente come già fa "Save As". Il nome del file su disco è ora sempre la fonte di verità,
qualunque sia stato l'ultimo salvataggio interno — un file rinominato da Esplora Risorse si
allinea automaticamente alla riapertura, senza bisogno di un confronto/avviso esplicito. Aggiornato
anche il messaggio di stato al caricamento, che ora mostra lo stesso nome coerente.

File: `Views/MainWindow.xaml.cs` (`Compensate_Click`, `LoadProjectFromFile`). Verificato:
`dotnet build` pulito, avvio app OK.
