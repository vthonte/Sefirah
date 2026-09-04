# Sefirah Sideload Installer
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "       Sefirah Sideload Installer       " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$scriptDir = $PSScriptRoot

# 1. Install certificate
$cer = Get-ChildItem -Path $scriptDir -Recurse -Filter "*.cer" | Select-Object -First 1
if ($cer) {
    Write-Host "`n[1/2] Installing certificate $($cer.Name) to Trusted People store..." -ForegroundColor Yellow
    Import-Certificate -FilePath $cer.FullName -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
    try {
        Import-Certificate -FilePath $cer.FullName -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" -ErrorAction SilentlyContinue | Out-Null
    } catch {}
    Write-Host "Certificate installed." -ForegroundColor Green
} else {
    Write-Host "`n[1/2] No .cer certificate found, proceeding..." -ForegroundColor DarkGray
}

# 2. Install MSIX package
$msix = Get-ChildItem -Path $scriptDir -Recurse -Filter "*.msix" | Select-Object -First 1
if ($msix) {
    Write-Host "`n[2/2] Installing MSIX package $($msix.Name)..." -ForegroundColor Yellow
    Add-AppxPackage -Path $msix.FullName
    Write-Host "`nSUCCESS! Sefirah installed successfully." -ForegroundColor Green
    Write-Host "You can now launch Sefirah from the Windows Start menu!" -ForegroundColor Green
} else {
    Write-Error "Could not find .msix package in $scriptDir"
}
