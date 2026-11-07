$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\ServerCharacterInspector.cs')
$needle = 'private const string QueryJs = """'
$index = $source.IndexOf($needle, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'Cannot find read-only character bridge' }
$start = $source.IndexOf([char]10, $index) + 1
$end = $source.IndexOf('""";', $start, [StringComparison]::Ordinal)
if ($start -lt 1 -or $end -lt 0) { throw 'Could not extract character bridge' }
$js = $source.Substring($start, $end - $start)
foreach ($bad in @('\?\.', '\?\?', '\.flatMap\s*\(', '\bcatch\s*\{')) {
    if ($js -match $bad) { throw "Unsupported Node 8 syntax: $bad" }
}
$path = Join-Path $env:RUNNER_TEMP 'character-bridge.js'
Set-Content -LiteralPath $path -Value $js -Encoding utf8
try {
    node --check $path
    if ($LASTEXITCODE -ne 0) { throw 'Character bridge does not parse' }
    Write-Host 'PASS: legacy Node bridge syntax'
} finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
