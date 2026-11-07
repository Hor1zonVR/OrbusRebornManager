# The bridge runs inside the game's Docker image which uses Node 8 syntax.
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot '..\ServerPlayerService.cs'
$source = Get-Content -Raw -LiteralPath $sourcePath
$needle = 'private const string CharacterQueryJs = """'
$index = $source.IndexOf($needle, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'Cannot find embedded player bridge script' }
$start = $source.IndexOf([char]10, $index)
if ($start -lt 0) { throw 'Missing player bridge script newline' }
$start++
$end = $source.IndexOf('""";', $start, [StringComparison]::Ordinal)
if ($end -lt 0) { throw 'Cannot find end of embedded player bridge script' }
$script = $source.Substring($start, $end - $start)
$unsupported = @(
  @{ expression = '\?\.'; name = 'optional chaining' },
  @{ expression = '\?\?'; name = 'nullish coalescing' },
  @{ expression = '\bcatch\s*\{'; name = 'optional catch binding' },
  @{ expression = '\.flatMap\s*\('; name = 'Array.flatMap' },
  @{ expression = '\.fromEntries\s*\('; name = 'Object.fromEntries' }
)
foreach ($rule in $unsupported) {
  if ($script -match $rule.expression) {
    throw "Player bridge uses unsupported Node 8 feature: $($rule.name)"
  }
}
if (-not $script.Contains('ORBUS_MANAGER_PLAYERS:')) {
  throw 'Missing expected JSON result marker'
}
$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$testFile = Join-Path $tempRoot 'orbus-player-bridge-compat.js'
try {
  Set-Content -LiteralPath $testFile -Value $script -Encoding utf8
  node --check $testFile
  if ($LASTEXITCODE -ne 0) { throw 'Player bridge script failed Node syntax check' }
  Write-Host 'Player bridge passed compatibility checks.'
} finally {
  Remove-Item -LiteralPath $testFile -ErrorAction SilentlyContinue
}
