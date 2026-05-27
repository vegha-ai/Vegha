# Signs an MSIX with a dev cert and (optionally) sideloads it for local testing.
#
# Why: Windows refuses to install MSIX packages whose publisher cert isn't trusted on
# the machine. Partner Center signs Store ingest, but for local sideload you need a
# self-signed cert that (a) matches the manifest's Publisher EXACTLY and (b) lives in
# LocalMachine\TrustedPeople. This script handles all of that.
#
# Examples:
#   # First run (creates cert + signs + installs):
#   ./eng/Test-MsixLocal.ps1 -MsixPath releases/msix/Vegha-1.0.2.0.msix
#
#   # Just sign (no install) — useful when you'll double-click the .msix:
#   ./eng/Test-MsixLocal.ps1 -MsixPath releases/msix/Vegha-1.0.2.0.msix -NoInstall
#
#   # Force a fresh cert (e.g. after rotating the manifest Publisher):
#   ./eng/Test-MsixLocal.ps1 -MsixPath releases/msix/Vegha-1.0.2.0.msix -ForceNewCert
#
# Requires: admin (to import the cert into LocalMachine\TrustedPeople). The script
# will relaunch itself elevated if needed.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MsixPath,

    [string] $CertSubject = 'CN=366CAB54-5973-4620-BC10-FC2235E7BE4C',
    [string] $PfxPath     = "$env:LOCALAPPDATA\Vegha\dev-signing.pfx",
    [string] $PfxPassword = 'vegha-dev',
    [switch] $NoInstall,
    [switch] $ForceNewCert
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Elevate if not admin -------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Re-launching elevated..." -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
                 '-MsixPath', $MsixPath,
                 '-CertSubject', $CertSubject,
                 '-PfxPath', $PfxPath,
                 '-PfxPassword', $PfxPassword)
    if ($NoInstall)     { $argList += '-NoInstall' }
    if ($ForceNewCert)  { $argList += '-ForceNewCert' }
    Start-Process pwsh -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$MsixPath = (Resolve-Path $MsixPath).Path
if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }

# --- Locate signtool ------------------------------------------------------
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
if (-not (Test-Path $sdkRoot)) {
    throw "Windows 10 SDK not found at $sdkRoot. Install via Visual Studio Installer."
}
$signtool = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found under $sdkRoot" }
$signtoolPath = $signtool.FullName

# --- Create dev cert if missing ------------------------------------------
$pfxDir = Split-Path $PfxPath
if (-not (Test-Path $pfxDir)) { New-Item -ItemType Directory -Force $pfxDir | Out-Null }

if ($ForceNewCert -and (Test-Path $PfxPath)) {
    Write-Host "Removing existing dev cert (-ForceNewCert)..." -ForegroundColor Yellow
    Remove-Item $PfxPath -Force
}

if (-not (Test-Path $PfxPath)) {
    Write-Host "==> Creating self-signed cert for $CertSubject"
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $CertSubject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Vegha MSIX dev signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

    $pwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $pwd | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
    Write-Host "  Wrote $PfxPath"
}

# --- Trust the cert in LocalMachine\TrustedPeople (where App Installer looks)
$pwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
$imported = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $pwd
Write-Host "==> Trusted cert thumbprint: $($imported.Thumbprint)"

# --- Sign the MSIX --------------------------------------------------------
Write-Host "==> Signing $MsixPath"
& $signtoolPath sign /fd SHA256 /a /f $PfxPath /p $PfxPassword $MsixPath
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

# --- Install --------------------------------------------------------------
if ($NoInstall) {
    Write-Host ""
    Write-Host "Signed. To install, double-click the .msix or run:" -ForegroundColor Green
    Write-Host "    Add-AppxPackage -Path '$MsixPath'" -ForegroundColor Green
    return
}

Write-Host "==> Installing $MsixPath"
# -ForceTargetApplicationShutdown closes any running Vegha instance during upgrade.
Add-AppxPackage -Path $MsixPath -ForceTargetApplicationShutdown
Write-Host ""
Write-Host "Installed. Look for Vegha in Start menu / pinned taskbar." -ForegroundColor Green
