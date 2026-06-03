# uninstall.ps1 — Remove the SapServer scheduled task.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$taskName = 'SapServer'

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "Task '$taskName' is not registered." -ForegroundColor Yellow
    exit 0
}

$info = Get-ScheduledTaskInfo -TaskName $taskName
if ($info.LastTaskResult -eq 267009) {
    # 267009 = STILL_RUNNING — stop it first
    Stop-ScheduledTask -TaskName $taskName
}

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Write-Host "Task '$taskName' removed." -ForegroundColor Green
