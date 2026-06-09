# Removes the scratch upgrade-test install created by Install-LocalBuild.ps1.
# Safe to run even when nothing is installed (no-op then).
#
#   ./eng/test/Uninstall-LocalBuild.ps1

[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'VeghaUpgradeTest')
)

$update = Join-Path $InstallDir 'Update.exe'
if (Test-Path $update) {
    Write-Host "Uninstalling via Update.exe --uninstall ..." -ForegroundColor Cyan
    try {
        & $update --uninstall
        Start-Sleep -Seconds 3
    }
    catch {
        Write-Warning "Update.exe --uninstall reported: $_"
    }
}

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Clean. ($InstallDir removed)" -ForegroundColor Green
