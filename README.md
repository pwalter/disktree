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

## Download

Die fertige Datei steht unter [Releases](../../releases):

| Datei | Beschreibung |
| --- | --- |
| `DiskTree.exe` | Eine einzelne Datei, läuft überall (Windows x64), nichts zu installieren |
| `DiskTree-slim.exe` | Sehr klein, braucht die [.NET 10 Desktop-Laufzeit](https://dotnet.microsoft.com/download/dotnet/10.0) |

## Kommandozeile

```
DiskTree.exe [Pfad] [Optionen]

  Pfad                  Laufwerk oder Ordner, der sofort gescannt wird
  -t, --threads N       Anzahl der Scan-Threads (1-256)
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

Ein Tag `vX.Y.Z` pushen. Die GitHub Action baut dann beide Dateien und legt ein Release mit Prüfsummen an:

```
git tag v1.0.0
git push origin v1.0.0
```
