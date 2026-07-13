# stop.ps1 - Stop the SapServer scheduled task.
$ErrorActionPreference = 'Stop'

$taskName = 'SapServer'

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task -or $task.State -ne 'Running') {
    $state = if ($task) { $task.State } else { 'not registered' }
    Write-Host "Task '$taskName' is not running (state: $state)." -ForegroundColor Yellow
    exit 0
}

Write-Host "Stopping '$taskName'..."
Stop-ScheduledTask -TaskName $taskName
Write-Host "Stopped." -ForegroundColor Green
