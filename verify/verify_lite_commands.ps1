# ============================================================
# 【已废弃】自 2026-08-16 起，HexFill 只交付一份完整版 HexFill.dll，
# 不再构建 Lite 精简版，本白名单校验脚本不再参与任何构建门禁。
# 仅随 HexFill.Lite.csproj 保留作历史留存。
# ============================================================
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath
)

$ErrorActionPreference = "Stop"
$allowedCommands = @(
    "HDIA",
    "HNM",
    "HCLN",
    "HGR",
    "HRS",
    "HRD",
    "HEX",
    "HCN",
    "HIX",
    "HOF",
    "HBAL"
)

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$projectDirectory = Split-Path -Parent $resolvedProjectPath
[xml]$project = Get-Content -LiteralPath $resolvedProjectPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($project.NameTable)
$namespaceManager.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")
$compileNodes = $project.SelectNodes("//msb:Compile", $namespaceManager)

$commands = New-Object System.Collections.Generic.List[string]
$commandAttributeCount = 0
foreach ($compileNode in $compileNodes) {
    $sourcePattern = Join-Path $projectDirectory $compileNode.Include
    $sourceFiles = @(Get-ChildItem -Path $sourcePattern -File -ErrorAction Stop)
    foreach ($sourceFile in $sourceFiles) {
        $source = Get-Content -LiteralPath $sourceFile.FullName -Raw
        $attributePattern = '\[\s*(?:(?:global::)?Autodesk\.AutoCAD\.Runtime\.)?CommandMethod(?:Attribute)?\s*\('
        $literalPattern = $attributePattern + '\s*"([^"]+)"'
        $attributeMatches = [regex]::Matches($source, $attributePattern)
        $matches = [regex]::Matches($source, $literalPattern)
        $commandAttributeCount += $attributeMatches.Count
        foreach ($match in $matches) {
            $commands.Add($match.Groups[1].Value.ToUpperInvariant())
        }
    }
}

$actualCommands = @($commands | Sort-Object -Unique)
$unexpected = @($actualCommands | Where-Object { $_ -notin $allowedCommands })
$missing = @($allowedCommands | Where-Object { $_ -notin $actualCommands })
$duplicates = @($commands | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
$unsupportedRegistration = $commandAttributeCount -ne $commands.Count

if ($unexpected.Count -gt 0 -or $missing.Count -gt 0 -or $duplicates.Count -gt 0 -or $unsupportedRegistration) {
    if ($unexpected.Count -gt 0) {
        Write-Error ("Lite contains commands outside the whitelist: " + ($unexpected -join ", "))
    }
    if ($missing.Count -gt 0) {
        Write-Error ("Lite is missing required commands: " + ($missing -join ", "))
    }
    if ($duplicates.Count -gt 0) {
        Write-Error ("Lite contains duplicate command registrations: " + ($duplicates -join ", "))
    }
    if ($unsupportedRegistration) {
        Write-Error "Lite command registrations must use literal command names so the whitelist can verify them."
    }
    exit 1
}

Write-Host ("Lite command whitelist verified: " + ($actualCommands -join ", "))
