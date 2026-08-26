# uninstall.ps1 — Remove the SapServer IIS site and application pool.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$appPoolName = 'SapServer'
$siteName    = 'SapServer'

$site = Get-Website -Name $siteName -ErrorAction SilentlyContinue
if ($site) {
    Write-Host "Removing site '$siteName'..."
    Remove-Website -Name $siteName
} else {
    Write-Host "Site '$siteName' is not registered." -ForegroundColor Yellow
}

if (Test-Path "IIS:\AppPools\$appPoolName") {
    if ((Get-WebAppPoolState -Name $appPoolName).Value -eq 'Started') {
        Stop-WebAppPool -Name $appPoolName
    }
    Write-Host "Removing app pool '$appPoolName'..."
    Remove-WebAppPool -Name $appPoolName
    Write-Host "App pool '$appPoolName' removed." -ForegroundColor Green
} else {
    Write-Host "App pool '$appPoolName' is not registered." -ForegroundColor Yellow
}
