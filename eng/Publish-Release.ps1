# Tags a release and triggers the cross-platform build/publish pipeline.
#
# Examples:
#   ./eng/Publish-Release.ps1 -Version 1.0.0
#   ./eng/Publish-Release.ps1 -Version 1.0.1 -BuildLocal
#
# Default flow:
#   1. Validate the version and that the git tree is clean.
#   2. Bump <Version> in Directory.Build.props (the single source of truth —
#      drives About dialog, Velopack pack version, AND MSIX Identity/@Version
#      via Pack-Msix.ps1 which appends ".0" for the Store's revision rule).
#   3. Commit, create annotated tag vX.Y.Z, and push --follow-tags.
#   4. The pushed tag triggers BOTH .github/workflows/release.yml (Velopack
#      installers for win/mac/linux, published as a GitHub Release) AND
#      .github/workflows/msix.yml (unsigned MSIX uploaded as a workflow
#      artifact for manual Partner Center submission). Same version everywhere.
#
# With -BuildLocal it ALSO builds the Windows installers on this machine and
# uploads them to the release via `gh` — useful for a Windows-only hotfix or
# when CI is unavailable. macOS and Linux installers can only be built in CI.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $BuildLocal,
    [switch] $SkipPush
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tag = "v$Version"

function Require-Command([string] $name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command '$name' is not on PATH."
    }
}

Require-Command git

Push-Location $repoRoot
try {
    # --- Preconditions ---------------------------------------------------
    if (git status --porcelain) {
        throw "Working tree is not clean. Commit or stash changes before releasing."
    }
    if (git tag --list $tag) {
        throw "Tag $tag already exists."
    }

    # --- Bump version in Directory.Build.props ---------------------------
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $props = Get-Content $propsPath -Raw
    $updated = [regex]::Replace($props, '<Version>.*?</Version>', "<Version>$Version</Version>")
    if ($updated -ne $props) {
        Set-Content -Path $propsPath -Value $updated -NoNewline
        & git add Directory.Build.props
        & git commit -m "Release $tag"
        Write-Host "Bumped Directory.Build.props to $Version and committed." -ForegroundColor Green
    }
    else {
        Write-Host "Directory.Build.props already at $Version — no version commit needed."
    }

    # --- Tag -------------------------------------------------------------
    & git tag -a $tag -m "Vegha $tag"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
    Write-Host "Created tag $tag." -ForegroundColor Green

    if ($SkipPush) {
        Write-Host "-SkipPush set: nothing pushed. Push manually with:" -ForegroundColor Yellow
        Write-Host "    git push --follow-tags" -ForegroundColor Yellow
    }
    else {
        & git push --follow-tags
        if ($LASTEXITCODE -ne 0) { throw "git push failed" }
        Write-Host "Pushed commit + tag — release.yml will build and publish the GitHub Release." -ForegroundColor Green
    }
}
finally {
    Pop-Location
}

# --- Optional: build + upload the Windows installers locally -------------
if ($BuildLocal) {
    Require-Command gh

    Write-Host ""
    Write-Host "=== Building Windows installers locally ===" -ForegroundColor Cyan

    $packScript = Join-Path $PSScriptRoot 'Pack-Installer.ps1'
    $stage = Join-Path $repoRoot 'release-local'
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # rid -> stable asset name (must match the publish job in release.yml).
    $map = [ordered]@{
        'win-x64'   = 'Vegha-win-x64-Setup.exe'
        'win-arm64' = 'Vegha-win-arm64-Setup.exe'
    }

    foreach ($rid in $map.Keys) {
        & $packScript -Runtime $rid -Version $Version
        if ($LASTEXITCODE -ne 0) { throw "Pack-Installer.ps1 failed for $rid" }

        $setup = Get-ChildItem (Join-Path $repoRoot "releases/$rid") -Filter '*Setup.exe' |
            Select-Object -First 1
        if (-not $setup) { throw "No *Setup.exe produced for $rid" }
        Copy-Item $setup.FullName (Join-Path $stage $map[$rid])
    }

    $assets = Get-ChildItem $stage | ForEach-Object { $_.FullName }

    Write-Host "Uploading Windows installers to release $tag via gh..." -ForegroundColor Cyan
    if (gh release view $tag 2>$null) {
        & gh release upload $tag @assets --clobber
    }
    else {
        & gh release create $tag @assets --title "Vegha $tag" --generate-notes
    }
    if ($LASTEXITCODE -ne 0) { throw "gh release upload/create failed" }
    Write-Host "Windows installers attached to $tag." -ForegroundColor Green
}

Write-Host ""
Write-Host "Release $tag initiated." -ForegroundColor Green
