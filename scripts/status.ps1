# status.ps1 — Show the current state of the SapServer IIS app pool/site and recent logs.

# See install.ps1's comment: WebAdministration's IIS:\ PSDrive isn't created
# when loaded through PowerShell 7+'s Windows PowerShell Compatibility layer.
if ($PSVersionTable.PSEdition -eq 'Core') {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}

Import-Module WebAdministration

$appPoolName = 'SapServer'
$siteName    = 'SapServer'

if (-not (Test-Path "IIS:\AppPools\$appPoolName")) {
    Write-Host "App pool '$appPoolName' is NOT registered." -ForegroundColor Red
    exit 0
}

$poolState = (Get-WebAppPoolState -Name $appPoolName).Value
$site      = Get-Website -Name $siteName -ErrorAction SilentlyContinue

$colour = switch ($poolState) {
    'Started' { 'Green'  }
    'Stopped' { 'Red'    }
    default   { 'Yellow' }
}

Write-Host "App pool : $appPoolName"        -ForegroundColor Cyan
Write-Host "State    : $poolState"          -ForegroundColor $colour
if ($site) {
    Write-Host "Site     : $($site.Name) (state: $($site.State))"
    Write-Host "Bindings : $($site.Bindings.Collection -join ', ')"
}

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
