# stop.ps1 - Stop the SapServer IIS application pool.
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$appPoolName = 'SapServer'

if (-not (Test-Path "IIS:\AppPools\$appPoolName")) {
    Write-Host "App pool '$appPoolName' is not registered." -ForegroundColor Yellow
    exit 0
}

$state = (Get-WebAppPoolState -Name $appPoolName).Value
if ($state -ne 'Started') {
    Write-Host "App pool '$appPoolName' is not running (state: $state)." -ForegroundColor Yellow
    exit 0
}

Write-Host "Stopping '$appPoolName'..."
Stop-WebAppPool -Name $appPoolName
Write-Host "Stopped." -ForegroundColor Green
