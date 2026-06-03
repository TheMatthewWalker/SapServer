# watch-log.ps1 - Tail today's SapServer log file.
# Opens in a visible terminal window at logon via the SapServerLog scheduled task.

$publishDir = "$PSScriptRoot\..\publish"
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
