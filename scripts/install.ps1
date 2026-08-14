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
Write-Host "Enter SAP service account credentials (stored as machine env vars)."
Write-Host "Enter 1 to keep every service worker on a single shared account (the"
Write-Host "original behaviour), or more to give each worker its own SAP login"
Write-Host "(SapPool:ServiceWorkerCount workers cycle round-robin through however"
Write-Host "many accounts you enter here, e.g. 4 workers over 4 accounts = 1:1)."
$accountCountInput = Read-Host "  How many service accounts? [1]"
$accountCount = 1
if ($accountCountInput -and [int]::TryParse($accountCountInput, [ref]$accountCount)) {
    if ($accountCount -lt 1) { $accountCount = 1 }
} else {
    $accountCount = 1
}

# ---- Machine environment variables ----------------------------------------
Write-Host "Setting environment variables..."
[System.Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')
[System.Environment]::SetEnvironmentVariable('Auth__JwtSecret',        $secretPlain, 'Machine')

for ($i = 0; $i -lt $accountCount; $i++) {
    Write-Host ""
    Write-Host "Service account $($i + 1) of $accountCount`:"
    $sapUser = Read-Host "  SAP Username"
    $sapPass = Read-Host "  SAP Password" -AsSecureString
    $sapPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sapPass))

    # A single account still goes through SapPool:ServiceAccounts (index 0) —
    # SapConnectionPool.SelectWorker routes every worker to ServiceAccounts[0]
    # in that case, which is equivalent to the old single-ServiceAccount
    # behaviour, so there's no need to also set the legacy
    # SapPool__ServiceAccount__User/Password pair here.
    [System.Environment]::SetEnvironmentVariable("SapPool__ServiceAccounts__${i}__User",     $sapUser,      'Machine')
    [System.Environment]::SetEnvironmentVariable("SapPool__ServiceAccounts__${i}__Password", $sapPassPlain, 'Machine')
}

Write-Host ""
Write-Host "Reminder: SapPool:ServiceAccount (singular) in appsettings.Production.json"
Write-Host "still needs its System/Client/SystemId/Language filled in — Purchasing/"
Write-Host "PackagingController's elevated endpoints read that as the shared"
Write-Host "connection profile regardless of how many ServiceAccounts you just set."
Write-Host "Also set SapPool:ServiceWorkerCount to $accountCount (or a multiple of it)"
Write-Host "in appsettings.Production.json so every account actually gets used."

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
