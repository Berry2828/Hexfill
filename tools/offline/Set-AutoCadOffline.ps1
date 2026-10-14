param([ValidateSet('Install','Verify','Remove')][string]$Mode='Install')
$ErrorActionPreference='Stop'
$resultPath=Join-Path $PSScriptRoot 'result.json'
$manifestPath=Join-Path $PSScriptRoot 'programs.json'
try {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator permission is required.' }
    Add-Type -Path (Join-Path $PSScriptRoot 'AutoCadOffline.cs')
    if($Mode -eq 'Install') {
        $cadRoot=(Resolve-Path -LiteralPath 'D:\Program Files\Autodesk\AutoCAD 2018').Path
        [string[]]$programs=@(Get-ChildItem -LiteralPath $cadRoot -Recurse -Filter '*.exe' -File | Select-Object -ExpandProperty FullName)
        if($programs.Count -lt 2 -or !($programs -contains (Join-Path $cadRoot 'acad.exe'))) { throw 'CAD executable inventory is incomplete.' }
        $programs | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        $installed=[AutoCadOffline]::Install($programs)
        foreach($program in $programs) {
            $hash=[Security.Cryptography.SHA256]::Create()
            try { $tag=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::Unicode.GetBytes($program.ToLowerInvariant())))).Replace('-','').Substring(0,16) } finally { $hash.Dispose() }
            foreach($direction in 'Inbound','Outbound') {
                $name="AutoCAD-Offline-$tag-$direction"
                $existing=Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
                if($existing) { $existing | Set-NetFirewallRule -Enabled True -Action Block -Profile Any -Direction $direction }
                else { New-NetFirewallRule -Name $name -DisplayName "AutoCAD Offline: $([IO.Path]::GetFileName($program)) ($direction)" -Group 'AutoCAD Offline' -Program $program -Direction $direction -Action Block -Profile Any -Protocol Any -Enabled True | Out-Null }
            }
        }
    } else { [string[]]$programs=@(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json) }
    if($Mode -eq 'Remove') {
        [AutoCadOffline]::Remove($programs)
        Get-NetFirewallRule -Group 'AutoCAD Offline' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        $verified=0
    } else {
        $verified=[AutoCadOffline]::Verify($programs)
        $rules=@(Get-NetFirewallRule -Group 'AutoCAD Offline')
        if($rules.Count -ne $programs.Count*2 -or @($rules | Where-Object { $_.Enabled -ne 'True' -or $_.Action -ne 'Block' }).Count -ne 0) { throw 'Firewall rule verification failed.' }
    }
    [pscustomobject]@{status='success';mode=$Mode;programs=$programs.Count;wfpFilters=$verified;firewallRules=@(Get-NetFirewallRule -Group 'AutoCAD Offline' -ErrorAction SilentlyContinue).Count;timestamp=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
} catch {
    [pscustomobject]@{status='failed';mode=$Mode;error=$_.Exception.Message;timestamp=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
