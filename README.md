# DiskTree

Speicherplatz-Analyse für Windows, ähnlich wie TreeSize: Pfad oder Laufwerk angeben, DiskTree scannt mit mehreren Threads und baut den Ordnerbaum dabei live auf.

## Funktionen

- **Live-Baum:** Ordner erscheinen sofort, die Größe wächst mit (Spinner und „…“, bis sie feststeht). Pro Ebene sind die größten Ordner oben.
- **Laufwerk als Wurzel:** Anteile beziehen sich auf die Kapazität des Laufwerks, dazu „Freier Speicher“ und „Nicht erfasst“.
- **Dateityp-Diagramm:** Word, Excel, MSSQL-Datenbank/-Backup, Bilder, Videos, Archive usw. für den markierten Ordner.
- **Größte Dateien:** Die 200 größten Dateien des Scans.
- **Windows-Kontextmenü** (Öffnen, Öffnen mit, Löschen, Eigenschaften …) plus „Ab hier neu scannen“ und „Übergeordneten Ordner öffnen“.
- **Neuscan an Ort und Stelle:** Ein Ordner wird neu gescannt, alle Ordner darüber werden korrigiert.
- Optionaler Neustart als Administrator, damit auch geschützte Ordner gelesen werden.
- **Dunkelmodus:** folgt der Windows-Einstellung, per Schalter in der Toolbar auch fest Hell oder Dunkel.
- **Mehrere Pfade** (Semikolon-getrennt, per Drag & Drop oder mehrere Argumente) werden gemeinsam gescannt; UNC-Pfade (`\server\share`) funktionieren ebenfalls.
- **Treemap:** Unterordner als Rechtecke nach Größe, Klick markiert im Baum, Doppelklick taucht ein.
- **Alter:** Auswertung nach „zuletzt geändert“ (Diagramm und Spalte „Älter als …“ im Baum, Schwellwert einstellbar).
- **Suche** (Strg+F): Ordner und Dateien nach Name, Mindestgröße und Endung.
- **Duplikate finden:** gleiche Dateien per Größe, Teil-Hash und SHA-256, mit „verschwendetem Platz“.
- **Besitzer und Rechte:** Besitzer und ACL des markierten Ordners, Speicher pro Besitzer (auf Knopfdruck).
- **Ausschlüsse:** Ordner (Name, Muster wie `*.tmp` oder Pfad) vom Scan ausnehmen, auch per `--exclude`.
- **Self-Update:** DiskTree sucht beim Start nach einer neuen Version (unten rechts in der Statusleiste), lädt sie von den GitHub-Releases, prüft die SHA-256-Prüfsumme und startet neu. Abschaltbar per Rechtsklick auf die Versionsanzeige oder mit `--no-update-check`.
- **Export** als CSV (Excel-tauglich), außerdem **Bericht** als HTML oder PDF (größte Ordner, Dateitypen, Alter, größte Dateien).
- **Aufräumen:** mehrere Dateien markieren (Größte Dateien, Suche, Duplikate) und verschieben, in ZIP archivieren, umbenennen (F2, auch per Muster) oder in den Papierkorb legen – immer mit Bestätigung.
- **Filter** nach Zugriffs-, Erstell- und Änderungsdatum sowie Dateiattributen (versteckt, System, komprimiert, Cloud-Platzhalter …); der Alters-Tab kann nach jedem der drei Zeitstempel auswerten.
- **Explorer-Kontextmenü** „Mit DiskTree analysieren“ (Rechtsklick auf die Versionsanzeige schaltet es ein) und **Portable-Modus** (`DiskTree.portable` neben die exe legen oder `--portable`).
- **Scans speichern und öffnen** (`.disktree`) und **vergleichen**: welche Ordner sind gewachsen, geschrumpft, neu oder entfernt.

## Download

Die fertige Datei steht unter [Releases](../../releases):

