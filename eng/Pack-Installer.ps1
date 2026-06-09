# Builds a Velopack installer for Vegha.App for one or more runtimes.
#
# Examples:
#   ./eng/Pack-Installer.ps1 -Runtime win-x64 -Version 0.1.0
#   ./eng/Pack-Installer.ps1 -Runtime all -Version 0.1.0
#
# Output: releases/<rid>/Vegha-Setup.exe + delta/full nupkg (or .pkg/.AppImage on mac/linux).
# This is the single source of truth used by .github/workflows/release.yml — keep them in sync.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'all')]
    [string] $Runtime,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $Configuration = 'Release',

    [string] $PackId = 'Vegha',
    [string] $PackTitle = 'Vegha',
    [string] $PackAuthors = 'Vegha contributors',

    # signtool arguments (everything except the target file) forwarded to
    # `vpk pack --signParams`, so Velopack Authenticode-signs the app binaries
    # AND the generated Setup.exe during packaging. Windows runtimes only;
    # ignored for osx-*/linux-*. Empty (default) = no signing.
    [string] $SignParams = '',

    [switch] $SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'app/Vegha.App/Vegha.App.csproj'

if (-not (Test-Path $projectPath)) {
    throw "Project not found: $projectPath"
}

function Ensure-Vpk {
    if (Get-Command vpk -ErrorAction SilentlyContinue) { return }
    Write-Host "vpk not on PATH — installing Velopack CLI as a global dotnet tool..."
    & dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw "Failed to install vpk" }
    # dotnet tool install puts vpk in ~/.dotnet/tools — make sure it's reachable for this session.
    $toolsDir = Join-Path $HOME '.dotnet/tools'
    if (Test-Path $toolsDir) { $env:PATH = "$toolsDir$([IO.Path]::PathSeparator)$env:PATH" }
}

function Get-MainExe([string] $rid) {
    if ($rid.StartsWith('win-')) { return 'Vegha.App.exe' }
    return 'Vegha.App'
}

function Invoke-Pack([string] $rid) {
    $publishDir = Join-Path $repoRoot "publish/$rid"
    $outputDir = Join-Path $repoRoot "releases/$rid"
    $mainExe = Get-MainExe $rid

    Write-Host ""
    Write-Host "=== Packing $PackId $Version for $rid ===" -ForegroundColor Cyan

    Write-Host "Publishing self-contained to $publishDir..."
    # PublishSingleFile=true on macOS/Linux embeds managed assemblies and
    # runtimeconfig.json into the executable so codesign has no unsignable
    # subcomponents to trip over. Windows uses false (single-file + Velopack
    # delta patching requires loose assemblies on Windows).
    $singleFile = if ($rid.StartsWith('win-')) { 'false' } else { 'true' }
    & dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=$singleFile `
        -p:PublishReadyToRun=true `
        -p:Version=$Version `
        --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

    $packArgs = @(
        'pack'
        '--packId', $PackId
        '--packTitle', $PackTitle
        '--packVersion', $Version
        '--packAuthors', $PackAuthors
        '--packDir', $publishDir
        '--mainExe', $mainExe
        '--outputDir', $outputDir
        # One Velopack channel per RID (win-x64 / win-arm64 / osx-arm64 / linux-x64) so every
        # platform's feed (releases.<channel>.json + *.nupkg) can live in a single GitHub
        # Release without colliding. The app selects its channel via ExplicitChannel
        # (VelopackUpdateService.CurrentRidChannel) — these two MUST stay in lockstep.
        '--channel', $rid
    )
    if ($SignParams -and $rid.StartsWith('win-')) {
        Write-Host "Authenticode signing enabled (vpk --signParams)."
        $packArgs += '--signParams', $SignParams
    }
    elseif ($SignParams) {
        Write-Host "Ignoring -SignParams for non-Windows runtime $rid."
    }

    Write-Host "Running vpk pack..."
    & vpk @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for $rid" }

    Write-Host "Output:" -ForegroundColor Green
    Get-ChildItem $outputDir | ForEach-Object { Write-Host "  $($_.Name) ($([int]($_.Length / 1KB)) KB)" }
}

Ensure-Vpk

if (-not $SkipRestore) {
    Write-Host "Restoring NuGet packages..."
    & dotnet restore $projectPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
}

$runtimes = if ($Runtime -eq 'all') {
    @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64')
} else {
    @($Runtime)
}

foreach ($rid in $runtimes) {
    Invoke-Pack $rid
}

Write-Host ""
Write-Host "Done. Installers in $repoRoot/releases/" -ForegroundColor Green
