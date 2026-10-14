param([switch]$NoPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $projectRoot 'packages\Microsoft.Net.Compilers\tools\csc.exe'
$outputDirectory = Join-Path $projectRoot 'bin\Release'
$outputDll = Join-Path $outputDirectory 'HexFill.dll'
$stageDirectory = Join-Path $projectRoot 'obj\release-stage'
New-Item -ItemType Directory -Force -Path $stageDirectory,$outputDirectory | Out-Null
$stageDll = Join-Path $stageDirectory 'HexFill.dll'
$responseFile = Join-Path $stageDirectory 'release.rsp'
$manualBuilder = Join-Path $projectRoot 'tools\manual\build_manual.mjs'
$manualNotesPath = Join-Path $projectRoot 'tools\manual\release-notes.json'
$taskRuntimeRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.cache\codex-runtimes\codex-primary-runtime\dependencies'
$manualNode = Join-Path $taskRuntimeRoot 'node\bin\node.exe'

Push-Location -LiteralPath $projectRoot
try {
    & (Join-Path $projectRoot 'verify\verify_performance_guards.ps1') -ProjectDirectory $projectRoot
    $response = Get-Content -LiteralPath (Join-Path $projectRoot 'build_hexfill.rsp') -Raw
    $response = [regex]::Replace($response, '(?m)^/out:.*$', '/out:"' + $stageDll + '"')
    [IO.File]::WriteAllText($responseFile, $response, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $stageDll) { Remove-Item -LiteralPath $stageDll }
    & $compiler "@$responseFile"
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $stageDll)) { throw 'Compilation failed.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($stageDll).FileVersion
    if (!$version) { throw 'Missing DLL version.' }
    $bytes = [IO.File]::ReadAllBytes($stageDll)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) { throw 'DLL is not AMD64.' }
    $chinaDate = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::Now, 'China Standard Time').ToString('yyyyMMdd')
    $destination = if ($NoPublish) { $outputDll } else { Join-Path ([Environment]::GetFolderPath('Desktop')) "HexFill_${chinaDate}_Full.dll" }
    if (!(Test-Path -LiteralPath $manualBuilder) -or !(Test-Path -LiteralPath $manualNotesPath) -or !(Test-Path -LiteralPath $manualNode)) { throw 'DLL release requires the bundled Node runtime and manual sources.' }
    $manualNotes = Get-Content -LiteralPath $manualNotesPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manualNotes.version -ne $version -or !$manualNotes.changedCommands -or !$manualNotes.changes) { throw 'Manual release notes must match the DLL version and describe changed commands.' }
    $manualModules = Join-Path $projectRoot 'tools\manual\node_modules'
    if (!(Test-Path -LiteralPath $manualModules)) { New-Item -ItemType Junction -Path $manualModules -Target (Join-Path $taskRuntimeRoot 'node\node_modules') | Out-Null }
    & $manualNode $manualBuilder --version $version --date $chinaDate --out-dir $stageDirectory --dll-name ([IO.Path]::GetFileName($destination))
    if ($LASTEXITCODE -ne 0) { throw 'Manual generation or verification failed; DLL was not published.' }
    $manualName = "HexFill_${version}_命令使用说明书_${chinaDate}.xlsx"
    $stageManual = Join-Path $stageDirectory $manualName
    $manualManifest = Get-Content -LiteralPath (Join-Path $stageDirectory 'manual-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!(Test-Path -LiteralPath $stageManual) -or $manualManifest.version -ne $version -or $manualManifest.commands.Count -lt 1 -or $manualManifest.sheets[0] -ne '本版改动') { throw 'Missing or mismatched companion manual; DLL was not published.' }
    # Publish the manual before its matching DLL. A failed manual never leaves a new DLL alone.
    Copy-Item -LiteralPath $stageManual -Destination (Join-Path $outputDirectory $manualName) -Force
    Copy-Item -LiteralPath $stageDll -Destination $outputDll -Force
    $manualDestination = Join-Path ([IO.Path]::GetDirectoryName($destination)) $manualName
    if (!$NoPublish) {
        Copy-Item -LiteralPath $stageManual -Destination $manualDestination -Force
        Copy-Item -LiteralPath $outputDll -Destination $destination -Force
    }
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($hash -ne (Get-FileHash -LiteralPath $stageDll -Algorithm SHA256).Hash) { throw 'Output hash mismatch.' }
    if ((Get-FileHash -LiteralPath $manualDestination -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $stageManual -Algorithm SHA256).Hash) { throw 'Manual output hash mismatch.' }
    Write-Output "Built HexFill $version (AMD64): $destination"
    Write-Output "SHA256: $hash"
    Write-Output "Companion manual: $manualDestination"
} finally { Pop-Location }
