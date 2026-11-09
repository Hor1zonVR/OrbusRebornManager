$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\ServerEquipmentInstanceWriter.cs')
$needle = 'private const string PatchJs = """'
$index = $source.IndexOf($needle, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'Cannot find equipment bridge' }
$start = $source.IndexOf([char]10, $index) + 1
$end = $source.IndexOf('""";', $start, [StringComparison]::Ordinal)
if ($start -lt 1 -or $end -lt 0) { throw 'Could not extract equipment bridge' }
$js = $source.Substring($start, $end - $start)
foreach ($bad in @('\?\.', '\?\?', '\.flatMap\s*\(', '\bcatch\s*\{')) {
    if ($js -match $bad) { throw "Unsupported legacy Node syntax: $bad" }
}
$path = Join-Path $env:RUNNER_TEMP 'equipment-bridge.js'
Set-Content -LiteralPath $path -Value $js -Encoding utf8
try {
    node --check $path
    if ($LASTEXITCODE -ne 0) { throw 'Equipment bridge does not parse' }
    foreach ($expected in @('changeAt(slot,nextRaw)', 'row("inventory").eq(inv)',
        '"itemLevel"', '"itemPlusLevel"', 'createHash("sha256")',
        'verify.inventory[slot] !== nextRaw')) {
        if (!$js.Contains($expected)) {
            throw "Equipment bridge missing required guard: $expected"
        }
    }
    Write-Host 'PASS: equipment bridge syntax and atomic-slot guards'
} finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
