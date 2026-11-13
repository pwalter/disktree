# Drittanbieter-Hinweise

DiskTree selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Die veröffentlichte
`DiskTree.exe` enthält zusätzlich die folgenden Komponenten, die unter eigenen
Lizenzen stehen. Alle genannten Lizenzen sind MIT-Lizenzen; der jeweilige
Urheberrechtshinweis und der Lizenztext unten müssen bei der Weitergabe erhalten
bleiben.

| Komponente | Version | Lizenz | Urheber |
| --- | --- | --- | --- |
| [PDFsharp](https://github.com/empira/PDFsharp) (PDF-Berichte) | 6.2.4 | MIT | empira Software GmbH |
| .NET-Laufzeit und Klassenbibliothek (nur in `DiskTree.exe`, nicht in `DiskTree-slim.exe`) | 10.x | MIT | .NET Foundation und Mitwirkende, Microsoft |
| Windows Presentation Foundation (WPF) (nur in `DiskTree.exe`) | 10.x | MIT | .NET Foundation und Mitwirkende, Microsoft |
| Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions (Abhängigkeiten von PDFsharp) | 8.0.x | MIT | .NET Foundation und Mitwirkende, Microsoft |

Nur für Entwicklung und Tests (nicht Teil der veröffentlichten Dateien): xUnit
(Apache-2.0), Microsoft.NET.Test.Sdk, Microsoft.CodeCoverage und
Newtonsoft.Json (MIT).

Hinweis zu PDFsharp: Ab Version 6.5 steht PDFsharp unter einer anderen Lizenz
(AGPL bzw. kommerziell mit Lizenzschlüssel). DiskTree verwendet bewusst die
MIT-lizenzierte Version 6.2.4. Vor einer Aktualisierung die Lizenz prüfen.

Die Berichte verwenden die auf dem Rechner installierte Schrift Arial; es wird
keine Schriftdatei mitgeliefert.

## MIT-Lizenztext (gilt für die oben genannten Komponenten)

Copyright (c) PDFsharp: empira Software GmbH
Copyright (c) .NET Foundation und Mitwirkende; Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
