# One AI Factory benchmark run: prepare the repo, create the project, attach source, start the autonomous lifecycle,
# then monitor until a human gate or a terminal state. Requires start-factory.ps1 to be running.
param(
    [Parameter(Mandatory)][ValidateSet('T1', 'T2', 'T3')][string]$Task,
    [int]$Run = 1,
    [string]$Api = 'http://localhost:5223',
    # Hard stop for the whole benchmark's OpenRouter spend (key usage is cumulative, in USD).
    [double]$BudgetUsd = 5.5
)

$ErrorActionPreference = 'Stop'
$runName = "factory-$Task-r$Run"
$log = "C:\bench-runs\$runName.log"

. (Join-Path $PSScriptRoot 'openrouter-key.ps1')
function Get-OpenRouterUsage {
    (Invoke-RestMethod -Uri 'https://openrouter.ai/api/v1/key' -Headers @{ Authorization = "Bearer $env:OPENROUTER_API_KEY" }).data.usage
}
$usageBefore = Get-OpenRouterUsage
if ($usageBefore -ge $BudgetUsd) { throw "OpenRouter usage $usageBefore USD already at budget $BudgetUsd USD; not starting $runName." }
& (Join-Path $PSScriptRoot 'bench.ps1') -Action prepare -Tool factory -Task $Task -Run $Run | Out-Null

function Post([string]$Path, $Body) {
    $json = $Body | ConvertTo-Json -Depth 6
    Invoke-RestMethod -Method Post -Uri "$Api$Path" -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($json))
}

$taskFile = Get-ChildItem (Join-Path $PSScriptRoot "tasks\$Task-*.md") | Select-Object -First 1
$description = [string](Get-Content -Raw -Encoding UTF8 $taskFile.FullName)

"START $((Get-Date).ToString('o'))" | Set-Content $log
# Project slugs are unique; the suffix keeps a re-run from colliding with an earlier (failed) attempt.
$project = Post '/api/projects' @{ name = "$runName-$(Get-Date -Format 'HHmmss')"; description = $description }
"PROJECT $($project.id)" | Add-Content $log
Post "/api/projects/$($project.id)/source" @{ sourceType = 'GitRepository'; location = "C:\bench-runs\$runName"; branch = 'main' } | Out-Null
Post "/api/projects/$($project.id)/start" @{ actor = 'bench' } | Out-Null
"LIFECYCLE-STARTED $((Get-Date).ToString('o'))" | Add-Content $log

& (Join-Path $PSScriptRoot 'monitor-factory.ps1') -ProjectId $project.id -RunName $runName -Api $Api
$usageAfter = Get-OpenRouterUsage
"COST_USD $([math]::Round($usageAfter - $usageBefore, 4)) (total $usageAfter)" | Add-Content $log
