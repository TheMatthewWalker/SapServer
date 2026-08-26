# load-test-transfer-orders.ps1 - Fire a batch of real POST /api/warehouse/transfer-order
# requests at a target SapServer instance with controlled concurrency, and report
# throughput/latency/error-rate. Run it once against the old (production) server and
# once against a local new-rebuild instance with the SAME -InputCsv and a different
# -Label, then use compare-load-test-results.ps1 to diff the two.
#
# Requires PowerShell 7+ (ForEach-Object -Parallel) - run via `pwsh`, not Windows
# PowerShell 5.1. Unlike install.ps1/deploy.ps1/etc., this script never touches
# WebAdministration/IIS:\, so there's no PS5.1-relaunch requirement here.
#
# Every row in -InputCsv becomes one REAL CreateTransferOrderRequest - there is no
# dry-run mode on this endpoint (see WarehouseController.CreateTransferOrder), so
# each request that passes the destination-bin check attempts a real LT01/LT04
# transfer order in SAP. Use the same source and destination bin per row if the
# intent is a net-zero-stock-movement test/cleanup - the script does not enforce
# or check this, it just sends whatever the CSV contains.
#
# CSV columns (header row required): StorageLocation,Material,Quantity,SourceType,
# SourceBin,DestinationType,DestinationBin,Batch,StockCategory,SpecialStockIndicator,
# SpecialStockNumber - the last four are optional, leave the cell blank if unused.
#
# Example:
#   pwsh ./scripts/load-test-transfer-orders.ps1 -BaseUrl http://localhost:7200 `
#       -InputCsv .\cleanup-1000.csv -Label new -Concurrency 10
#   pwsh ./scripts/load-test-transfer-orders.ps1 -BaseUrl https://old-prod-host `
#       -InputCsv .\cleanup-1000.csv -Label old -Concurrency 10 -BearerToken $token

param(
    [Parameter(Mandatory)] [string] $BaseUrl,      # e.g. http://localhost:7200, or the old server's address
    [Parameter(Mandatory)] [string] $InputCsv,
    [Parameter(Mandatory)] [string] $Label,        # names the results file, e.g. "old" / "new"
    [int]    $Concurrency = 10,                    # matches SapNco:PoolSize's default - see CLAUDE.md
    [string] $BearerToken = $null,                 # omit to rely on the target's Auth:DevBypassAuth
    [int]    $Count = 0,                           # 0 = use every row in the CSV
    [int]    $TimeoutSec = 60,
    [switch] $Force                                # skip the confirmation prompt
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "This script needs PowerShell 7+ for ForEach-Object -Parallel. Run it via 'pwsh', not Windows PowerShell 5.1."
    exit 1
}

if (-not (Test-Path $InputCsv)) {
    Write-Error "Input CSV not found: $InputCsv"
    exit 1
}

$rows = Import-Csv -Path $InputCsv
if ($Count -gt 0) { $rows = $rows | Select-Object -First $Count }

$targetUrl = "$($BaseUrl.TrimEnd('/'))/api/warehouse/transfer-order"

Write-Host ""
Write-Host "Target:      $targetUrl" -ForegroundColor Cyan
Write-Host "Requests:    $($rows.Count)" -ForegroundColor Cyan
Write-Host "Concurrency: $Concurrency" -ForegroundColor Cyan
Write-Host "Label:       $Label" -ForegroundColor Cyan
Write-Host ""
Write-Host "Every request is a REAL SAP transfer order - this endpoint has no dry-run mode." -ForegroundColor Yellow

if (-not $Force) {
    $confirm = Read-Host "Type YES to continue"
    if ($confirm -ne 'YES') { Write-Host "Aborted."; exit 1 }
}

$headers = @{ 'Content-Type' = 'application/json' }
if ($BearerToken) { $headers['Authorization'] = "Bearer $BearerToken" }

