# status.ps1 — Show the current state of the SapServer scheduled task and recent logs.

$taskName = 'SapServer'

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "Task '$taskName' is NOT registered." -ForegroundColor Red
    exit 0
}

$info  = Get-ScheduledTaskInfo -TaskName $taskName
$state = $task.State

$colour = switch ($state) {
    'Running' { 'Green'  }
    'Ready'   { 'Yellow' }
    default   { 'Red'    }
}

Write-Host "Task    : $($task.TaskName)"            -ForegroundColor Cyan
Write-Host "State   : $state"                       -ForegroundColor $colour
Write-Host "Last run: $($info.LastRunTime)"
Write-Host "Last result: 0x$($info.LastTaskResult.ToString('X'))"

# Show last 20 lines from the most recent log file
$logDir  = "$PSScriptRoot\..\publish\logs"
$logFile = Get-ChildItem $logDir -Filter "*.log" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1

if ($logFile) {
    Write-Host ""
    Write-Host "--- Recent log ($($logFile.Name)) ---" -ForegroundColor Cyan
    Get-Content $logFile.FullName -Tail 20
} else {
    Write-Host ""
    Write-Host "No log files found in $logDir" -ForegroundColor Yellow
}
