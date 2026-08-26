# compare-load-test-results.ps1 - Print two load-test-transfer-orders.ps1 results
# files (e.g. load-test-results-old.json / load-test-results-new.json) side by side
# with a percentage delta per metric, so a capacity/throughput comparison between
# the old and new SapServer doesn't have to be done by eye.
#
# Example:
#   pwsh ./scripts/compare-load-test-results.ps1 -OldResultsFile .\load-test-results-old.json -NewResultsFile .\load-test-results-new.json

param(
    [Parameter(Mandatory)] [string] $OldResultsFile,
    [Parameter(Mandatory)] [string] $NewResultsFile
)

$ErrorActionPreference = 'Stop'

$old = Get-Content $OldResultsFile | ConvertFrom-Json
$new = Get-Content $NewResultsFile | ConvertFrom-Json

function Get-Delta {
    param($OldVal, $NewVal, [bool]$HigherIsBetter)
    if ($OldVal -eq 0) { return "n/a" }
    $pct = [Math]::Round((($NewVal - $OldVal) / [Math]::Abs($OldVal)) * 100, 1)
    $sign = if ($pct -ge 0) { "+" } else { "" }
    $isBetter = if ($HigherIsBetter) { $pct -ge 0 } else { $pct -le 0 }
    $tag = if ($isBetter) { "better" } else { "worse" }
    return "$sign$pct% ($tag)"
}

function Write-Row {
    param($Metric, $OldVal, $NewVal, $DeltaText)
    Write-Host ("{0,-16} {1,15} {2,15} {3,22}" -f $Metric, $OldVal, $NewVal, $DeltaText)
}

Write-Host ""
Write-Host ("{0,-16} {1,15} {2,15} {3,22}" -f "Metric", $old.Label, $new.Label, "Change") -ForegroundColor Cyan
Write-Host ("-" * 71)
Write-Row "Requests/sec"   $old.RequestsPerSec $new.RequestsPerSec (Get-Delta $old.RequestsPerSec $new.RequestsPerSec $true)
Write-Row "Total seconds"  $old.TotalSeconds   $new.TotalSeconds   (Get-Delta $old.TotalSeconds   $new.TotalSeconds   $false)
Write-Row "Latency p50 ms" $old.LatencyP50Ms   $new.LatencyP50Ms   (Get-Delta $old.LatencyP50Ms   $new.LatencyP50Ms   $false)
Write-Row "Latency p95 ms" $old.LatencyP95Ms   $new.LatencyP95Ms   (Get-Delta $old.LatencyP95Ms   $new.LatencyP95Ms   $false)
Write-Row "Latency p99 ms" $old.LatencyP99Ms   $new.LatencyP99Ms   (Get-Delta $old.LatencyP99Ms   $new.LatencyP99Ms   $false)
Write-Row "Latency max ms" $old.LatencyMaxMs   $new.LatencyMaxMs   (Get-Delta $old.LatencyMaxMs   $new.LatencyMaxMs   $false)
Write-Row "Succeeded"      $old.Succeeded      $new.Succeeded      ""
Write-Row "Failed"         $old.Failed         $new.Failed         ""
Write-Host ""

if ($old.ErrorBreakdown) { Write-Host "$($old.Label) errors: $($old.ErrorBreakdown)" -ForegroundColor Yellow }
if ($new.ErrorBreakdown) { Write-Host "$($new.Label) errors: $($new.ErrorBreakdown)" -ForegroundColor Yellow }

if ($old.Concurrency -ne $new.Concurrency) {
    Write-Host ""
    Write-Host "WARNING: the two runs used different -Concurrency ($($old.Concurrency) vs $($new.Concurrency)) - the comparison above isn't apples-to-apples." -ForegroundColor Red
}
