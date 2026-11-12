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
- **Export** als CSV (Excel-tauglich): Baum oder größte Dateien.
- **Scans speichern und öffnen** (`.disktree`) und **vergleichen**: welche Ordner sind gewachsen, geschrumpft, neu oder entfernt.

## Download

Die fertige Datei steht unter [Releases](../../releases):

| Datei | Beschreibung |
| --- | --- |
| `DiskTree.exe` | Eine einzelne Datei, läuft überall (Windows x64), nichts zu installieren |
| `DiskTree-slim.exe` | Sehr klein, braucht die [.NET 10 Desktop-Laufzeit](https://dotnet.microsoft.com/download/dotnet/10.0) |

## Kommandozeile

```
DiskTree.exe [Pfad] [Optionen]

  Pfad                  Laufwerk oder Ordner, der sofort gescannt wird (mehrere Pfade möglich)
  -t, --threads N       Anzahl der Scan-Threads (1-256)
  -x, --exclude MUSTER  Ordner beim Scan überspringen (mehrfach möglich)
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

## Release veröffentlichen

Jeder Push auf `main` baut beide Dateien und legt automatisch ein Release mit Prüfsummen an. Die Version ist das Datum plus laufende Nummer des Tages (`2026.11.12.1`, `2026.11.12.2`, …, deutsche Zeit).

Ein Release mit eigener Nummer entsteht auch durch ein Tag `vX.Y.Z`:

```
git tag v1.0.0
git push origin v1.0.0
```
