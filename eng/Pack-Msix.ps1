# Builds an MSIX bundle for Vegha.App (Microsoft Store path).
#
# Examples:
#   ./eng/Pack-Msix.ps1 -Version 0.1.0.0
#   ./eng/Pack-Msix.ps1 -Version 0.1.0.0 -OutputDir releases/msix
#
# Output: <OutputDir>/Vegha-<Version>.msix (unsigned).
# This is the single source of truth used by .github/workflows/msix.yml — keep them in sync.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $Configuration = 'Release',
    [string] $OutputDir = '.',
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'app/Vegha.App/Vegha.App.csproj'
$manifestPath = Join-Path $repoRoot 'app/Vegha.App/Package.appxmanifest'
$publishDir = Join-Path $repoRoot 'publish/win-x64-msix'
$stageDir = Join-Path $repoRoot 'stage-msix'

if (-not (Test-Path $projectPath))  { throw "Project not found: $projectPath" }
if (-not (Test-Path $manifestPath)) { throw "Manifest not found: $manifestPath" }

function Find-MakeAppx {
    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) {
        throw "Windows 10 SDK not found at $sdkRoot. Install via Visual Studio Installer or https://aka.ms/windowssdk."
    }
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $candidate = Get-ChildItem $sdkRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\$arch\\" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) { throw "$arch makeappx.exe not found under $sdkRoot" }
    $candidate.FullName
}

Write-Host "==> Patching manifest Identity/@Version to $Version"
$manifestXml = Get-Content $manifestPath -Raw
$pattern = '(<Identity\b[^>]*?\bVersion=")\d+\.\d+\.\d+\.\d+(")'
if ($manifestXml -notmatch $pattern) { throw "Identity/@Version not found in $manifestPath" }
$patched = [regex]::Replace($manifestXml, $pattern, "`${1}$Version`${2}")
Set-Content -Path $manifestPath -Value $patched -NoNewline

if (-not $SkipPublish) {
    Write-Host "==> dotnet publish ($Configuration, win-x64, self-contained, MSIX flavor)"
    & dotnet publish $projectPath `
        -c $Configuration -r win-x64 --self-contained true `
        -p:VeghaFlavor=MSIX `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "==> Skipping publish (-SkipPublish); reusing $publishDir"
    if (-not (Test-Path $publishDir)) { throw "Publish dir missing: $publishDir" }
}

Write-Host "==> Staging MSIX layout in $stageDir"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force $stageDir | Out-Null
Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse -Force
Copy-Item $manifestPath (Join-Path $stageDir 'AppxManifest.xml') -Force

# Logo assets are <AvaloniaResource> (embedded in the DLL), so dotnet publish
# doesn't emit them on disk. MSIX validates the manifest's logo paths against
# real files in the package, so copy them in here.
$assetsSrc = Join-Path $repoRoot 'app/Vegha.App/Assets'
$assetsDst = Join-Path $stageDir 'Assets'
New-Item -ItemType Directory -Force $assetsDst | Out-Null
Copy-Item (Join-Path $assetsSrc '*') $assetsDst -Force

$makeappx = Find-MakeAppx
Write-Host "==> Using $makeappx"

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$msixPath = Join-Path (Resolve-Path $OutputDir) "Vegha-$Version.msix"

Write-Host "==> Packing $msixPath"
& $makeappx pack /d $stageDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "MSIX built: $msixPath" -ForegroundColor Green
