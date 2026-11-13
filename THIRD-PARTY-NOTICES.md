# Drittanbieter-Hinweise

DiskTree selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Die veröffentlichte
`DiskTree.exe` enthält zusätzlich die folgenden Komponenten, die unter eigenen
Lizenzen stehen: MIT (Lizenztext unten) und Apache-2.0 (AWS SDK, Abschnitt weiter
unten). Der jeweilige Urheberrechtshinweis und der Lizenztext müssen bei der
Weitergabe erhalten bleiben.

| Komponente | Version | Lizenz | Urheber |
| --- | --- | --- | --- |
| [PDFsharp](https://github.com/empira/PDFsharp) (PDF-Berichte) | 6.2.4 | MIT | empira Software GmbH |
| .NET-Laufzeit und Klassenbibliothek (nur in `DiskTree.exe`, nicht in `DiskTree-slim.exe`) | 10.x | MIT | .NET Foundation und Mitwirkende, Microsoft |
| Windows Presentation Foundation (WPF) (nur in `DiskTree.exe`) | 10.x | MIT | .NET Foundation und Mitwirkende, Microsoft |
| Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions (Abhängigkeiten von PDFsharp) | 8.0.x | MIT | .NET Foundation und Mitwirkende, Microsoft |
| [AWS SDK for .NET](https://github.com/aws/aws-sdk-net): AWSSDK.Core, AWSSDK.S3, AWSSDK.SSO, AWSSDK.SSOOIDC, AWSSDK.SecurityToken, AWSSDK.Extensions.Numerics (S3-Quellen; SSO/STS für AWS-Profile mit SSO bzw. AssumeRole) | 4.1.0 | Apache-2.0 | Amazon.com, Inc. oder verbundene Unternehmen |

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

## Apache-2.0 (AWS SDK for .NET)

AWS SDK for .NET
Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use
these files except in compliance with the License. You may obtain a copy of the
License at

    https://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

NOTICE: Die NuGet-Pakete 4.1.0 enthalten keine eigene NOTICE-Datei. Das Projekt
führt seine Hinweise in NOTICE.txt des Repositorys
(https://github.com/aws/aws-sdk-net); DiskTree verändert die Bibliotheken nicht.
