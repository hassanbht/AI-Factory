param([string]$ResultsDir = "TestResults/P2")

# Default-safety: this process and its children run with no live opt-in and no provider credential.
$optIns = @('AI_FACTORY_HERMES_SMOKE_TEST','AI_FACTORY_OPENHANDS_SMOKE_TEST','AI_FACTORY_LIVE_HERMES_CREDENTIALS',
  'AI_FACTORY_LIVE_GATEWAY_AGENTRUN','AI_FACTORY_LIVE_SMOKE','AI_FACTORY_LIVE_CLAUDE_CONTRACT','AI_FACTORY_LIVE_BENCHMARK',
  'AI_FACTORY_RUN_BENCHMARK','AI_FACTORY_LIVE_CERT_HERMES','AI_FACTORY_LIVE_CERT_OPENHANDS','AI_FACTORY_CERT_MODEL',
  'OPENROUTER_API_KEY','ANTHROPIC_API_KEY','CLAUDE_CODE_OAUTH_TOKEN','OPENAI_API_KEY')
foreach ($n in $optIns) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
Write-Host ("env check: " + (($optIns | Where-Object { Test-Path "Env:$_" }) -join ',') + " (empty = none set)")

if (Test-Path $ResultsDir) { Remove-Item -Recurse -Force $ResultsDir }

Write-Host "=== Clean build (--no-incremental) ==="
dotnet build apps/AI.Factory.slnx --no-incremental
if ($LASTEXITCODE -ne 0) { Write-Error "build failed"; exit 1 }

foreach ($i in 1..3) {
  Write-Host "=== Regression run $i ==="
  $start = Get-Date
  dotnet test apps/AI.Factory.slnx --no-build --results-directory "$ResultsDir/Run$i" --logger trx | Out-Null
  $code = $LASTEXITCODE
  $wall = [int]((Get-Date) - $start).TotalSeconds
  $factory = @(docker ps -a --format "{{.Names}}" | Where-Object { $_ -like 'ai-factory-agent-*' }).Count
  Write-Host "run $i exit=$code wallS=$wall factoryContainersAfter=$factory"
}
