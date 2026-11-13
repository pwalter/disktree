<#
  Legt synthetische Demo-Daten fuer die Screenshots der Landingpage an.
  Nichts davon ist echt: Namen und Inhalte sind erfunden, grosse Dateien sind NTFS-Sparse-Dateien
  (sie melden die grosse Laenge, belegen aber kaum Platz auf der Platte).

  Aufruf:   powershell -ExecutionPolicy Bypass -File make-demo-data.ps1 [-Root C:\DiskTreeDemo] [-Phase 1|2]
            Phase 1 = Ausgangszustand (zuerst scannen und als Scan speichern),
            Phase 2 = veraenderter Zustand (Ordner gewachsen / geschrumpft / neu / entfernt) fuer den Vergleich.
  Aufraeumen: Remove-Item C:\DiskTreeDemo -Recurse -Force
  Hinweis:  Sparse-Dateien gleicher Groesse sind fuer die Duplikate-Suche identisch (nur Nullen);
            deshalb haben alle grossen Dateien eigene Groessen, nur die gewollten Duplikate teilen sich eine.
#>
param([string]$Root = 'C:\DiskTreeDemo', [int]$Phase = 1)
$ErrorActionPreference = 'Stop'
$rnd = [System.Random]::new(4711)
$now = Get-Date

function Stamp([string]$p, [double]$daysMin, [double]$daysMax) {
    $d = [Math]::Max(0.2, $daysMin + $rnd.NextDouble() * ($daysMax - $daysMin))
    $w = $now.AddDays(-$d)
    $c = $w.AddDays(-($rnd.NextDouble() * 120))
    $a = $w.AddDays($rnd.NextDouble() * [Math]::Min($d, 200))
    if ($a -gt $now) { $a = $now }
    $i = Get-Item -LiteralPath $p -Force
    $i.CreationTime = $c; $i.LastWriteTime = $w; $i.LastAccessTime = $a
}

# kleine Datei mit zufaelligem (oder vorgegebenem) Inhalt oder grosse Sparse-Datei
function F([string]$rel, [long]$size, [double]$dMin, [double]$dMax, [byte[]]$content = $null) {
    $p = Join-Path $Root $rel
    [void][IO.Directory]::CreateDirectory((Split-Path $p))
    if ($size -le 256KB) {
        if (-not $content) { $content = New-Object byte[] $size; $rnd.NextBytes($content) }
        [IO.File]::WriteAllBytes($p, $content)
    } else {
        $fs = [IO.File]::Create($p); $fs.Close()
        & fsutil sparse setflag $p | Out-Null
        $fs = [IO.File]::Open($p, 'Open', 'Write'); $fs.SetLength($size); $fs.Close()
    }
    Stamp $p $dMin $dMax
}
function Rb([int]$n) { $b = New-Object byte[] $n; $rnd.NextBytes($b); ,$b }
function Sz([double]$mb) { [long]($mb * 1MB) + $rnd.Next(1000, 99999) }   # eigene, unterschiedliche Groessen

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
[void][IO.Directory]::CreateDirectory($Root)

# ---------- Projekte ----------
$web = 'Projekte\Webshop'
foreach ($n in 1..40) { F "$web\src\Modul$n.cs" ($rnd.Next(2000, 60000)) 0 240 }
foreach ($n in 1..12) { F "$web\src\assets\style$n.css" ($rnd.Next(2000, 30000)) 0 300 }
foreach ($n in 1..25) { F "$web\bin\Release\Lib$n.dll" ($rnd.Next(30000, 250000)) 0 60 }
F "$web\bin\Release\Webshop.dll" (Sz 24) 0 3
F "$web\obj\Webshop.pdb" (Sz 9) 0 3
F "$web\node_modules\bundle.js" (Sz 48) 1 40
F "$web\node_modules\vendor.map" (Sz 33) 1 40
F "$web\db\shop-dev.mdf" (Sz 2300) 0 4
F "$web\db\shop-dev_log.ldf" (Sz 780) 0 4
F "$web\db\shop-2025-10.bak" (Sz 6200) 20 40
F "$web\README.md" 4200 0 30
$kp = 'Projekte\Kundenportal'
foreach ($n in 1..30) { F "$kp\src\Seite$n.js" ($rnd.Next(1500, 40000)) 0 400 }
F "$kp\Kundenportal.sln" 3000 0 200
F "$kp\dist\app.js" (Sz 7.4) 2 50
F "$kp\export\kundendaten.csv" (Sz 1850) 40 120
F "$kp\logs\portal.log" (Sz 380) 0 2
F "$kp\logs\portal-alt.log" (Sz 910) 30 90
$mob = 'Projekte\Mobile-App'
foreach ($n in 1..20) { F "$mob\src\View$n.xaml" ($rnd.Next(1000, 20000)) 0 500 }
F "$mob\build\app-release.apk" (Sz 68) 5 70
F "$mob\build\app-debug.apk" (Sz 91) 5 70
F "$mob\assets\Design.psd" (Sz 540) 90 400
$da = 'Projekte\Datenanalyse'
F "$da\rohdaten-2024.csv" (Sz 4300) 200 330
F "$da\rohdaten-2025.csv" (Sz 3100) 20 90
F "$da\auswertung.xlsx" (Sz 18) 1 20
F "$da\modell.pkl" (Sz 740) 10 60

