# Installs the Vegha Setup.exe from releases/<rid> into a SCRATCH directory, silently, for
# upgrade testing. This is a throwaway install — it does NOT touch any real Vegha you have.
#
#   ./eng/test/Install-LocalBuild.ps1                 # installs releases/win-x64 -> %LOCALAPPDATA%\VeghaUpgradeTest
#   ./eng/test/Install-LocalBuild.ps1 -Runtime win-x64
#
# Run eng/Pack-Installer.ps1 first so the Setup.exe exists.

[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'VeghaUpgradeTest'),
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$rel = Join-Path $repoRoot "releases/$Runtime"

$setup = Get-ChildItem $rel -Filter '*Setup.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $setup) {
    throw "No *Setup.exe under $rel. Run:  ./eng/Pack-Installer.ps1 -Runtime $Runtime -Version <X>  first."
}

Write-Host "Installing $($setup.Name)  ->  $InstallDir   (silent)..." -ForegroundColor Cyan
& $setup.FullName --silent --installto $InstallDir
Start-Sleep -Seconds 5

$exe = Join-Path $InstallDir 'current\Vegha.App.exe'
if (-not (Test-Path $exe)) {
    Get-ChildItem $InstallDir -Recurse -ErrorAction SilentlyContinue | ForEach-Object FullName
    throw "Install did not produce $exe"
}

Write-Host "Installed OK." -ForegroundColor Green
Write-Host "Launch it with:" -ForegroundColor Green
Write-Host "    Start-Process `"$exe`""
