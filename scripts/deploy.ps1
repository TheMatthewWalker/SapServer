# deploy.ps1 - Stop the app pool, rebuild, publish, and restart it.
# Run as Administrator.
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

# See install.ps1's comment: WebAdministration's IIS:\ PSDrive isn't created
# when loaded through PowerShell 7+'s Windows PowerShell Compatibility layer.
if ($PSVersionTable.PSEdition -eq 'Core') {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}

Import-Module WebAdministration

$appPoolName = 'SapServer'
$siteName    = 'SapServer'
$projectRoot = "$PSScriptRoot\.."
$publishDir  = "$projectRoot\publish"
$healthUrl   = 'http://localhost:7200/health'

# ---- Stop if running -------------------------------------------------------
$poolExists = Test-Path "IIS:\AppPools\$appPoolName"
if ($poolExists -and (Get-WebAppPoolState -Name $appPoolName).Value -eq 'Started') {
    Write-Host "Stopping app pool..."
    Stop-WebAppPool -Name $appPoolName
    Start-Sleep -Seconds 2
}

# ---- Publish ---------------------------------------------------------------
# net48 is framework-dependent, not self-contained - win-x64/--self-contained
# don't apply the way they did for the old ASP.NET Core publish. Any modern
# Windows Server already ships .NET Framework 4.8; PlatformTarget=x64 in
# SapServer.csproj still governs bitness (matches the app pool's
# enable32BitAppOnWin64=$false set by install.ps1).
Write-Host "Publishing..."
dotnet publish "$projectRoot\SapServer.csproj" -c Release -o "$publishDir"
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed."; exit 1 }

# ---- Start -----------------------------------------------------------------
if ($poolExists) {
    Write-Host "Starting app pool..."
    Start-WebAppPool -Name $appPoolName
    Start-Sleep -Seconds 2
    $newState = (Get-WebAppPoolState -Name $appPoolName).Value
    Write-Host "State: $newState" -ForegroundColor $(if ($newState -eq 'Started') { 'Green' } else { 'Yellow' })

    # ---- Warm-up ------------------------------------------------------------
    # IIS's OnDemand start mode means the pool being "Started" only means the
    # worker process shell is up - Startup.Configuration (DI/SAP-pool wiring,
    # the first Serilog log line) doesn't actually run until the first real
    # HTTP request arrives. install.ps1's AlwaysRunning/preloadEnabled +
    # web.config's <applicationInitialization> make IIS send that request
    # itself where the Application Initialization module is installed, but
    # this sends one directly regardless, so the app is confirmed up (and
    # something has actually shown up in today's log) before you go looking
    # for it - matching the manual "hit /health and it started" workaround.
    # /health is the unauthenticated liveness endpoint, so no bearer token is
    # needed here.
    Write-Host "Warming up (GET $healthUrl)..."
    $warmedUp = $false
    for ($i = 0; $i -lt 5 -and -not $warmedUp; $i++) {
        Start-Sleep -Seconds 2
        try {
            $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
            Write-Host "App responded: $($response.data.status)" -ForegroundColor Green
            $warmedUp = $true
        } catch {
            Write-Host "  not up yet ($($_.Exception.Message))" -ForegroundColor DarkGray
        }
    }
    if (-not $warmedUp) {
        Write-Host "App didn't respond to warm-up after 10s - check the log / Event Viewer for a startup error." -ForegroundColor Yellow
    }
} else {
    Write-Host "App pool not registered - run 'install.ps1' first." -ForegroundColor Yellow
}
