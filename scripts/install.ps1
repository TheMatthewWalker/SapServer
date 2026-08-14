# install.ps1 — Register SapServer as a Task Scheduler startup task.
# Runs in the interactive user session, which is required for SAP GUI COM objects.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$taskName   = 'SapServer'
$exePath    = "$PSScriptRoot\..\publish\SapServer.exe"
$exePath    = (Resolve-Path $exePath).Path
$workingDir = Split-Path $exePath

# ---- JWT secret ------------------------------------------------------------
$secret = Read-Host "Enter Auth__JwtSecret (shared with sql2005-bridge)" -AsSecureString
$secretPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret))

if ($secretPlain.Length -lt 32) { Write-Error "Secret must be at least 32 characters."; exit 1 }

# ---- Machine environment variables ----------------------------------------
Write-Host "Setting environment variables..."
[System.Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
[System.Environment]::SetEnvironmentVariable('Auth__JwtSecret',        $secretPlain, 'Machine')

# ---- SAP credentials --------------------------------------------------------
# Deliberately NOT prompted for here / set as env vars — SAP service account(s)
# live directly in appsettings.Production.json's SapPool:ServiceAccount (single
# account) or SapPool:ServiceAccounts (array, one login per service worker,
# see README.md), same as every other non-secret SapPool setting. That file is
# already .gitignore'd, same protection env vars would have given, but a lot
# easier to maintain when there's more than one account to keep track of.
Write-Host ""
Write-Host "Reminder: fill in SapPool:ServiceAccount (and SapPool:ServiceAccounts if" -ForegroundColor Yellow
Write-Host "you want more than one login) directly in appsettings.Production.json"    -ForegroundColor Yellow
Write-Host "before starting the service — it's no longer set via this script."        -ForegroundColor Yellow

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
