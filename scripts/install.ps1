# install.ps1 - Register SapServer as an IIS site + application pool.
#
# Replaces the old Task Scheduler + self-contained-Kestrel deploy model.
# That model existed solely because the SAPFunctions64 COM OCX needed an
# interactive Windows session (IIS app pools run in Session 0, which caused
# AccessViolationException). SAP NCo is not COM and has no such constraint,
# so this app can now run as an ordinary IIS-hosted ASP.NET application -
# see CLAUDE.md's "SAP NCo Spike" / architecture sections for the full
# rationale behind the .NET Framework 4.8 + NCo rebuild.
#
# Run as Administrator, on a machine with IIS + its PowerShell management
# tools installed:
#   - Windows Server: Install-WindowsFeature Web-Server, Web-Asp-Net45, Web-Scripting-Tools
#     (Web-Scripting-Tools specifically is what provides the WebAdministration
#     module below - easy to miss, since Web-Server/Web-Asp-Net45 alone install
#     IIS itself but not its PowerShell cmdlets.)
#   - Windows 10/11 (client): Enable-WindowsOptionalFeature -Online -All -FeatureName
#     IIS-WebServerRole, IIS-WebServer, IIS-CommonHttpFeatures, IIS-HttpErrors,
#     IIS-ApplicationDevelopment, IIS-NetFxExtensibility45, IIS-ISAPIExtensions,
#     IIS-ISAPIFilter, IIS-ASPNET45, IIS-ManagementConsole, IIS-ManagementScriptingTools
#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

# WebAdministration's IIS:\ PSDrive isn't created when the module loads
# through PowerShell 7+'s Windows PowerShell Compatibility layer
# (WinPSCompatSession, per the warning Import-Module prints under pwsh) -
# only cmdlets/functions are proxied there, not PSProvider drives - so every
# "IIS:\..." path below would fail with "Cannot find drive" even though
# Import-Module itself appears to succeed. Re-launch under real Windows
# PowerShell 5.1, where WebAdministration's provider loads natively.
if ($PSVersionTable.PSEdition -eq 'Core') {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}

try {
    Import-Module WebAdministration -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "IIS's PowerShell management tools aren't installed on this machine." -ForegroundColor Red
    Write-Host "On Windows Server:  Install-WindowsFeature Web-Server, Web-Asp-Net45, Web-Scripting-Tools" -ForegroundColor Yellow
    Write-Host "On Windows 10/11:   Enable-WindowsOptionalFeature -Online -All -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-CommonHttpFeatures, IIS-HttpErrors, IIS-ApplicationDevelopment, IIS-NetFxExtensibility45, IIS-ISAPIExtensions, IIS-ISAPIFilter, IIS-ASPNET45, IIS-ManagementConsole, IIS-ManagementScriptingTools" -ForegroundColor Yellow
    Write-Host ""
    throw
}

$siteName   = 'SapServer'
$appPoolName = 'SapServer'
$publishDir = (Resolve-Path "$PSScriptRoot\..\publish").Path
$port       = 7200

