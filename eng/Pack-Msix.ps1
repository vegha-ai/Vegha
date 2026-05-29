# Builds an MSIX bundle for Vegha.App (Microsoft Store path).
#
# Examples:
#   ./eng/Pack-Msix.ps1                          # derive version from Directory.Build.props
#   ./eng/Pack-Msix.ps1 -Version 1.0.2           # 3-part: auto-extends to 1.0.2.0
#   ./eng/Pack-Msix.ps1 -Version 1.0.2.0         # 4-part: used as-is (revision must be 0)
#
# Output: <OutputDir>/Vegha-<Version>.msix (unsigned).
# This is the single source of truth used by .github/workflows/msix.yml — keep them in sync.

[CmdletBinding()]
param(
    # Optional. When omitted, the version is read from Directory.Build.props's
    # <Version>. Accepts a 3-part SemVer (e.g. 1.0.2) which gets extended to
    # 1.0.2.0, or a 4-part MSIX version (e.g. 1.0.2.0). Store rejects any
    # revision (4th component) other than 0.
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
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
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$publishDir = Join-Path $repoRoot 'publish/win-x64-msix'
$stageDir = Join-Path $repoRoot 'stage-msix'

if (-not (Test-Path $projectPath))  { throw "Project not found: $projectPath" }
if (-not (Test-Path $manifestPath)) { throw "Manifest not found: $manifestPath" }

# Resolve version: explicit param > Directory.Build.props. Normalize to 4-part.
if (-not $Version) {
    $propsText = Get-Content $propsPath -Raw
    if ($propsText -notmatch '<Version>([^<]+)</Version>') {
        throw "Directory.Build.props does not contain <Version>; pass -Version explicitly."
    }
    $Version = $Matches[1].Trim()
    Write-Host "==> Using version from Directory.Build.props: $Version"
}
if ($Version -match '^\d+\.\d+\.\d+$') {
    $Version = "$Version.0"   # extend SemVer to MSIX 4-part with revision=0
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "MSIX rejects non-zero revision: got $Version. Use major.minor.build form (revision must be 0)."
}

function Find-SdkTool {
    param([Parameter(Mandatory)][string] $Name)
    $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) {
        throw "Windows 10 SDK not found at $sdkRoot. Install via Visual Studio Installer or https://aka.ms/windowssdk."
    }
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $candidate = Get-ChildItem $sdkRoot -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\$arch\\" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) { throw "$arch $Name not found under $sdkRoot" }
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
        -p:PublishReadyToRun=true `
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

$makeappx = Find-SdkTool 'makeappx.exe'
$makepri  = Find-SdkTool 'makepri.exe'
Write-Host "==> makeappx: $makeappx"
Write-Host "==> makepri:  $makepri"

# Generate resources.pri so MRT honors qualifiers on logo assets (targetsize-*,
# scale-*, _altform-unplated). Without this file, Windows falls back to plain
# filename lookup which does NOT honor altform-unplated — the taskbar then
# renders Square44x44Logo on a BackgroundColor plate even when an unplated
# variant is present in the package.
Write-Host "==> Generating priconfig.xml + resources.pri"
$priConfig = Join-Path $stageDir 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /o
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed with exit code $LASTEXITCODE" }
$priOut = Join-Path $stageDir 'resources.pri'
& $makepri new /pr $stageDir /cf $priConfig /of $priOut /mn (Join-Path $stageDir 'AppxManifest.xml') /o
if ($LASTEXITCODE -ne 0) { throw "makepri new failed with exit code $LASTEXITCODE" }
Remove-Item $priConfig -Force  # build-time only; don't ship in the package

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$msixPath = Join-Path (Resolve-Path $OutputDir) "Vegha-$Version.msix"

Write-Host "==> Packing $msixPath"
& $makeappx pack /d $stageDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "MSIX built: $msixPath" -ForegroundColor Green
