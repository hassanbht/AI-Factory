param(
    [Parameter(Mandatory)][ValidateSet('prepare', 'score')][string]$Action,
    [Parameter(Mandatory)][ValidateSet('archon', 'factory', 'reference')][string]$Tool,
    [Parameter(Mandatory)][ValidateSet('T1', 'T2', 'T3')][string]$Task,
    [int]$Run = 1,
    [string]$RunsRoot = 'C:\bench-runs',
    # score only: AI Factory clones the source into its own Project.RootPath; point at that clone.
    [string]$RunDir
)

$ErrorActionPreference = 'Stop'
$benchDir = $PSScriptRoot
$runName = "$Tool-$Task-r$Run"
$runDir = if ($RunDir) { $RunDir } else { Join-Path $RunsRoot $runName }

function Invoke-Git([string[]]$GitArgs) {
    & git -C $runDir @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $GitArgs failed in $runDir" }
}

if ($Action -eq 'prepare') {
    if (Test-Path $runDir) { throw "$runDir already exists; pick another -Run number." }
    New-Item -ItemType Directory -Force $runDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $benchDir 'fixture\*') $runDir
    Copy-Item -Force (Join-Path $benchDir 'fixture\.gitignore') $runDir
    Invoke-Git @('init', '-q', '-b', 'main')
    Invoke-Git @('config', 'core.autocrlf', 'false')
    Invoke-Git @('add', '-A')
    Invoke-Git @('-c', 'user.name=bench', '-c', 'user.email=bench@localhost', 'commit', '-q', '-m', 'baseline')
    Invoke-Git @('tag', 'baseline')
    Write-Host "Prepared $runDir"
    Write-Host "Prompt: $(Join-Path $benchDir "tasks\$Task-*.md")"
    return
}

# score: measure the agent's final working tree (committed or not) against the baseline tag.
# Tool-made clones may emit CRLF warnings on stderr; those are not failures.
$ErrorActionPreference = 'Continue'
$changed = @(& git -C $runDir diff --name-only baseline -- . ':!*/bin/*' ':!*/obj/*') +
           @(& git -C $runDir ls-files --others --exclude-standard)
$changed = @($changed | Where-Object { $_ } | Sort-Object -Unique)
$numstat = & git -C $runDir diff --numstat baseline
$added = 0; $deleted = 0
foreach ($line in $numstat) {
    $parts = $line -split "`t"
    if ($parts[0] -match '^\d+$') { $added += [int]$parts[0]; $deleted += [int]$parts[1] }
}
$outOfScope = @($changed | Where-Object { $_ -notmatch '^(src|tests)/' })

$hiddenTarget = Join-Path $runDir 'tests\Inventory.Tests\Hidden'
New-Item -ItemType Directory -Force $hiddenTarget | Out-Null
Copy-Item -Force (Join-Path $benchDir "hidden\$Task.cs") $hiddenTarget

$trxDir = Join-Path $runDir 'TestResults'
Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
# Failing tests and compile errors are expected outcomes here, not script errors.
& dotnet test (Join-Path $runDir 'Inventory.slnx') --logger 'trx;LogFileName=score.trx' --results-directory $trxDir | Out-Host
$trx = Get-ChildItem -Recurse -Filter 'score.trx' $trxDir -ErrorAction SilentlyContinue | Select-Object -First 1

$result = [ordered]@{
    run = $runName; tool = $Tool; task = $Task
    build = [bool]$trx
    hiddenPassed = 0; hiddenTotal = 0; otherPassed = 0; otherFailed = 0
    filesChanged = $changed.Count; linesAdded = $added; linesDeleted = $deleted
    outOfScopeFiles = $outOfScope
    scoredAt = (Get-Date).ToString('o')
}
if ($trx) {
    foreach ($r in ([xml](Get-Content -Raw $trx.FullName)).TestRun.Results.UnitTestResult) {
        $passed = $r.outcome -eq 'Passed'
        if ($r.testName -like 'Inventory.HiddenTests.*') {
            $result.hiddenTotal++; if ($passed) { $result.hiddenPassed++ }
        } elseif ($passed) { $result.otherPassed++ } else { $result.otherFailed++ }
    }
}

# Leave the run directory exactly as the agent left it.
Remove-Item -Recurse -Force $hiddenTarget, $trxDir -ErrorAction SilentlyContinue

$resultsDir = Join-Path $benchDir 'results'
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$json = $result | ConvertTo-Json
Set-Content -Encoding utf8 -Path (Join-Path $resultsDir "$runName.json") -Value $json
$json
