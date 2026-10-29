# Builds DiskTree as a single .exe.
#   dist\DiskTree.exe        self-contained: runs on any Windows x64 machine, nothing to install
#   dist\slim\DiskTree.exe   framework-dependent: much smaller, needs the .NET 10 Desktop Runtime
param(
    [switch]$SelfContainedOnly
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'DiskTree.csproj'
$dist = Join-Path $here 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish $proj -c Release -p:SelfContained=true -o $dist
if ($LASTEXITCODE -ne 0) { throw 'publish (self-contained) failed' }

if (-not $SelfContainedOnly) {
    # compression of the bundle is only possible for self-contained apps
    dotnet publish $proj -c Release -p:SelfContained=false -p:EnableCompressionInSingleFile=false -o (Join-Path $dist 'slim')
    if ($LASTEXITCODE -ne 0) { throw 'publish (slim) failed' }
}

Get-ChildItem $dist -Recurse -File -Filter *.exe | ForEach-Object {
    '{0,-40} {1,8:N1} MB' -f $_.FullName.Substring($dist.Length + 1), ($_.Length / 1MB)
}