# ---------- Fotos ----------
$photoYears = @(
  @{ y = 2019; d = 2480; n = 90;  sub = @('Italien-Urlaub', 'Familienfeier') },
  @{ y = 2020; d = 2120; n = 60;  sub = @('Garten', 'Weihnachten') },
  @{ y = 2021; d = 1760; n = 110; sub = @('Hochzeit-Lena', 'Wanderung-Alpen') },
  @{ y = 2022; d = 1390; n = 120; sub = @('Kroatien', 'Geburtstag') },
  @{ y = 2023; d = 1020; n = 150; sub = @('Skandinavien', 'Sommerfest') },
  @{ y = 2024; d = 660;  n = 130; sub = @('Portugal', 'Herbstausflug') },
  @{ y = 2025; d = 290;  n = 110; sub = @('Berlin', 'Wochenende') }
)
foreach ($py in $photoYears) {
    $per = [int]($py.n / $py.sub.Count)
    foreach ($s in $py.sub) {
        foreach ($i in 1..$per) {
            $raw = ($i % 6 -eq 0)
            $name = 'IMG_{0}_{1}.{2}' -f (1000 + $rnd.Next(0, 8999)), $i, $(if ($raw) { 'cr2' } else { 'jpg' })
            $size = if ($raw) { Sz ($rnd.Next(22, 34)) } else { Sz ($rnd.NextDouble() * 6 + 3.5) }
            F "Fotos\$($py.y)\$s\$name" $size ($py.d - 330) ($py.d)
        }
    }
}
F 'Fotos\Panorama-Alpen.psd' (Sz 780) 700 900

# ---------- Videos ----------
F 'Videos\Urlaub-Italien-2023.mp4' 6761234567 900 1000     # Duplikat (s.u.)
F 'Videos\Familie\Geburtstag-2024.mp4' (Sz 3400) 400 600
F 'Videos\Familie\Weihnachten-2023.mp4' (Sz 2100) 700 800
F 'Videos\Familie\Hochzeit-Lena.mkv' (Sz 11800) 1700 1800
F 'Videos\Tutorials\PowerShell-Grundlagen.mp4' (Sz 1450) 300 420
F 'Videos\Tutorials\Docker-Kurs-Teil1.mkv' (Sz 2850) 250 400
F 'Videos\Tutorials\Docker-Kurs-Teil2.mkv' (Sz 3320) 250 400
F 'Videos\Schnitt\Reisefilm-Rohmaterial.mov' (Sz 18600) 120 200
F 'Videos\Schnitt\Reisefilm-Final.mp4' (Sz 4700) 60 100
F 'Videos\Schnitt\Vorschau.webm' (Sz 160) 60 100

# ---------- Musik ----------
foreach ($n in 1..48) { F ("Musik\Album-{0}\Titel-{1:00}.mp3" -f (1 + ($n % 6)), $n) (Sz ($rnd.NextDouble() * 5 + 3)) 400 2600 }
foreach ($n in 1..10) { F ("Musik\Lossless\Stueck-{0:00}.flac" -f $n) (Sz ($rnd.NextDouble() * 30 + 25)) 900 2000 }

# ---------- Backups ----------
F 'Backups\Server\ERP-Voll-2025-05.bak' (Sz 14200) 170 180
F 'Backups\Server\ERP-Voll-2025-08.bak' (Sz 15100) 80 90
F 'Backups\Server\ERP-Voll-2025-11.bak' (Sz 15900) 1 6
F 'Backups\Server\ERP-Diff-2025-11-10.bak' (Sz 1800) 2 4
F 'Backups\Server\ERP-Log-2025-11-12.trn' (Sz 620) 0 2
F 'Backups\Laptop\Laptop-Image-2025-06.vhdx' (Sz 41000) 150 160
F 'Backups\Laptop\Laptop-Image-2025-09.vhdx' (Sz 43500) 50 60
F 'Backups\Dokumente-Sicherung-2025.zip' (Sz 4900) 25 35
F 'Backups\Fotos-Sicherung-2024.7z' (Sz 9800) 330 380

