$ErrorActionPreference = 'Continue'
$base = 'http://localhost:7200'
$log  = 'C:\Users\matthew.walker\source\repos\TheMatthewWalker\SapServer\endpoint-test-log-2026-08-27.md'

$header = "# SapServer endpoint test log - " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + " - Development mode, SAP sandbox ksnoka20/KAQ/100"
$header | Out-File $log -Encoding utf8

function Test-Endpoint {
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null,
        [string]$Note = ''
    )
    $uri = "$base$Path"
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("")
    $lines.Add("## $Method $Path")
    if ($Note) { $lines.Add("Note: $Note") }
    if ($Body) {
        $jsonCompact = $Body | ConvertTo-Json -Depth 8 -Compress
        $lines.Add("Request: $jsonCompact")
    }
    try {
        if ($Body) {
            $json = $Body | ConvertTo-Json -Depth 8
            $resp = Invoke-RestMethod -Uri $uri -Method $Method -Body $json -ContentType 'application/json' -TimeoutSec 45
        } else {
            $resp = Invoke-RestMethod -Uri $uri -Method $Method -TimeoutSec 45
        }
        $respJson = $resp | ConvertTo-Json -Depth 8 -Compress
        if ($respJson.Length -gt 2000) { $respJson = $respJson.Substring(0,2000) + "...(truncated)" }
        $lines.Add("Result: 200 OK -- $respJson")
        if ($resp.success -eq $false) {
            $verdict = "BUG FOUND: success=false with HTTP 200 -- " + $resp.error.message
        } else {
            $verdict = "PASS"
        }
        $lines.Add("Verdict: $verdict")
        Write-Host "OK   $Method $Path" -ForegroundColor Green
    } catch {
        $statusCode = $null
        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        $errBody = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $errBody = $_.ErrorDetails.Message }
        $lines.Add("Result: HTTP $statusCode -- " + $_.Exception.Message)
        if ($errBody) { $lines.Add("Body: $errBody") }
        $lines.Add("Verdict: ERROR - status $statusCode")
        Write-Host "ERR  $Method $Path -> $statusCode $($_.Exception.Message)" -ForegroundColor Red
    }
    $lines | Out-File $log -Append -Encoding utf8
    return $resp
}

Write-Host "=== PHASE 1: Read-only / discovery endpoints ===" -ForegroundColor Cyan

Test-Endpoint GET "/api/warehouse/stock?Material=30005R" -Note "Already confirmed working pre-script"
Test-Endpoint GET "/api/warehouse/im-stock?StorageLocation=1716"
Test-Endpoint GET "/api/warehouse/stock/totals?Material=30005R"
Test-Endpoint GET "/api/warehouse/stock/bins?Material=30005R"
Test-Endpoint GET "/api/warehouse/open-transfer-requirements"
Test-Endpoint GET "/api/warehouse/bin-storage-types?bin=STORE"
Test-Endpoint GET "/api/warehouse/tr-cleanup-candidates"
Test-Endpoint GET "/api/warehouse/zdelflag/likp-ablad/0080001234"
Test-Endpoint GET "/api/warehouse/zdelflag/lips-items/0080001234"

Test-Endpoint GET "/api/logistics/picksheets/open"

Test-Endpoint GET "/api/quality/display?Material=30005R"

Test-Endpoint GET "/api/performance/stock"
Test-Endpoint GET "/api/performance/agreements?horizonDays=30"
Test-Endpoint GET "/api/performance/invoicing"
Test-Endpoint GET "/api/performance/otif"
Test-Endpoint GET "/api/performance/turns-valclass/valuation-classes"

Test-Endpoint GET "/api/consignment/gr?sapVendorNumber=100000"
Test-Endpoint GET "/api/consignment/stock"

Test-Endpoint GET "/api/mrp-analysis/consumption-by-year"
Test-Endpoint GET "/api/mrp-analysis/goods-receipt-history"

Test-Endpoint GET "/api/packaging/30005R/exists"
Test-Endpoint GET "/api/packaging/30005R/description"
Test-Endpoint GET "/api/packaging/30005R/mara"
Test-Endpoint GET "/api/packaging/30005R/bom"
Test-Endpoint GET "/api/packaging/30005R/customers"
Test-Endpoint GET "/api/packaging/30005R/instruction"

Test-Endpoint GET "/api/production/bom" -Body @{ Material = '30005R' }
Test-Endpoint GET "/api/production/check-profit-centre" -Body @{ Material = '30005R' }
Test-Endpoint GET "/api/production/check-profit-centres" -Body @{ Materials = @('30005R') }
Test-Endpoint GET "/api/production/find-cost-collector" -Body @{ Material = '30005R' }

Test-Endpoint POST "/api/customs/lips" -Body @{ Deliveries = @('0080001234') }
Test-Endpoint POST "/api/customs/likp" -Body @{ Deliveries = @('0080001234') }
Test-Endpoint POST "/api/customs/marc" -Body @{ Materials = @('30005R') }

Test-Endpoint POST "/api/warehouse/picksheet-materials" -Body @{ Deliveries = @('0080001234') }
Test-Endpoint POST "/api/warehouse/picksheet-stock" -Body @{ Materials = @('30005R') }

Test-Endpoint GET "/api/function/params" -Body @{ FunctionName = 'RFC_PING' }

Test-Endpoint GET "/api/rfc/status"

Write-Host "=== Phase 1 done. Log: $log ===" -ForegroundColor Cyan
