# One Archon benchmark run: prepare the repo, run stock archon-implement with the pinned model, log wall time, score.
param(
    [Parameter(Mandatory)][ValidateSet('T1', 'T2', 'T3')][string]$Task,
    [int]$Run = 1
)

$ErrorActionPreference = 'Stop'
$env:ARCHON_TELEMETRY_DISABLED = '1'
$env:Path = "$env:USERPROFILE\.bun\bin;$env:Path"
$archon = "$env:USERPROFILE\.archon\bin\archon.exe"
$runName = "archon-$Task-r$Run"
$runDir = "C:\bench-runs\$runName"
$log = "C:\bench-runs\$runName.log"

& (Join-Path $PSScriptRoot 'bench.ps1') -Action prepare -Tool archon -Task $Task -Run $Run | Out-Null
$taskFile = Get-ChildItem (Join-Path $PSScriptRoot "tasks\$Task-*.md") | Select-Object -First 1
$prompt = [string](Get-Content -Raw -Encoding UTF8 $taskFile.FullName)

$start = Get-Date
"START $($start.ToString('o'))" | Set-Content $log
# Archon writes progress to stderr; that is not a script failure.
$ErrorActionPreference = 'Continue'
& $archon workflow run archon-implement --cwd $runDir --no-worktree --config (Join-Path $PSScriptRoot 'archon-config.yaml') $prompt *>&1 |
    Out-File -Append -Encoding utf8 $log
$exit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
$end = Get-Date
"END $($end.ToString('o')) EXIT $exit WALL_SECONDS $([int]($end - $start).TotalSeconds)" | Add-Content $log

& (Join-Path $PSScriptRoot 'bench.ps1') -Action score -Tool archon -Task $Task -Run $Run *> $null
$r = Get-Content -Raw (Join-Path $PSScriptRoot "results\$runName.json") | ConvertFrom-Json
"$runName exit=$exit wall=$([int]($end - $start).TotalSeconds)s hidden=$($r.hiddenPassed)/$($r.hiddenTotal) own=$($r.otherPassed)p/$($r.otherFailed)f files=$($r.filesChanged) outOfScope=$($r.outOfScopeFiles -join ',')"
