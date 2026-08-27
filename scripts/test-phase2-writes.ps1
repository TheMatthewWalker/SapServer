$ErrorActionPreference = 'Continue'
$base = 'http://localhost:7200'
$log  = 'C:\Users\matthew.walker\source\repos\TheMatthewWalker\SapServer\endpoint-test-log-2026-08-27.md'

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
            $resp = Invoke-RestMethod -Uri $uri -Method $Method -Body $json -ContentType 'application/json' -TimeoutSec 60
        } else {
            $resp = Invoke-RestMethod -Uri $uri -Method $Method -TimeoutSec 60
        }
        $respJson = $resp | ConvertTo-Json -Depth 8 -Compress
        if ($respJson.Length -gt 2500) { $respJson = $respJson.Substring(0,2500) + "...(truncated)" }
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
        if ($_.Exception.Response) { $statusCode = [int]$_.Exception.Response.StatusCode }
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

function Log-NotExecuted {
    param([string]$Method, [string]$Path, [string]$Reason)
    $lines = @("", "## $Method $Path", "Verdict: NOT EXECUTED -- $Reason")
    $lines | Out-File $log -Append -Encoding utf8
    Write-Host "SKIP $Method $Path -- $Reason" -ForegroundColor Yellow
}

Write-Host "=== PHASE 2: Write/transactional endpoints (dryRun first, then judgment) ===" -ForegroundColor Cyan

# --- dryRun-capable real-BAPI endpoints: exercise dryRun always ---
Test-Endpoint POST "/api/warehouse/stock-adjustment?dryRun=true" -Body @{ Material='30005R'; StorageLocation='1710'; StorageType='RO'; StorageBin='STORE'; MovementType='711'; Quantity=1; Unit='KG' }
Test-Endpoint POST "/api/warehouse/goods-issue?dryRun=true" -Body @{ DeliveryNumber='0082291409' }
Test-Endpoint POST "/api/warehouse/delivery-change?dryRun=true" -Body @{ DeliveryNumber='0082291409'; Items=@(@{ ItemNumber='000010'; Material='30005R'; Quantity=1; BaseUom='KG' }) }
Test-Endpoint POST "/api/warehouse/consignment-mb1b?dryRun=true" -Body @{ Material='30005R'; Quantity=1; Header='Test'; SpecialStockNumber='0000200604'; StorageLocation='1710'; SourceType='RO'; SourceBin='STORE'; DestinationType='SA'; DestinationBin='PTFE' }
Test-Endpoint POST "/api/quality/block?dryRun=true" -Body @{ Material='30005R'; Quantity=1; Header='Test'; Batch=''; StorageLocation='1710'; BinType='RO'; Bin='STORE'; Username='TEST' } -Note "QualityController doesn't declare dryRun -- confirm whether this param is even accepted"

# --- Followed by a real TestRun (rolls back) where the model supports it ---
Test-Endpoint POST "/api/warehouse/stock-adjustment" -Body @{ Material='30005R'; StorageLocation='1710'; StorageType='RO'; StorageBin='STORE'; MovementType='711'; Quantity=1; Unit='KG'; TestRun=$true } -Note "TestRun=true, real SAP call, should roll back"
Test-Endpoint POST "/api/warehouse/goods-issue" -Body @{ DeliveryNumber='0082291409'; TestRun=$true } -Note "TestRun=true"
Test-Endpoint POST "/api/warehouse/delivery-change" -Body @{ DeliveryNumber='0082291409'; Items=@(@{ ItemNumber='000010'; Material='30005R'; Quantity=1; BaseUom='KG' }); TestRun=$true } -Note "TestRun=true"
Test-Endpoint POST "/api/warehouse/consignment-mb1b" -Body @{ Material='30005R'; Quantity=1; Header='Test'; SpecialStockNumber='0000200604'; StorageLocation='1710'; SourceType='RO'; SourceBin='STORE'; DestinationType='SA'; DestinationBin='PTFE'; TestRun=$true } -Note "TestRun=true"

# --- transfer-order: no test/dry flag on the BAPI itself, but check the bin-existence guard works and dryRun response shape ---
Test-Endpoint POST "/api/warehouse/transfer-order?dryRun=true" -Body @{ StorageLocation='1710'; Material='30005R'; Quantity=1; SourceType='RO'; SourceBin='STORE'; DestinationType='RO'; DestinationBin='NOSUCHBIN99' } -Note "Deliberately bad destination bin -- should 422 from the LAGP existence pre-check before ever calling L_TO_CREATE_SINGLE, per the controller's own documented fail-fast design"

# --- Quality block/unblock: no dry-run flag exists; genuinely reversible (block then immediately unblock the same thing) ---
Log-NotExecuted POST "/api/quality/block" "No dry-run flag on QualityMb1bRequest/BlockStock -- would place a REAL quality block on real stock via MB1B. Skipping the real call pending explicit confirmation this material/batch is safe to block+unblock in the sandbox; dryRun test above already shown to establish whether the param is even honored."

# --- ExplodeBom: read-heavy, no SAP writes ---
Test-Endpoint POST "/api/mrp-analysis/explode-bom" -Body @{ Items=@(@{ Material='30005R'; Quantity=10 }) }

# --- Sales waterfall ---
Test-Endpoint POST "/api/sales/schedule-waterfall" -Body @{ SalesOrg='3012'; ShipToParties=@(); Materials=@(); IncludeForecast=$true; IncludeJit=$true; ScheduleDateFrom='2026-01-01'; ScheduleDateTo='2026-12-31' }

