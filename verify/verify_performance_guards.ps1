param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory
)

$ErrorActionPreference = "Stop"
$hexFillPath = Join-Path $ProjectDirectory "HexFill.cs"
$hadPath = Join-Path $ProjectDirectory "Commands\HadCommand.cs"
$acadVerifyPath = Join-Path $ProjectDirectory "verify\acad_verify.py"
$hexFill = Get-Content -LiteralPath $hexFillPath -Raw
$had = Get-Content -LiteralPath $hadPath -Raw
$acadVerify = Get-Content -LiteralPath $acadVerifyPath -Raw

$forbiddenPatterns = @(
    "CollectCirclesForEER",
    "DeleteCirclesForEER",
    "PointInPolygon(new Point2d(x, finalY), curve)"
)

foreach ($pattern in $forbiddenPatterns) {
    if ($hexFill.Contains($pattern) -or $had.Contains($pattern)) {
        Write-Error "Performance regression guard rejected forbidden pattern: $pattern"
        exit 1
    }
}

$requiredPatterns = @(
    "CircleSpatialIndex",
    "BuildNearestPointTree",
    "BuildCurveContainment",
    "CollectCircleRecordsInRegion",
    "CollectSelectedClosedRegionIds",
    "NearestNeighborBruteForceThreshold",
    "ApplyReplacement"
)

foreach ($pattern in $requiredPatterns) {
    if (-not $hexFill.Contains($pattern) -and -not $had.Contains($pattern)) {
        Write-Error "Performance regression guard is missing required optimized path: $pattern"
        exit 1
    }
}

Write-Host "HexFill performance regression guards verified."

foreach ($requiredPattern in @(
    "bool hasNewXData = false;",
    "insideValidAppSection && !skipCurrentApp",
    "ent.XData = hasNewXData ? newRb : null;",
    "EnsureRegAppsRegistered(tr, new[]",
    "HCLN_CLEARED_APP"
)) {
    if (-not $hexFill.Contains($requiredPattern)) {
        Write-Error "HNM undo/XData regression guard is missing required pattern: $requiredPattern"
        exit 1
    }
}

if ($hexFill.Contains("if (registeredApps.Contains(appName)) continue;")) {
    Write-Error "HNM undo/XData regression guard rejected stale RegApp cache short-circuit."
    exit 1
}

Write-Host "HexFill HNM undo/XData regression guards verified."

if ($acadVerify.Contains('("FILEDIA", 0),') -or
    $acadVerify.Contains('("SECURELOAD", 0),')) {
    Write-Error "AutoCAD verifier must not keep FILEDIA or SECURELOAD disabled for the full session."
    exit 1
}

foreach ($requiredPattern in @(
    'USER_INTERFACE_VARIABLE_VALUES',
    '_wait_owned_process_exit(timeout=20)',
    'self.send_command("\n".join(lines) + "\n", timeout=timeout)',
    'requires_file_prompt_suppression',
    'run_net_state_case'
)) {
    if (-not $acadVerify.Contains($requiredPattern)) {
        Write-Error "AutoCAD state-safety guard is missing required pattern: $requiredPattern"
        exit 1
    }
}

Write-Host "AutoCAD dialog and shutdown state guards verified."

$htc = Get-Content -LiteralPath (Join-Path $ProjectDirectory 'Commands\HtcCommand.cs') -Raw
$hbal = Get-Content -LiteralPath (Join-Path $ProjectDirectory 'Commands\HbalCommand.cs') -Raw
foreach ($requiredPattern in @('ValidateGridDimensions', 'ValidateCircleRecordsWritable',
    'MaxGridCandidatePoints', 'CheckGenerationCancelled', 'out value) && IsFinite(value)',
    'int processedRegions = 0;')) {
    if (!$hexFill.Contains($requiredPattern)) { throw "Missing generation safety guard: $requiredPattern" }
}
foreach ($requiredPattern in @('rowContainments', 'candidate.Clear()', 'foreach (ObjectId id in textIds)')) {
    if (!$htc.Contains($requiredPattern)) { throw "Missing text generation guard: $requiredPattern" }
}
foreach ($requiredPattern in @('QueueBalloonRenumber', 'OnBalloonLayerModified',
    'RenumberBalloons(spaceId)', 'db = previousDatabase;', 'request.Spaces.Remove(spaceId)')) {
    if (!$hbal.Contains($requiredPattern)) { throw "Missing balloon recovery guard: $requiredPattern" }
}
Write-Host 'HexFill generation and balloon recovery guards verified.'
