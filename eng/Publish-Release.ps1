# Prepares a release by bumping the version and opening a PR.
#
# Releasing itself is now a MANUAL action — this script does NOT trigger a
# release. After the PR this opens is merged, go to the GitHub Actions tab ->
# "Release" -> "Run workflow" and enter the same version. That single run:
#   - builds every artifact the vegha.ai site links to (signed win x64/arm64,
#     mac dmg, linux AppImage) and publishes the GitHub Release + Velopack feeds,
#   - generates the winget (Vegha.Vegha) manifests as an artifact for manual
#     submission,
#   - builds the MSIX and submits it to the Microsoft Store.
#
# This script no longer creates or pushes a git tag: release.yml creates the
# v<version> tag itself when it publishes the GitHub Release. Bumping
# Directory.Build.props keeps the About dialog and dev builds in sync (the
# release workflow passes the version explicitly, so artifacts are correct
# regardless, but main should still reflect the released version).
#
# Examples:
#   ./eng/Publish-Release.ps1 -Version 1.2.3
#   ./eng/Publish-Release.ps1 -Version 1.2.3 -SkipPush   # branch + commit only

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $SkipPush
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$branch = "release/v$Version"

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

    # --- Branch off the latest main --------------------------------------
    & git fetch origin main
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
    & git switch -c $branch origin/main
    if ($LASTEXITCODE -ne 0) { throw "Failed to create branch $branch (does it already exist?)." }

    # --- Bump <Version> in Directory.Build.props -------------------------
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $props = Get-Content $propsPath -Raw
    $updated = [regex]::Replace($props, '<Version>.*?</Version>', "<Version>$Version</Version>")
    if ($updated -eq $props) {
        Write-Host "Directory.Build.props already at $Version — committing branch anyway for the PR." -ForegroundColor Yellow
    }
    Set-Content -Path $propsPath -Value $updated -NoNewline
    & git add Directory.Build.props
    & git commit -m "Release v$Version"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed (nothing to commit?)." }
    Write-Host "Bumped Directory.Build.props to $Version on branch $branch." -ForegroundColor Green

    if ($SkipPush) {
        Write-Host "-SkipPush set. Push + open a PR manually:" -ForegroundColor Yellow
        Write-Host "    git push -u origin $branch" -ForegroundColor Yellow
        return
    }

    # --- Push branch + open PR -------------------------------------------
    & git push -u origin $branch
    if ($LASTEXITCODE -ne 0) { throw "git push failed" }

    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $body = "Version bump for the v$Version release.`n`nAfter merge: **Actions -> Release -> Run workflow**, version ``$Version``."
        & gh pr create --base main --head $branch --title "Release v$Version" --body $body
        if ($LASTEXITCODE -ne 0) { throw "gh pr create failed" }
    }
    else {
        Write-Host "gh CLI not found — open the PR for '$branch' -> main manually." -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Review + merge the release PR." -ForegroundColor Cyan
Write-Host "  2. Actions -> Release -> Run workflow -> version $Version." -ForegroundColor Cyan
Write-Host "  3. After the run: download the 'winget-manifests' artifact and submit it to winget-pkgs." -ForegroundColor Cyan
