# start.ps1 — Start the SapServer IIS application pool + site.
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$appPoolName = 'SapServer'
$siteName    = 'SapServer'

Write-Host "Starting app pool '$appPoolName'..."
Start-WebAppPool -Name $appPoolName
Start-Website -Name $siteName

Start-Sleep -Seconds 2
$state = (Get-WebAppPoolState -Name $appPoolName).Value

Write-Host "State : $state" -ForegroundColor $(if ($state -eq 'Started') { 'Green' } else { 'Yellow' })
