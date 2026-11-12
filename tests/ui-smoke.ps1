<#
.SYNOPSIS
  Start-Test der gebauten DiskTree-App: Fenster erscheint, App laeuft ein paar Sekunden ohne Absturz, wird beendet.

.DESCRIPTION
  Startet DiskTree.exe mit einem kleinen Testordner (--no-admin-prompt --no-update-check --portable), wartet auf das
  Hauptfenster, wartet danach -Seconds Sekunden und prueft, dass der Prozess noch laeuft. Beendet die App am Ende.
  --portable haelt die Einstellungen des Smoke-Tests aus dem Profil des Benutzers heraus (settings.json landet neben der exe).
  Mit -Screenshot wird das Fenster nach tests\out\ fotografiert (Ordner ist in .gitignore).

  Exit-Code 0 = ok, 1 = Fehler (kein Fenster, Absturz, Exe fehlt).

.EXAMPLE
  dotnet build DiskTree.csproj
  pwsh tests\ui-smoke.ps1 -Screenshot
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 6,
    [int]$WindowTimeoutSeconds = 30,
    [switch]$Screenshot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo 'bin\Debug\net10.0-windows\DiskTree.exe' }
if (-not (Test-Path $Exe)) { Write-Error "DiskTree.exe nicht gefunden: $Exe (erst 'dotnet build DiskTree.csproj' ausfuehren)"; exit 1 }

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SmokeWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@

# small test folder with a few files
$data = Join-Path ([IO.Path]::GetTempPath()) ('disktree-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $data 'sub\deeper') -Force | Out-Null
foreach ($f in 'a.docx', 'sub\b.pdf', 'sub\deeper\c.bin') {
    $fs = [IO.File]::Create((Join-Path $data $f)); $fs.SetLength(200KB); $fs.Dispose()
}

$proc = $null
$ok = $false
try {
    $proc = Start-Process -FilePath $Exe -ArgumentList @('--no-admin-prompt', '--no-update-check', '--portable', "`"$data`"") -PassThru
    Write-Host "Gestartet: PID $($proc.Id)"

    # 1. window appears
    $deadline = (Get-Date).AddSeconds($WindowTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "Prozess beendete sich vor dem Fenster (Exit-Code $($proc.ExitCode))." }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero -and $proc.MainWindowTitle) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "Kein Hauptfenster innerhalb von $WindowTimeoutSeconds s." }
    Write-Host "Fenster: '$($proc.MainWindowTitle)'"

    # 2. still alive after a few seconds (scan of the test folder runs meanwhile)
    Start-Sleep -Seconds $Seconds
    $proc.Refresh()
    if ($proc.HasExited) { throw "Absturz: Prozess nach dem Start beendet (Exit-Code $($proc.ExitCode))." }
    if (-not $proc.Responding) { throw "Die App reagiert nicht mehr." }
    Write-Host "Laeuft nach $Seconds s weiter und reagiert."

    # 3. optional screenshot
    if ($Screenshot) {
        $out = Join-Path $PSScriptRoot 'out'
        New-Item -ItemType Directory -Path $out -Force | Out-Null
        [SmokeWin]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 400
        $r = New-Object SmokeWin+RECT
        [SmokeWin]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
        if ($w -gt 0 -and $h -gt 0) {
            $bmp = New-Object System.Drawing.Bitmap $w, $h
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
            $file = Join-Path $out ('ui-smoke-{0:yyyyMMdd-HHmmss}.png' -f (Get-Date))
            $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Write-Host "Screenshot: $file"
        }
    }
    $ok = $true
}
catch {
    Write-Host "FEHLER: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($proc -and -not $proc.HasExited) {
        [void]$proc.CloseMainWindow()
        if (-not $proc.WaitForExit(5000)) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null }
    }
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
}

if ($ok) { Write-Host 'UI-Smoke-Test: OK'; exit 0 }
Write-Host 'UI-Smoke-Test: FEHLGESCHLAGEN' -ForegroundColor Red
exit 1