# ---- Machine environment variables ----------------------------------------
# ASPNETCORE_ENVIRONMENT has to be an env var - it's what Startup.cs's
# BuildConfiguration() uses to pick which appsettings.{Environment}.json
# layers on top of appsettings.json, so there's no config-file equivalent to
# put it in instead. A plain app pool recycle (Stop/Start-WebAppPool, or just
# deploy.ps1) is NOT enough to pick up a change to this - confirmed for real:
# new worker processes get their environment block from the Windows Process
# Activation Service (WAS), which caches the machine environment when WAS
# itself last started, not a live read of the registry on every recycle. A
# changed value here needs a full `iisreset` (which restarts WAS itself), or
# a machine reboot, before a new app pool worker will actually see it.
Write-Host "Setting environment variables..."
[System.Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Production', 'Machine')

# ---- Secrets ----------------------------------------------------------------
# Deliberately NOT prompted for here / set as env vars - Auth:JwtSecret and
# SapNco:ServiceAccount(s) (SAP service account credentials) live directly in
# appsettings.Production.json instead, same as every other setting. That
# file is already .gitignore'd, same protection env vars would have given,
# but much easier to maintain.
Write-Host ""
Write-Host "Reminder: fill in Auth:JwtSecret (shared with sql2005-bridge) and"        -ForegroundColor Yellow
Write-Host "SapNco:ServiceAccount / ServiceAccounts directly in"                     -ForegroundColor Yellow
Write-Host "appsettings.Production.json before starting the site - neither is"       -ForegroundColor Yellow
Write-Host "set via this script."                                                    -ForegroundColor Yellow

# ---- Application pool -------------------------------------------------------
Write-Host ""
Write-Host "Creating application pool '$appPoolName'..."
if (Test-Path "IIS:\AppPools\$appPoolName") {
    Write-Host "App pool already exists - leaving its settings as-is." -ForegroundColor DarkGray
} else {
    New-WebAppPool -Name $appPoolName | Out-Null
}
# Classic .NET Framework CLR (v4.0 covers 4.8) in Integrated pipeline mode -
# required for System.Web.Http / Microsoft.Owin.Host.SystemWeb's ASP.NET
# module integration.
Set-ItemProperty "IIS:\AppPools\$appPoolName" managedRuntimeVersion 'v4.0'
Set-ItemProperty "IIS:\AppPools\$appPoolName" managedPipelineMode 'Integrated'
# 64-bit worker process - matches SapServer.csproj's PlatformTarget=x64
# (needed for the SAP NCo native binaries, which are x64-only).
Set-ItemProperty "IIS:\AppPools\$appPoolName" enable32BitAppOnWin64 $false

# ---- Site ---------------------------------------------------------------
Write-Host "Creating site '$siteName' (physical path: $publishDir, port: $port)..."
if (Get-Website -Name $siteName -ErrorAction SilentlyContinue) {
    Write-Host "Site already exists - leaving its bindings as-is." -ForegroundColor DarkGray
} else {
    New-Website -Name $siteName -PhysicalPath $publishDir -ApplicationPool $appPoolName -Port $port | Out-Null
}


# ---- Eager startup (Application Initialization) -----------------------------
# By default the app pool's Start Mode is OnDemand and the site's application
# isn't preloaded - the worker process (and within it, Startup.Configuration's
# DI/SAP-pool wiring) doesn't actually run until the first real HTTP request
# arrives, not when the pool starts. Confirmed for real: after a deploy.ps1
# restart, the log stayed completely empty until a request was sent manually.
# AlwaysRunning + preloadEnabled here, paired with web.config's
# <applicationInitialization> warming /health, make IIS send its own internal
# warm-up request right after the pool starts, so the app (and deploy.ps1's
# own warm-up below) don't have to be the ones paying the first-request cold
# start.
Write-Host ""
Write-Host "Configuring eager startup (Application Initialization)..."
Set-ItemProperty "IIS:\AppPools\$appPoolName" startMode 'AlwaysRunning'
Set-WebConfigurationProperty -PSPath 'IIS:\' `
    -Filter "/system.applicationHost/sites/site[@name='$siteName']/application[@path='/']" `
    -Name preloadEnabled -Value $true

# Get-WindowsFeature (Server) and Get-WindowsOptionalFeature (client) are
# mutually exclusive depending on SKU - probe both defensively rather than
# assuming which one exists on this machine.
$appInitInstalled = $false
try {
    $appInitInstalled = (Get-WindowsFeature -Name Web-AppInit -ErrorAction Stop).InstallState -eq 'Installed'
} catch {
    try {
        $appInitInstalled = (Get-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -ErrorAction Stop).State -eq 'Enabled'
    } catch { }
}
if (-not $appInitInstalled) {
    Write-Host ""
    Write-Host "Application Initialization isn't installed - AlwaysRunning/preload are set," -ForegroundColor Yellow
    Write-Host "but IIS won't actually send the warm-up request without it (deploy.ps1's own" -ForegroundColor Yellow
    Write-Host "warm-up request still works either way). To install it:" -ForegroundColor Yellow
    Write-Host "  Windows Server:  Install-WindowsFeature Web-AppInit" -ForegroundColor Yellow
    Write-Host "  Windows 10/11:   Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Site registered on http://localhost:$port - for HTTPS, bind a" -ForegroundColor Yellow
Write-Host "certificate via IIS Manager or New-WebBinding + netsh http add sslcert" -ForegroundColor Yellow
Write-Host "(a one-time manual step; not automated here since it needs a real cert)." -ForegroundColor Yellow
Write-Host ""
Write-Host "Run 'deploy.ps1' to publish the app into $publishDir and start the site." -ForegroundColor Green
