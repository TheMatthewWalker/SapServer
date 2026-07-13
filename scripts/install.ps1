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

# ---- SAP credentials -------------------------------------------------------
Write-Host ""
Write-Host "Enter SAP service account credentials (stored as machine env vars):"
$sapUser = Read-Host "  SAP Username"
$sapPass = Read-Host "  SAP Password" -AsSecureString
$sapPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sapPass))

# ---- Machine environment variables ----------------------------------------
Write-Host "Setting environment variables..."
[System.Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT',          'Production',   'Machine')
[System.Environment]::SetEnvironmentVariable('Auth__JwtSecret',                  $secretPlain,   'Machine')
[System.Environment]::SetEnvironmentVariable('SapPool__ServiceAccount__User',     $sapUser,       'Machine')
[System.Environment]::SetEnvironmentVariable('SapPool__ServiceAccount__Password', $sapPassPlain,  'Machine')

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
