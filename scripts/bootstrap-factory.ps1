# AI Factory — deterministic local bootstrap (Phase M). Idempotent: safe to run again at any time.
#
#   .\scripts\bootstrap-factory.ps1            # check prerequisites, build, register agent definitions (Api must be running)
#   .\scripts\bootstrap-factory.ps1 -Start     # ... and start Api + Worker (Development) in the background first
#
# Never prints a secret value — only whether each Factory secret NAME is set in this process environment.
param(
    [string]$Api = 'http://localhost:5223',
    [switch]$Start,
    [switch]$SkipBuild,
    [int]$StartTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$apps = Join-Path $root 'apps'

function Write-Check([string]$Name, [bool]$Ok, [string]$Detail) {
    $mark = if ($Ok) { 'OK  ' } else { 'MISS' }
    Write-Host ("  [{0}] {1,-34} {2}" -f $mark, $Name, $Detail)
}

function Get-ToolVersion([string]$Command, [string[]]$VersionArgs = @('--version')) {
    $cmd = Get-Command $Command -ErrorAction SilentlyContinue
    if (-not $cmd) { return $null }
    try { return ((& $cmd.Source @VersionArgs 2>$null) | Select-Object -First 1) } catch { return 'present (version unknown)' }
}

Write-Host "1. Tools"
$dotnet = Get-ToolVersion 'dotnet'
Write-Check 'dotnet (required)' ([bool]$dotnet) "$dotnet"
if (-not $dotnet) { throw 'The .NET SDK is required.' }
$docker = Get-ToolVersion 'docker' @('version', '--format', '{{.Server.Version}}')
Write-Check 'docker (Hermes, OpenHands)' ([bool]$docker) "$docker"
$claude = Get-ToolVersion 'claude'
Write-Check 'claude (ClaudeCode adapter)' ([bool]$claude) "$claude"
$codex = Get-ToolVersion 'codex'
Write-Check 'codex (Codex adapter)' ([bool]$codex) "$codex  (on Windows set Codex__ExecutablePath to the native codex.exe)"

Write-Host "2. Factory secrets (names only; values are never printed)"
foreach ($pair in @(
        @('OPENROUTER_API_KEY', 'Hermes / OpenHands via OpenRouter'),
        @('ANTHROPIC_API_KEY', 'ClaudeCode (API key)'),
        @('CLAUDE_CODE_OAUTH_TOKEN', 'ClaudeCode (subscription token, alternative)'),
        @('OPENAI_API_KEY', 'Codex'))) {
    $set = -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($pair[0]))
    Write-Check $pair[0] $set $pair[1]
}

if (-not $SkipBuild) {
    Write-Host "3. Build"
    & dotnet build (Join-Path $apps 'AI.Factory.slnx') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

if ($Start) {
    Write-Host "4. Start Api + Worker (Development)"
    $logs = Join-Path $root 'logs'
    New-Item -ItemType Directory -Force $logs | Out-Null
    foreach ($app in 'AI.Factory.Api', 'AI.Factory.Worker') {
        $dir = Join-Path $apps "src\$app"
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:DOTNET_ENVIRONMENT = 'Development'
        $env:ASPNETCORE_URLS = $Api
        # Only the Api migrates; a fresh local database is created on its first start.
        $env:Database__AutoMigrate = if ($app -eq 'AI.Factory.Api') { 'true' } else { 'false' }
        $process = Start-Process dotnet -PassThru -WindowStyle Hidden -WorkingDirectory $dir `
            -ArgumentList (Join-Path $dir "bin\Debug\net10.0\$app.dll") `
            -RedirectStandardOutput (Join-Path $logs "$app.out.log") -RedirectStandardError (Join-Path $logs "$app.err.log")
        Write-Host "  $app pid $($process.Id)"
    }

    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        try { $ready = (Invoke-WebRequest "$Api/health/ready" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $ready = $false }
    } until ($ready -or (Get-Date) -gt $deadline)
    if (-not $ready) { throw "Api did not become ready at $Api within $StartTimeoutSeconds s (see logs\AI.Factory.Api.*.log)." }
}

Write-Host "5. Default AgentDefinitions"
try {
    $existing = @(Invoke-RestMethod "$Api/api/agents/definitions" -TimeoutSec 10)
} catch {
    Write-Host "  Api not reachable at $Api - start it (or rerun with -Start), then rerun this script."
    exit 1
}

# One enabled definition per AgentType the lifecycle dispatches. The Factory's instruction builder supplies the
# task, constraints and structured-output contract; an empty system prompt is the benchmarked configuration.
foreach ($type in 'Planner', 'Implementation', 'Test', 'Repair', 'Review', 'Documentation') {
    if ($existing | Where-Object { $_.agentType -eq $type -and $_.enabled }) {
        Write-Check $type $true 'already registered'
        continue
    }

    $body = @{ name = "factory-$($type.ToLowerInvariant())"; agentType = $type; systemPrompt = ''; enabled = $true } | ConvertTo-Json
    Invoke-RestMethod -Method Post "$Api/api/agents/definitions" -ContentType 'application/json' -Body $body | Out-Null
    Write-Check $type $true 'registered'
}

Write-Host ""
Write-Host "Ready. Autonomous path: POST $Api/api/projects, POST /api/projects/{id}/source, POST /api/projects/{id}/start."
Write-Host "Runtime selection: Execution:AgentTypeProfiles (Hermes | OpenHands | ClaudeCode | Codex) - see docs/execution-runtimes.md."
