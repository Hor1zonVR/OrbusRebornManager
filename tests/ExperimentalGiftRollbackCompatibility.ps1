$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\ExperimentalGiftRollback.cs')
$needle = 'private const string RollbackJs = """'
$idx = $source.IndexOf($needle,[StringComparison]::Ordinal)
if ($idx -lt 0) { throw 'Missing recovery bridge' }
$start=$source.IndexOf([char]10,$idx)+1
$end=$source.IndexOf('""";',$start,[StringComparison]::Ordinal)
if ($start -lt 1 -or $end -lt 0) { throw 'Malformed recovery bridge source' }
$js=$source.Substring($start,$end-$start)
foreach ($bad in @('\?\.', '\?\?', '\.flatMap\s*\(', '\bcatch\s*\{')) {
  if ($js -match $bad) { throw "Unsupported Node 8 syntax: $bad" }
}
foreach ($guard in @(
  'changeAt(slot,original)', 'row("inventory").eq(inv)',
  'inv[slot]!==expected', 'digest!==hash',
  'expected.startsWith(String(grantedItem) + "@1@")',
  'verified.inventory[slot]!==original'
)) {
  if (!$js.Contains($guard)) { throw "Missing rollback guard: $guard" }
}
$p=Join-Path $env:RUNNER_TEMP 'gift-rollback-node.js'
Set-Content -LiteralPath $p -Value $js -Encoding utf8
try {
  node --check $p
  if ($LASTEXITCODE -ne 0) { throw 'Rollback bridge JavaScript parse failure' }
  Write-Host 'PASS: rollback script only conditionally reverts one grant'
} finally { Remove-Item $p -ErrorAction SilentlyContinue }
