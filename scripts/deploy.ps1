# deploy.ps1 - Stop the app pool, rebuild, publish, and restart it.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$appPoolName = 'SapServer'
$siteName    = 'SapServer'
$projectRoot = "$PSScriptRoot\.."
$publishDir  = "$projectRoot\publish"

# ---- Stop if running -------------------------------------------------------
$poolExists = Test-Path "IIS:\AppPools\$appPoolName"
if ($poolExists -and (Get-WebAppPoolState -Name $appPoolName).Value -eq 'Started') {
    Write-Host "Stopping app pool..."
    Stop-WebAppPool -Name $appPoolName
    Start-Sleep -Seconds 2
}

# ---- Publish ---------------------------------------------------------------
# net48 is framework-dependent, not self-contained — win-x64/--self-contained
# don't apply the way they did for the old ASP.NET Core publish. Any modern
# Windows Server already ships .NET Framework 4.8; PlatformTarget=x64 in
# SapServer.csproj still governs bitness (matches the app pool's
# enable32BitAppOnWin64=$false set by install.ps1).
Write-Host "Publishing..."
dotnet publish "$projectRoot\SapServer.csproj" -c Release -o "$publishDir"
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed."; exit 1 }

# ---- Start -----------------------------------------------------------------
if ($poolExists) {
    Write-Host "Starting app pool..."
    Start-WebAppPool -Name $appPoolName
    Start-Sleep -Seconds 2
    $newState = (Get-WebAppPoolState -Name $appPoolName).Value
    Write-Host "State: $newState" -ForegroundColor $(if ($newState -eq 'Started') { 'Green' } else { 'Yellow' })
} else {
    Write-Host "App pool not registered - run 'install.ps1' first." -ForegroundColor Yellow
}
