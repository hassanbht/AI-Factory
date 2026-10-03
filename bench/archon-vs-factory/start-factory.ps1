# Starts AI Factory Api + Worker for the benchmark with per-process overrides only:
# isolated SQLite DB and workspace, every model category pinned to the benchmark model.
# appsettings*.json and the dev database (data/factory.db) are not touched.
# Requires a prior build: dotnet build apps/AI.Factory.slnx
#
# Phase M — one command per execution runtime (the orchestrator stays identical; only the harness changes):
#   .\start-factory.ps1 -Runtime Hermes       # baseline (as benchmarked on 2026-10-02)
#   .\start-factory.ps1 -Runtime OpenHands
#   .\start-factory.ps1 -Runtime ClaudeCode   # needs ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN (claude setup-token)
#   .\start-factory.ps1 -Runtime Codex        # needs OPENAI_API_KEY; runs an OpenAI model (-CodexModel): a confounder
# Planner/Implementation/Test/Repair go to the chosen runtime; Review keeps its explicit Hermes route.
param(
    [string]$Model = 'anthropic/claude-haiku-4.5',
    [ValidateSet('Hermes', 'OpenHands', 'ClaudeCode', 'Codex')][string]$Runtime = 'Hermes',
    [string]$ClaudeModel = 'claude-haiku-4-5',
    [string]$CodexModel = 'gpt-5-codex',
    [string]$BenchRoot = 'C:\bench-runs',
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$logDir = Join-Path $BenchRoot 'factory-logs'
$pidFile = Join-Path $logDir 'pids.txt'

if ($Stop) {
    if (Test-Path $pidFile) {
        Get-Content $pidFile | ForEach-Object { Stop-Process -Id ([int]$_) -Force -ErrorAction SilentlyContinue }
        Remove-Item $pidFile
    }
    return
}

. (Join-Path $PSScriptRoot 'openrouter-key.ps1')

$workspace = Join-Path $BenchRoot 'factory-workspace'
New-Item -ItemType Directory -Force $logDir, $workspace | Out-Null
$db = "Data Source=$(Join-Path $BenchRoot 'factory-bench.db')"

$env:ConnectionStrings__FactoryDatabase = $db
$env:Database__ConnectionString = $db
$env:Database__AutoMigrate = 'true'
$env:Orchestration__WorkspaceRoot = $workspace
$env:Hermes__WorkspaceRoot = $workspace
$env:Hermes__DefaultModel = $Model
# Mirrors appsettings.Production.json: base appsettings.json leaves Hermes:Provider unset (the OpenRouter route is then
# rejected) and uses NetworkMode "none" (Hermes could not reach OpenRouter).
$env:Hermes__Provider = 'openrouter'
$env:Hermes__ProviderApiKeyEnvironmentVariable = 'OPENROUTER_API_KEY'
$env:Hermes__ProviderApiKeySecretName = 'OPENROUTER_API_KEY'
$env:Hermes__NetworkMode = 'bridge'
$env:Hermes__TimeoutSeconds = '900'
# Phase M: no Hermes data-volume workaround any more — every Hermes run gets its own empty, disposable home, so
# ~/.hermes/.env can no longer override the key the Factory injects (it did during the 2026-10-02 run).
$env:AgentWorkspace__ProjectsBaseDirectory = $workspace
$env:AgentWorkspace__AllowedRoot = $workspace
$env:OpenHands__DefaultModel = $Model
$env:ModelRouting__Models__0__ModelId = $Model
$categories = 'requirements-analysis', 'architecture', 'planning', 'coding', 'debugging', 'testing', 'code-review', 'documentation'
foreach ($c in $categories) { Set-Item "env:ModelRouting__CategoryProfiles__${c}__0" $Model }
$env:ASPNETCORE_URLS = 'http://localhost:5223'
# Custom environment name: loads only base appsettings.json (Production would add appsettings.Production.json models).
# (The 2026-10-02 run also used it to dodge the Development-only captive IAuditLogger crash, fixed in Phase M.)
$env:ASPNETCORE_ENVIRONMENT = 'Benchmark'
$env:DOTNET_ENVIRONMENT = 'Benchmark'
# Base appsettings.json has openrouter disabled and omniroute (not running locally) enabled.
$env:AiProviders__Providers__openrouter__Enabled = 'true'
$env:AiProviders__Providers__omniroute__Enabled = 'false'

# Phase M — route the coding AgentTypes to the chosen runtime; Review keeps its explicit route (Hermes).
foreach ($agentType in 'Planner', 'Implementation', 'Test', 'Repair') {
    Set-Item "env:Execution__AgentTypeProfiles__$agentType" $Runtime
}
switch ($Runtime) {
    'ClaudeCode' {
        # Explicit model mapping: the routed Factory model id -> the Claude Code model name. Never auto/default.
        Set-Item "env:ClaudeCode__Models__$Model" $ClaudeModel
        $variable = if ($env:ANTHROPIC_API_KEY) { 'ANTHROPIC_API_KEY' } elseif ($env:CLAUDE_CODE_OAUTH_TOKEN) { 'CLAUDE_CODE_OAUTH_TOKEN' } `
            else { throw 'ClaudeCode needs ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN (claude setup-token) in this shell.' }
        $env:ClaudeCode__CredentialSecretName = $variable
        $env:ClaudeCode__CredentialEnvironmentVariable = $variable
    }
    'Codex' {
        if (-not $env:OPENAI_API_KEY) { throw 'Codex needs OPENAI_API_KEY in this shell.' }
        Set-Item "env:Codex__Models__$Model" $CodexModel
        # npm installs only a codex.cmd shim on PATH; the adapter needs the native binary.
        $native = Get-ChildItem (Join-Path (npm root -g) '@openai\codex') -Recurse -Filter 'codex.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\bin\\codex\.exe$' } | Select-Object -First 1
        if ($native) { $env:Codex__ExecutablePath = $native.FullName }
    }
}
Write-Host "Execution runtime for Planner/Implementation/Test/Repair: $Runtime"

# Run the built dlls directly (not `dotnet run`) so the recorded PID is the app itself.
# Working directory = project dir, so relative paths in appsettings.json resolve as in development.
$apps = (Resolve-Path (Join-Path $PSScriptRoot '..\..\apps\src')).Path
function Start-App([string]$Name) {
    $dir = Join-Path $apps $Name
    Start-Process dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $dir `
        -ArgumentList (Join-Path $dir "bin\Debug\net10.0\$Name.dll") `
        -RedirectStandardOutput (Join-Path $logDir "$Name.out.log") `
        -RedirectStandardError (Join-Path $logDir "$Name.err.log")
}
$api = Start-App 'AI.Factory.Api'
$worker = Start-App 'AI.Factory.Worker'

Set-Content $pidFile @($api.Id, $worker.Id)
Write-Host "Api pid $($api.Id) on http://localhost:5223, Worker pid $($worker.Id). Logs: $logDir"
Write-Host "Resume test: Stop-Process -Id $($worker.Id), then rerun this script."