# ---------- Dokumente ----------
$vertrag = Rb (180KB)    # fuer Duplikate
$rechnung = Rb (95KB)
foreach ($y in 2019..2025) {
    foreach ($m in 1..4) { F ("Dokumente\Rechnungen\$y\Rechnung-$y-{0:00}.pdf" -f ($m * 3)) ($rnd.Next(40000, 220000)) ((2026 - $y) * 365 - 330) ((2026 - $y) * 365 - 30) }
}
F 'Dokumente\Vertraege\Mietvertrag.pdf' 180KB 1500 1500 $vertrag
F 'Dokumente\Vertraege\Versicherung-Hausrat.pdf' ($rnd.Next(90000, 200000)) 600 700
F 'Dokumente\Vertraege\Arbeitsvertrag.docx' ($rnd.Next(30000, 90000)) 2000 2100
F 'Dokumente\Steuer\Steuererklaerung-2024.pdf' 210KB 200 220
F 'Dokumente\Steuer\Belege-2024.xlsx' (Sz 3.4) 190 220
foreach ($n in 1..8) { F "Dokumente\Praesentationen\Vortrag-$n.pptx" (Sz ($rnd.NextDouble() * 40 + 6)) 30 900 }
foreach ($n in 1..10) { F "Dokumente\Tabellen\Budget-$n.xlsx" (Sz ($rnd.NextDouble() * 5 + 0.4)) 5 700 }
foreach ($n in 1..9) { F "Dokumente\Notizen\Notiz-$n.docx" ($rnd.Next(20000, 140000)) 0 400 }
F 'Dokumente\Rechnungen\Rechnung-Archiv-Kopie.pdf' 95KB 900 900 $rechnung
F 'Dokumente\Rechnungen\2025\Rechnung-2025-Original.pdf' 95KB 120 120 $rechnung

# ---------- Downloads ----------
F 'Downloads\Windows11.iso' (Sz 5400) 150 160
F 'Downloads\Ubuntu-24.04.iso' (Sz 6100) 250 260
F 'Downloads\VSCodeSetup.exe' (Sz 98) 40 45
F 'Downloads\Treiber-Drucker.msi' (Sz 142) 380 400
F 'Downloads\Setup-Tool.exe' (Sz 56) 8 10
F 'Downloads\Beispiele.zip' (Sz 380) 12 14
F 'Downloads\Urlaub-Auswahl.zip' (Sz 1450) 25 30
F 'Downloads\Installer.tmp' (Sz 880) 0 1
F 'Downloads\Vortrag-Kopie.pptx' (Sz 14.2) 3 4
foreach ($n in 1..14) { F "Downloads\Scans\Scan-$n.pdf" ($rnd.Next(120000, 250000)) 1 600 }
# gewollte echte Duplikate (kleine Dateien mit gleichem Inhalt)
$setup = Rb (6MB)
F 'Downloads\Installer\Setup-Foto-Viewer.exe' 6MB 300 300 $setup
F 'Archiv\Software\Setup-Foto-Viewer.exe' 6MB 1100 1100 $setup
F 'Dokumente\Vertraege\Kopie von Mietvertrag.pdf' 180KB 20 20 $vertrag

# ---------- Archiv ----------
F 'Archiv\Mails\Postfach-2018.pst' (Sz 14300) 2100 2200
F 'Archiv\Mails\Postfach-2020.pst' (Sz 9800) 1400 1500
F 'Archiv\Altprojekte\Projekte-2017.zip' (Sz 3800) 2300 2350
F 'Archiv\Altprojekte\Projekte-2019.zip' (Sz 5200) 2000 2100
F 'Archiv\Fotos-2016.7z' (Sz 17400) 2250 2300
F 'Archiv\Hochzeits-Video-roh.avi' (Sz 12900) 1850 1900
F 'Archiv\Videos-Kopie\Urlaub-Italien-2023.mp4' 6761234567 400 400   # Duplikat zu Videos\...
F 'Archiv\Datenbank\kasse-2019.mdf' (Sz 1900) 2000 2050
foreach ($n in 1..16) { F "Archiv\Alt\Dokument-$n.doc" ($rnd.Next(20000, 120000)) 1900 2300 }

# ---------- Phase 2: veraenderter Zustand ----------
if ($Phase -ge 2) {
    Remove-Item (Join-Path $Root 'Downloads\Windows11.iso'), (Join-Path $Root 'Downloads\Installer.tmp') -Force
    Remove-Item (Join-Path $Root 'Archiv\Altprojekte') -Recurse -Force
    Remove-Item (Join-Path $Root 'Backups\Laptop\Laptop-Image-2025-06.vhdx') -Force
    F 'Backups\Laptop\Laptop-Image-2025-11.vhdx' (Sz 45200) 0 1
    F 'Backups\Server\ERP-Voll-2025-12.bak' (Sz 16500) 0 1
    F 'Videos\Schnitt\Reisefilm-Endfassung.mp4' (Sz 5900) 0 2
    F 'Videos\Neu\Silvester-Clips.mov' (Sz 7300) 0 1
    foreach ($n in 1..60) { F "Fotos\2025\Wochenende\IMG_neu_$n.jpg" (Sz ($rnd.NextDouble() * 6 + 3.5)) 0 6 }
    F 'Projekte\Webshop\db\shop-2025-12.bak' (Sz 7400) 0 1
    F 'Projekte\Webshop\node_modules\bundle.js' (Sz 61) 0 1
    F 'Dokumente\Rechnungen\Rechnung-2026-01.pdf' 150KB 0 1
}
"Fertig: $Root"
