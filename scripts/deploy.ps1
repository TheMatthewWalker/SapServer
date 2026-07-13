# deploy.ps1 - Stop, rebuild, publish, and restart SapServer.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$taskName    = 'SapServer'
$projectRoot = "$PSScriptRoot\.."
$publishDir  = "$projectRoot\publish"

# ---- Stop if running -------------------------------------------------------
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task -and $task.State -eq 'Running') {
    Write-Host "Stopping task..."
    Stop-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2
}

# ---- Publish ---------------------------------------------------------------
Write-Host "Publishing..."
dotnet publish "$projectRoot" -c Release -r win-x64 --self-contained true -o "$publishDir"
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed."; exit 1 }

# ---- Start -----------------------------------------------------------------
if ($task) {
    Write-Host "Starting task..."
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2
    $newState = (Get-ScheduledTask -TaskName $taskName).State
    Write-Host "State: $newState" -ForegroundColor $(if ($newState -eq 'Running') { 'Green' } else { 'Yellow' })
} else {
    Write-Host "Task not registered - run 'install.ps1' first." -ForegroundColor Yellow
}

# ---- Watcher -----------------------------------------------------------------
$maxWaitSeconds = 120

$host.UI.RawUI.WindowTitle = "SapServer Log"

Write-Host "SapServer Log Watcher" -ForegroundColor Cyan
Write-Host "Waiting for today's log file..." -ForegroundColor DarkGray

$waited = 0
while ($waited -lt $maxWaitSeconds) {
    $logFile = Join-Path $publishDir "logs\sapserver-$(Get-Date -Format 'yyyyMMdd').log"
    if (Test-Path $logFile) { break }
    Start-Sleep -Seconds 2
    $waited += 2
}

if (-not (Test-Path $logFile)) {
    Write-Host "Log file not found after ${maxWaitSeconds}s: $logFile" -ForegroundColor Yellow
    Write-Host "Server may not have started yet. Press any key to exit." -ForegroundColor DarkGray
    $null = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

Write-Host "Tailing: $logFile" -ForegroundColor Green
Write-Host ("-" * 80)
Get-Content $logFile -Wait -Tail 50