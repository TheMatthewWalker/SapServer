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