Write-Host ""
Write-Host "Running..." -ForegroundColor DarkGray
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$results = $rows | ForEach-Object -Parallel {
    $row      = $_
    $url      = $using:targetUrl
    $headers  = $using:headers
    $timeout  = $using:TimeoutSec

    $bodyObj = @{
        storageLocation       = $row.StorageLocation
        material               = $row.Material
        quantity                = [decimal]$row.Quantity
        sourceType            = $row.SourceType
        sourceBin               = $row.SourceBin
        destinationType       = $row.DestinationType
        destinationBin          = $row.DestinationBin
    }
    if ($row.Batch)                 { $bodyObj.batch                 = $row.Batch }
    if ($row.StockCategory)         { $bodyObj.stockCategory         = $row.StockCategory }
    if ($row.SpecialStockIndicator) { $bodyObj.specialStockIndicator = $row.SpecialStockIndicator }
    if ($row.SpecialStockNumber)    { $bodyObj.specialStockNumber    = $row.SpecialStockNumber }
    $body = $bodyObj | ConvertTo-Json

    $requestSw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $body -TimeoutSec $timeout -ContentType 'application/json'
        $requestSw.Stop()
        [PSCustomObject]@{
            Success   = [bool]$response.success
            ErrorCode = if (-not $response.success) { $response.error.code } else { $null }
            LatencyMs = $requestSw.Elapsed.TotalMilliseconds
            Material  = $row.Material
        }
    } catch {
        $requestSw.Stop()
        $statusCode = $null
        if ($_.Exception.Response) { $statusCode = [int]$_.Exception.Response.StatusCode }
        [PSCustomObject]@{
            Success   = $false
            ErrorCode = if ($statusCode) { "HTTP_$statusCode" } else { "REQUEST_FAILED" }
            LatencyMs = $requestSw.Elapsed.TotalMilliseconds
            Material  = $row.Material
        }
    }
} -ThrottleLimit $Concurrency

$sw.Stop()

$succeeded = @($results | Where-Object Success)
$failed    = @($results | Where-Object { -not $_.Success })
$latencies = @($results | Select-Object -ExpandProperty LatencyMs | Sort-Object)

function Get-Percentile {
    param($Sorted, $P)
    if ($Sorted.Count -eq 0) { return 0 }
    $idx = [Math]::Ceiling($P / 100 * $Sorted.Count) - 1
    $idx = [Math]::Max(0, [Math]::Min($idx, $Sorted.Count - 1))
    return [Math]::Round($Sorted[$idx], 1)
}

$summary = [PSCustomObject]@{
    Label          = $Label
    BaseUrl        = $BaseUrl
    Concurrency    = $Concurrency
    TotalRequests  = $results.Count
    Succeeded      = $succeeded.Count
    Failed         = $failed.Count
    TotalSeconds   = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
    RequestsPerSec = [Math]::Round($results.Count / $sw.Elapsed.TotalSeconds, 2)
    LatencyP50Ms   = Get-Percentile $latencies 50
    LatencyP95Ms   = Get-Percentile $latencies 95
    LatencyP99Ms   = Get-Percentile $latencies 99
    LatencyMaxMs   = if ($latencies.Count -gt 0) { [Math]::Round(($latencies | Select-Object -Last 1), 1) } else { 0 }
    ErrorBreakdown = (($failed | Group-Object ErrorCode | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')
}

Write-Host ""
Write-Host "=== Results: $Label ===" -ForegroundColor Green
# Explicit Write-Host lines rather than Format-List/Format-Table - those rely on
# console width detection, which produces empty output in some non-interactive/
# headless hosts (confirmed while building this script).
foreach ($prop in $summary.PSObject.Properties) {
    Write-Host ("{0,-15}: {1}" -f $prop.Name, $prop.Value)
}

$outFile = "load-test-results-$Label.json"
$summary | ConvertTo-Json | Set-Content -Path $outFile
Write-Host "Saved: $outFile" -ForegroundColor DarkGray
