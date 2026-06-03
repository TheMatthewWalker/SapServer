# start.ps1 — Start the SapServer scheduled task.
$ErrorActionPreference = 'Stop'
$taskName = 'SapServer'

Write-Host "Starting '$taskName'..."
Start-ScheduledTask -TaskName $taskName

Start-Sleep -Seconds 2
$info = Get-ScheduledTaskInfo -TaskName $taskName
$state = (Get-ScheduledTask -TaskName $taskName).State

Write-Host "State : $state" -ForegroundColor $(if ($state -eq 'Running') { 'Green' } else { 'Yellow' })
