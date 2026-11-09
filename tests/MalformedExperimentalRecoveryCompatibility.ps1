$ErrorActionPreference='Stop'
$root=Join-Path $PSScriptRoot '..'
$repair=Get-Content -Raw -LiteralPath (Join-Path $root 'MalformedExperimentalItemRecovery.cs')
$writer=Get-Content -Raw -LiteralPath (Join-Path $root 'ServerEquipmentInstanceWriter.cs')
$viewer=Get-Content -Raw -LiteralPath (Join-Path $root 'PlayerInventoryModels.cs')
$ui=Get-Content -Raw -LiteralPath (Join-Path $root 'MainWindow.xaml')
if(!$writer.Contains('itemId + "@1@" + JSON.stringify(info)')) {
    throw 'Item writer must use the outer @ delimiter'
}
if(!$viewer.Contains("raw.Split('@', 3)") -or !$viewer.Contains('legacyMalformed')) {
    throw 'Viewer must decode @ items and flag legacy broken % entries'
}
if(!$ui.Contains('Click="RepairMalformedInventory_Click"')) {
    throw 'Targeted recovery button is not wired'
}
$needle='private const string RecoverJs = """'
$index=$repair.IndexOf($needle,[StringComparison]::Ordinal)
if($index -lt 0) { throw 'Missing targeted recovery bridge' }
$start=$repair.IndexOf([char]10,$index)+1
$end=$repair.IndexOf('""";',$start,[StringComparison]::Ordinal)
if($start -lt 1 -or $end -lt 0) { throw 'Malformed recovery bridge' }
$js=$repair.Substring($start,$end-$start)
foreach($bad in @('\?\.', '\?\?', '\.flatMap\s*\(', '\bcatch\s*\{')) {
    if($js -match $bad) { throw "Unsupported legacy Node syntax: $bad" }
}
foreach($guard in @('row("inventory").eq(inv)', 'actual!==hash',
    'x.slot>=inv.length || inv[x.slot]!==x.expected',
    'cleaned[x.slot]=x.original', 'verified.inventory.some((x,i)=>x!==cleaned[i])')) {
    if(!$js.Contains($guard)) { throw "Missing rollback protection: $guard" }
}
$p=Join-Path $env:RUNNER_TEMP 'malformed-item-recovery.js'
Set-Content -LiteralPath $p -Value $js -Encoding utf8
try {
    node --check $p
    if($LASTEXITCODE -ne 0) { throw 'Targeted recovery bridge does not parse' }
    Write-Host 'PASS: @ slot decoder, quarantine and targeted recovery guards'
} finally {
    Remove-Item $p -ErrorAction SilentlyContinue
}