| Datei | Beschreibung |
| --- | --- |
| `DiskTree.exe` | Eine einzelne Datei, läuft überall (Windows x64), nichts zu installieren |
| `DiskTree-slim.exe` | Sehr klein, braucht die [.NET 10 Desktop-Laufzeit](https://dotnet.microsoft.com/download/dotnet/10.0) |

Die Dateien sind nicht code-signiert. Prüfen lässt sich ein Download auf zwei Wegen:

- **Prüfsumme:** `SHA256SUMS.txt` aus demselben Release mit `Get-FileHash .\DiskTree.exe` vergleichen.
- **Herkunft:** Jede exe hat eine Herkunftsbestätigung (Build-Provenance), die belegt, dass sie von GitHub Actions aus diesem Repository gebaut wurde. Prüfen mit der [GitHub CLI](https://cli.github.com/):

  ```
  gh attestation verify DiskTree.exe --repo pwalter/disktree
  ```

## Kommandozeile

```
DiskTree.exe [Pfad] [Optionen]

  Pfad                  Laufwerk oder Ordner, der sofort gescannt wird (mehrere Pfade möglich)
  -t, --threads N       Anzahl der Scan-Threads (1-256)
  -x, --exclude MUSTER  Ordner beim Scan überspringen (mehrfach möglich)
      --portable        Einstellungen neben der exe speichern
      --no-update-check Beim Start nicht nach einer neuen Version suchen
      --admin           Direkt als Administrator neu starten
      --no-admin-prompt Die Frage nach Administratorrechten überspringen
  -h, --help, /?        Hilfe
```

## Selbst bauen

Voraussetzung: .NET 10 SDK.

```
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File publish.ps1   # erzeugt dist\DiskTree.exe
```

## Aufbau

Alle Quelldateien liegen unter dem Namespace `DiskTree`, je Ordner ein Unter-Namespace (`DiskTree.Core`, `DiskTree.Features.Search` …).

| Ordner | Inhalt |
| --- | --- |
| `Core/` | Scanner, Knoten (`Node`), Ausschlussregeln, Pfadlisten, Dateityp- und Alters-Klassen, Größenformat |
| `Core/Snapshots/` | Gespeicherte Scans (`.disktree`): Lesen und Schreiben, Vergleich zweier Scans |
| `Features/Search/` | Suche in Baum und Dateisystem samt Datums- und Attributfiltern |
| `Features/Duplicates/` | Duplikate-Suche (Größe, Teil-Hash, SHA-256) und Duplikate-Tab |
| `Features/Owner/` | Besitzer und Rechte, Speicher pro Besitzer |
| `Features/Treemap/` | Treemap-Layout und Zeichnung |
| `Features/Reports/` | CSV-Export sowie HTML- und PDF-Bericht |
| `Features/FileOperations/` | Aufräumen: verschieben, ZIP, umbenennen, Papierkorb |
| `Features/Update/` | Self-Update über GitHub-Releases |
| `Features/Explorer/` | Eintrag im Explorer-Kontextmenü |
| `Infrastructure/` | Einstellungen, Portable-Modus, Kommandozeile, Shell-Kontextmenü, Administrator-Neustart, Explorer öffnen |
| `Ui/` | Hauptfenster (`MainWindow*`), Tabs, Dialoge, Diagramm, Theme, ViewModels (`NodeVM`), Hilfsklassen (`Dialogs`, `BrushFactory`, `VisualTreeHelpers`) |
| `tests/` | xUnit-Tests (`dotnet test tests/DiskTree.Tests`) und `ui-smoke.ps1` |

Im Wurzelordner bleiben `App.xaml`, `DiskTree.csproj`, `app.ico` und `publish.ps1`. Feature-Ordner enthalten vorerst Logik und die zugehörigen Ansichten (Panels) gemeinsam.

## Release veröffentlichen

Jeder Push auf `main` baut beide Dateien und legt automatisch ein Release mit Prüfsummen an. Die Version ist das Datum plus laufende Nummer des Tages (`2026.11.12.1`, `2026.11.12.2`, …, deutsche Zeit).

Ein Release mit eigener Nummer entsteht auch durch ein Tag `vX.Y.Z`:

```
git tag v1.0.0
git push origin v1.0.0
```

## Lizenz

DiskTree steht unter der [MIT-Lizenz](LICENSE). Hinweise zu verwendeten Komponenten stehen in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
