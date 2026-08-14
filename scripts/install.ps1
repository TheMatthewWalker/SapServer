# install.ps1 — Register SapServer as a Task Scheduler startup task.
# Runs in the interactive user session, which is required for SAP GUI COM objects.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$taskName   = 'SapServer'
$exePath    = "$PSScriptRoot\..\publish\SapServer.exe"
$exePath    = (Resolve-Path $exePath).Path
$workingDir = Split-Path $exePath

# ---- Machine environment variables ----------------------------------------
# ASPNETCORE_ENVIRONMENT has to be an env var (or command-line arg) — it's what
# tells ASP.NET Core which appsettings.{Environment}.json to layer on top of
# appsettings.json in the first place, so there's no config-file equivalent to
# put it in instead.
Write-Host "Setting environment variables..."
[System.Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')

# ---- Secrets ----------------------------------------------------------------
# Deliberately NOT prompted for here / set as env vars — Auth:JwtSecret and
# SapPool:ServiceAccount(s) (SAP service account credentials — one or several,
# see README.md) live directly in appsettings.Production.json instead, same as
# every other setting. That file is already .gitignore'd, same protection env
# vars would have given, but much easier to maintain — no separate step to
# remember, no re-running this script just to rotate a value.
Write-Host ""
Write-Host "Reminder: fill in Auth:JwtSecret (shared with sql2005-bridge) and"        -ForegroundColor Yellow
Write-Host "SapPool:ServiceAccount / ServiceAccounts directly in"                     -ForegroundColor Yellow
Write-Host "appsettings.Production.json before starting the service — neither is"     -ForegroundColor Yellow
Write-Host "set via this script."                                                     -ForegroundColor Yellow

# ---- Register Task Scheduler task ------------------------------------------
Write-Host "Registering scheduled task '$taskName'..."

$action  = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $workingDir
$trigger = New-ScheduledTaskTrigger -AtLogon
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit 0 `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable

# Run as current user so SAP COM objects have access to the user session
$principal = New-ScheduledTaskPrincipal `
    -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) `
    -LogonType Interactive `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName $taskName `
    -Action   $action `
    -Trigger  $trigger `
    -Settings $settings `
    -Principal $principal `
    -Force | Out-Null

Write-Host ""
Write-Host "Task registered. Run 'start.ps1' to start it now." -ForegroundColor Green
Write-Host "It will also start automatically at next logon."   -ForegroundColor Green