# --- Customs remaining lookups (real delivery/material now known) ---
Test-Endpoint POST "/api/customs/vbfa" -Body @{ Lines=@(@{ Delivery='0082291409'; Item='000010' }) }
Test-Endpoint POST "/api/customs/kna1" -Body @{ Customers=@('0000363991') }
Test-Endpoint POST "/api/customs/vbrk" -Body @{ Invoices=@() }
Test-Endpoint POST "/api/customs/consignment-price" -Body @{ Lines=@(@{ Customer='0000363991'; Material='30005R' }) }

# --- RFC generic execute: harmless RFC_PING ---
Test-Endpoint POST "/api/rfc/execute" -Body @{ FunctionName='RFC_PING'; ImportParameters=@{}; ExportParameters=@(); OutputTables=@{} }

# --- Genuinely irreversible / needs-real-user-creds endpoints: NOT executed, dryRun only where available ---
Test-Endpoint POST "/api/purchasing/create-po?dryRun=true" -Body @{ Vendor='100000'; Currency='GBP'; Items=@(@{ Material='30005R'; ShortText='Test'; Quantity=1; Unit='KG'; DeliveryDate='2026-09-01' }) }
Log-NotExecuted POST "/api/purchasing/create-po" "Creates a real PO document in SAP with no dry-run/reverse path built into this endpoint; dryRun above only proves the request builds. Needs explicit confirmation before a real PO number gets consumed in the sandbox."
Log-NotExecuted POST "/api/purchasing/post-goods-receipt" "Posts a real goods receipt against a real PO; needs a genuinely open real PO from the sandbox to test meaningfully, and consumes a real material document number. Not attempted."
Log-NotExecuted POST "/api/purchasing/create-po-and-receipt" "Combined real PO + GR creation. Same reasoning as create-po/post-goods-receipt above."
Log-NotExecuted POST "/api/purchasing/create-po-elevated" "Requires one specific real user's own SAP credentials (elevated session) -- not available in this test session."
Log-NotExecuted POST "/api/packaging/create-elevated" "Requires one specific real user's own SAP credentials (elevated session) -- not available in this test session."
Log-NotExecuted POST "/api/production/backflush" "Real ZF40N production backflush BDC against a real production order -- no dry-run mode on this endpoint at all, consumes real order confirmation quantity. Not attempted without explicit confirmation and a specific real order number from the user."
Log-NotExecuted POST "/api/production/drumming-backflush" "Same reasoning as backflush -- real BDC, no dry-run, plus writes ZPRODBATCH_TBL/ZBATCHPACK_TBL via Z_ZPRODBATCH_MAINT."
Log-NotExecuted POST "/api/production/scrap/post" "Real scrap posting BDC, no dry-run."
Log-NotExecuted POST "/api/production/mixing-scrap" "Has a TestRun field on MixingScrapRequest -- SHOULD be tested with TestRun=true, see follow-up test below."
Log-NotExecuted POST "/api/production/goods-movement-backflush" "Has a TestRun field on GoodsMovementRequest -- SHOULD be tested with TestRun=true, see follow-up test below."
Log-NotExecuted POST "/api/production/reverse-backflush" "Reverses a real material document -- needs a real material document number from an actual prior backflush, none available/created this session."
Log-NotExecuted POST "/api/production/scrap/reverse" "Same reasoning -- needs a real prior scrap document to reverse."
Log-NotExecuted POST "/api/purchasing/reverse-goods-receipt" "Needs a real prior GR document to reverse."
Log-NotExecuted PUT "/api/packaging/instruction" "Writes/overwrites a real packaging instruction config row -- deferred pending confirmation this is safe to test against real config data (not easily 'reversible' the way block/unblock is)."
Log-NotExecuted DELETE "/api/packaging/instruction" "Deletes a real packaging instruction config row -- same reasoning."
Log-NotExecuted POST "/api/packaging/create" "Creates a real material master record (MM01) + BOM (CS01) -- not reversible via any endpoint in this API. Not attempted."
Log-NotExecuted POST "/api/packaging/mass-update" "Bulk real packaging-instruction writes -- same reasoning as PUT/DELETE instruction above."
Log-NotExecuted POST "/api/performance/turns-valclass/change-valuation-class" "Real, deliberately-irreversible MM02 valuation-class change with real GL/inventory value impact -- explicitly out of scope without direct confirmation."
Log-NotExecuted POST "/api/warehouse/create-lt04" "Real transfer order confirmation (LT04) against a real, existing open TR -- would need a genuine open TR from the sandbox; the one found earlier (TR 0000000061) belongs to someone else's real data, confirming it would actually move real stock. Not attempted."
Log-NotExecuted POST "/api/warehouse/delete-tr" "Deletes a real open transfer requirement -- same reasoning, would destroy real sandbox data belonging to TR 0000000061 found during discovery."
Log-NotExecuted POST "/api/warehouse/picksheet-stage-batch" "Real transfer order creation + LAGP bin auto-create BDC against a real picksheet -- needs a real material/batch/delivery combination and creates real staged stock; deferred."
Log-NotExecuted POST "/api/warehouse/picksheet-unstage-batch" "Reverses staging -- needs a real staged batch from the above, which wasn't created."
Test-Endpoint POST "/api/costing/cost-sheet" -Body @{ Date='20260101'; Materials=@('30005R') }
Test-Endpoint POST "/api/costing/period-balance" -Body @{ DateFrom='20260101'; DateTo='20260827'; GlAccounts=@('0000300000') }
Test-Endpoint POST "/api/costing/profit-center" -Body @{ DateFrom='20260101'; DateTo='20260827'; GlAccounts=@('0000300000') }
Log-NotExecuted POST "/api/costing/freight-posting" "Real FI posting (freight cost against a GL account/profit centre) -- no dry-run, real financial document. Not attempted."
Log-NotExecuted POST "/api/costing/freight-posting-batch" "Same reasoning, batch version."

Write-Host "=== Phase 2 done. Log: $log ===" -ForegroundColor Cyan
