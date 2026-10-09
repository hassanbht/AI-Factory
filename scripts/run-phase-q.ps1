param(
    [string]$Model = "deepseek/deepseek-v4-flash-0731",
    [string[]]$Tasks = @("Q-A", "Q-B", "Q-C", "Q-D"),
    [int]$Reps = 3,
    [double]$MaxSpendUsd = 2.00,
    [string]$ResultsDir = "TestResults/PhaseQ/certification-epoch-2"
)

# Phase Q runner. Each state transition is persisted before/after billed work so
# interruption is visible and a resumed run never blindly duplicates an identity.
$ErrorActionPreference = "Stop"
# Phase Q.3 safety interlock: the runner is intentionally non-executable until
# an explicit, separately approved Q.4 canary process removes this guard.
throw "Phase Q live execution is disabled in Q.3. Do not run a matrix sample; use a separately authorized Q.4 canary after security and accounting gates clear."
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot
if ($Model -ne "deepseek/deepseek-v4-flash-0731") { throw "Phase Q requires its certified explicit model; refusing model '$Model'." }
if ($MaxSpendUsd -ne 2.00) { throw "The Phase Q spend ceiling is fixed at USD 2.00." }
$resultsPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ResultsDir))
$manifestPath = Join-Path $resultsPath "manifest.json"
$summaryPath = Join-Path $resultsPath "summary.json"
$CertificationEpoch = 'PhaseQ-Certification-Epoch-2'
$rebaselineDecisionPath = Join-Path $repoRoot 'docs/phase-q-rebaseline-decision.md'
if (-not (Test-Path -LiteralPath $rebaselineDecisionPath) -or (Get-Content -LiteralPath $rebaselineDecisionPath -Raw) -notmatch 'PHASE_Q_REBASELINE_APPROVED=true') {
    throw "Certification epoch is not governed: explicit PHASE_Q_REBASELINE_APPROVED=true decision is required. No execution started."
}
$legacyResultsPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'TestResults/PhaseQ'))
$legacyPrefix = $legacyResultsPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($resultsPath.Equals($legacyResultsPath, [StringComparison]::OrdinalIgnoreCase) -or $resultsPath.StartsWith($legacyPrefix, [StringComparison]::OrdinalIgnoreCase) -and -not $resultsPath.Equals([IO.Path]::GetFullPath((Join-Path $repoRoot 'TestResults/PhaseQ/certification-epoch-2')), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Historical Phase-Q artifacts are immutable. Use only TestResults/PhaseQ/certification-epoch-2."
}
$epochMarker = Join-Path $resultsPath 'epoch.json'
$epochRecordPath = Join-Path $resultsPath 'epoch-record.json'

function Resolve-ManifestPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if (-not [IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path)) }
    if (Test-Path -LiteralPath $Path) { return [IO.Path]::GetFullPath($Path) }
    if ($Path -match '(?i)(?:^|[\\/])TestResults[\\/](.+)$') {
        $relative = ('TestResults/' + $Matches[1]).Replace('/', [IO.Path]::DirectorySeparatorChar)
        return [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
    }
    return $Path
}

function Get-ManifestRelativePath([string]$Path) {
    $fullPath = Resolve-ManifestPath $Path
    if ([string]::IsNullOrWhiteSpace($fullPath)) { return $null }
    $prefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($prefix.Length).Replace('\', '/')
    }
    return $fullPath
} 

function Write-AtomicJson([string]$Path, $Value) {
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $json = ConvertTo-Json -InputObject $Value -Depth 12
    [IO.File]::WriteAllText($temp, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    if ([IO.File]::Exists($Path)) {
        $backup = "$Path.$([guid]::NewGuid().ToString('N')).bak"
        [IO.File]::Replace($temp, $Path, $backup)
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    } else { [IO.File]::Move($temp, $Path) }
}

function Save-PartialState {
    $script:manifest.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-AtomicJson $script:manifestPath $script:manifest
    $summary = [pscustomobject]@{
        phase = $script:manifest.phase; historicalEpoch = $script:manifest.historicalEpoch
        certificationEpoch = $script:manifest.certificationEpoch; rebaselineDecision = $script:manifest.rebaselineDecision
        approvalMarker = $script:manifest.approvalMarker; phaseStatus = $script:manifest.phaseStatus
        runtime = $script:manifest.runtime; provider = $script:manifest.provider; model = $script:manifest.model
        maxSpendUsd = $script:manifest.maxSpendUsd; providerReadiness = $script:manifest.providerReadiness
        providerCostCaptureVerified = $script:manifest.providerCostCaptureVerified
        totals = [pscustomobject]@{ required = @($script:manifest.runs).Count; pass = 0; fail = 0; blocked = 0; timeout = 0; cancelled = 0; interrupted = 0; notRun = @($script:manifest.runs).Count; certified = 0; historicalCarriedForward = 0 }
        runs = @($script:manifest.runs)
    }
    Write-AtomicJson $script:summaryPath $summary
}

if (Test-Path -LiteralPath $epochMarker) {
    $configuredEpoch = Get-Content -LiteralPath $epochMarker -Raw | ConvertFrom-Json
    if ($configuredEpoch.certificationEpoch -ne $CertificationEpoch -or $configuredEpoch.approved -ne $true) {
        throw "Epoch metadata conflict or approval missing; refusing live execution."
    }
    if (-not (Test-Path -LiteralPath $epochRecordPath)) { throw 'Existing epoch marker lacks its separate audit record; refusing to reconstruct governance state.' }
} elseif (Test-Path -LiteralPath $manifestPath) {
    throw "Manifest already exists without its required epoch marker; refusing to overwrite or reconstruct state."
} elseif (Test-Path -LiteralPath (Join-Path $resultsPath 'summary.json')) {
    throw "Summary already exists without its source manifest; refusing to invent or overwrite epoch state."
}

function Read-EvidenceCost($Run) {
    $paths = @()
    if ($Run.evidencePath -and (Test-Path $Run.evidencePath)) { $paths += $Run.evidencePath }
    if ($Run.consolePath -and (Test-Path $Run.consolePath)) { $paths += $Run.consolePath }
    $knownCost = 0.0
    $estimateCost = 0.0
    $unknownAttempts = 0
    $attemptCosts = @{}
    foreach ($candidatePath in $paths) {
        $path = Resolve-ManifestPath $candidatePath
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        $text = Get-Content -LiteralPath $path -Raw
        $currentAttempt = $null
        foreach ($line in ($text -split "`r?`n")) {
            if ($line -match '^\s*PHASEQ agentRun id=([^ ]+)') {
                $currentAttempt = $Matches[1]
                if (-not $attemptCosts.ContainsKey($currentAttempt)) {
                    $attemptCosts[$currentAttempt] = [pscustomobject]@{ HasCost = $false; Amount = 0.0; IsEstimate = $false }
                }
            }
            if ($line -match '^\s*PHASEQ\s+metadata=(\{.*\})' -and $currentAttempt) {
                try { $meta = $Matches[1] | ConvertFrom-Json -ErrorAction Stop }
                catch { continue }
                $parsedAmount = 0.0
                $amountText = if ($null -ne $meta.CostUsd) { [string]$meta.CostUsd } else { '' }
                $hasParsedAmount = [double]::TryParse($amountText, [Globalization.NumberStyles]::Any, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedAmount)
                if ($null -ne $meta.CostUsd -and $hasParsedAmount) {
                    $costSource = [string]$meta.CostSource
                    $isEstimate = $costSource -match '(?i)estimated'
                    $isReported = $costSource -match '^openrouter:generation$'
                    $attemptCosts[$currentAttempt] = [pscustomobject]@{ HasCost = $true; Amount = $parsedAmount; IsEstimate = $isEstimate; IsReported = $isReported }
                }
            }
        }
    }
    foreach ($attempt in $attemptCosts.Values) {
        if (-not $attempt.HasCost) { $unknownAttempts++ }
        elseif ($attempt.IsEstimate) { $estimateCost += $attempt.Amount }
        elseif ($attempt.IsReported) { $knownCost += $attempt.Amount }
        else { $unknownAttempts++ }
    }
    return [pscustomobject]@{ ReportedCostUsd = [Math]::Round($knownCost, 8); EstimatedCostUsd = [Math]::Round($estimateCost, 8); UnknownCostAttempts = $unknownAttempts; AttemptCount = $attemptCosts.Count }
}

function Save-State {
    $script:manifest.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-AtomicJson $script:manifestPath $script:manifest
    $summary = [pscustomobject]@{
        phase = $script:manifest.phase
        historicalEpoch = $script:manifest.historicalEpoch
        certificationEpoch = $script:manifest.certificationEpoch
        rebaselineDecision = $script:manifest.rebaselineDecision
        approvalMarker = $script:manifest.approvalMarker
        phaseStatus = $script:manifest.phaseStatus
        runtime = $script:manifest.runtime; provider = $script:manifest.provider; model = $script:manifest.model
        maxSpendUsd = $script:manifest.maxSpendUsd; providerReadiness = $script:manifest.providerReadiness
        providerCostCaptureVerified = $script:manifest.providerCostCaptureVerified
        runs = @($script:manifest.runs)
        totals = [pscustomobject]@{
            required = @($script:manifest.runs).Count
            pass = @($script:manifest.runs | Where-Object { $_.status -eq 'PASS' }).Count
            fail = @($script:manifest.runs | Where-Object { $_.status -eq 'FAIL' }).Count
            blocked = @($script:manifest.runs | Where-Object { $_.status -eq 'BLOCKED' }).Count
            timeout = @($script:manifest.runs | Where-Object { $_.status -eq 'TIMEOUT' }).Count
            cancelled = @($script:manifest.runs | Where-Object { $_.status -eq 'CANCELLED' }).Count
            interrupted = @($script:manifest.runs | Where-Object { $_.status -eq 'INTERRUPTED' }).Count
            notRun = @($script:manifest.runs | Where-Object { $_.status -eq 'NOT_RUN' }).Count
            certified = @($script:manifest.runs | Where-Object { $_.certificationStatus -eq 'PASS' }).Count
        }
    }
    Write-AtomicJson $script:summaryPath $summary
}

function New-RunRecord([string]$RunId, [string]$Task, [int]$Rep) {
    $ev = Join-Path $script:resultsPath "$RunId.txt"
    $console = Join-Path $script:resultsPath "$RunId.console.log"
    $existing = @($ev, $console) | Where-Object { Test-Path $_ }
    $status = 'NOT_RUN'
    $detail = 'No certification-epoch artifact; no historical result is carried forward.'
    if ($existing.Count -gt 0) {
        throw "Certification output path contains historical evidence for $RunId; use a clean epoch directory. Existing files were not modified."
    }
    $record = [pscustomobject]@{
        runId = $RunId; task = $Task; repetition = $Rep; status = $status
        attemptNumber = 0; runIdentity = $null; projectedRemainingBudgetUsd = $null
        certificationStatus = 'NOT_CERTIFIED'; detail = $detail
        evidencePath = Get-ManifestRelativePath $ev
        consolePath = Get-ManifestRelativePath $console
        startedUtc = $null; completedUtc = $null; exitCode = $null; durationSeconds = $null
        reportedCostUsd = $null; estimatedCostUsd = $null; unknownCostAttempts = 0; costStatus = 'NOT_APPLICABLE'
        historicalEpoch = 'HistoricalEpoch'; certificationEpoch = $CertificationEpoch
        runtime = 'Hermes'; hermesImage = 'nousresearch/hermes-agent:0.20.6'; provider = 'OpenRouter'; model = $Model
        workItemId = $null; agentRunId = $null; generationId = $null; authoritativeCostLimitUsd = $null
        generation = 'NOT_RUN'; verification = 'NOT_RUN'; review = 'NOT_RUN'; acceptance = 'NOT_RUN'
        workspaceIntegrity = 'NOT_RUN'; credentialAudit = 'NOT_RUN'; cleanup = 'NOT_RUN'; providerUsageEvidence = $null
        failureClass = $null
    }
    return $record
}

function Get-EvidenceReportedProviderCost($Run, [string]$AgentRunId, [string]$Epoch) {
    $path = Resolve-ManifestPath $Run.evidencePath
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    $matches = @()
    foreach ($line in (Get-Content -LiteralPath $path)) {
        if ($line -match '^\s*PHASEQ\s+providerUsage=(\{.*\})') {
            try { $usage = $Matches[1] | ConvertFrom-Json -ErrorAction Stop }
            catch { throw "Malformed provider-usage evidence for $($Run.runId); refuse certification." }
            if ($usage.Source -ne 'openrouter:generation' -or $usage.GenerationId -notmatch '^gen-[0-9A-Za-z-]+$' -or $usage.CostStatus -ne 'REPORTED' -or $null -eq $usage.CostUsd -or [decimal]$usage.CostUsd -lt 0 -or $usage.AgentRunId -ne $AgentRunId -or $usage.CertificationEpoch -ne $Epoch -or [string]::IsNullOrWhiteSpace([string]$usage.EvidenceId)) {
                throw "Provider usage record is incomplete or correlated to a different run/epoch for $($Run.runId)."
            }
            $matches += $usage
        }
    }
    if ($matches.Count -eq 0) { return $null }
    if (@($matches | Group-Object GenerationId).Count -ne $matches.Count) { throw "Duplicate generation record for $($Run.runId)." }
    $cost = [decimal](($matches | Measure-Object -Property CostUsd -Sum).Sum)
    $limit = [decimal]$Run.authoritativeCostLimitUsd
    if ($Run.authoritativeCostLimitUsd -and $limit -gt 0 -and $cost -gt $limit) { throw "Provider final cost exceeds the safely reserved budget for $($Run.runId)." }
    return [pscustomobject]@{
        CostUsd = $cost
        GenerationIds = @($matches | ForEach-Object { [string]$_.GenerationId })
        EvidenceIds = @($matches | ForEach-Object { [string]$_.EvidenceId })
        InputTokens = [int](($matches | Measure-Object -Property InputTokens -Sum).Sum)
        OutputTokens = [int](($matches | Measure-Object -Property OutputTokens -Sum).Sum)
        TotalTokens = [int](($matches | Measure-Object -Property TotalTokens -Sum).Sum)
    }
}

function Persist-RunEvidence($Run) {
    $evidencePath = Resolve-ManifestPath $Run.evidencePath
    if (-not $evidencePath -or -not (Test-Path -LiteralPath $evidencePath)) { return }
    $text = Get-Content -LiteralPath $evidencePath -Raw
    $hasRequiredEvidence = $text -match 'PHASEQ workItem\.final=Completed' -and
        $text -match 'PHASEQ validationRun success=True' -and
        $text -match 'PHASEQ reviewRun blocksCompletion=False' -and
        $text -match 'PHASEQ containers\.after15s factoryOwned=0'
    if (-not $hasRequiredEvidence -and $Run.status -eq 'PASS') {
        $Run.status = 'FAIL'; $Run.certificationStatus = 'NOT_CERTIFIED'; $Run.failureClass = 'EVIDENCE'
    }
    $cost = Read-EvidenceCost $Run
    $Run.reportedCostUsd = $cost.ReportedCostUsd
    $Run.estimatedCostUsd = $cost.EstimatedCostUsd
    $Run.unknownCostAttempts = $cost.UnknownCostAttempts
    if ($cost.AttemptCount -eq 0) { $Run.costStatus = 'UNKNOWN'; $Run.unknownCostAttempts = 1 }
    elseif ($cost.UnknownCostAttempts -gt 0) { $Run.costStatus = 'UNKNOWN' }
    elseif ($cost.ReportedCostUsd -gt 0) { $Run.costStatus = 'REPORTED' }
    elseif ($cost.EstimatedCostUsd -gt 0) { $Run.costStatus = 'ESTIMATED' }
    else { $Run.costStatus = 'UNKNOWN' }
}

if (Test-Path $manifestPath) {
    if (-not (Test-Path -LiteralPath $epochMarker) -or -not (Test-Path -LiteralPath $epochRecordPath)) { throw 'Existing manifest has no complete epoch governance record; refuse resume.' }
    if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'Existing manifest has no summary; refusing to reconstruct or reset prior run state.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.phase -ne 'Q' -or $manifest.model -ne $Model -or $manifest.maxSpendUsd -ne $MaxSpendUsd) {
        throw "Existing Phase Q manifest conflicts with requested certified model/ceiling; refusing to overwrite it."
    }
    if ($manifest.certificationEpoch -ne $CertificationEpoch -or $manifest.approvalMarker -ne 'PHASE_Q_REBASELINE_APPROVED=true') {
        throw "Existing manifest belongs to another or unapproved epoch; refusing to overwrite historical state."
    }
    $script:manifestPath = $manifestPath; $script:summaryPath = $summaryPath; $script:manifest = $manifest
    $manifest.runs = @($manifest.runs)
    $manifest.resumeOperations = @($manifest.resumeOperations) + @([pscustomobject]@{ atUtc = [DateTime]::UtcNow.ToString('o'); checkoutRoot = $repoRoot; action = 'resume-requested' })
    $manifest.historicalEpoch = 'HistoricalEpoch'
    if ($manifest.providerCostCaptureVerified -ne $true -or [string]::IsNullOrWhiteSpace([string]$manifest.providerCostCaptureEvidence)) {
        $manifest.phaseStatus = 'BLOCKED_COST_EVIDENCE'
        $script:manifest = $manifest
        Save-PartialState
        throw 'Authoritative generation-linked cost capture remains unverified; no billed execution may start.'
    }
    foreach ($manifestRun in $manifest.runs) {
        if ($manifestRun.evidencePath) { $manifestRun.evidencePath = Get-ManifestRelativePath $manifestRun.evidencePath }
        if ($manifestRun.consolePath) { $manifestRun.consolePath = Get-ManifestRelativePath $manifestRun.consolePath }
    }
    foreach ($historicalRun in $manifest.runs) {
        if ($historicalRun.status -in @('HISTORICAL_COMPLETE','HISTORICAL_UNCLASSIFIED')) {
            throw "Certification epoch cannot contain historical run $($historicalRun.runId); preserve it in HistoricalEpoch and start a separate ledger."
        }
    }
    $expectedRunIds = @('Q-A-1','Q-A-2','Q-A-3','Q-B-1','Q-B-2','Q-B-3','Q-C-1','Q-C-2','Q-C-3','Q-D-1','Q-D-2','Q-D-3')
    $actualRunIds = @($manifest.runs | ForEach-Object { [string]$_.runId })
    if ($actualRunIds.Count -ne 12 -or @($actualRunIds | Select-Object -Unique).Count -ne 12 -or @($expectedRunIds | Where-Object { $_ -notin $actualRunIds }).Count -gt 0) {
        throw 'Certification epoch manifest must contain each required matrix identity exactly once.'
    }
} else {
    $epochMarkerValue = [pscustomobject]@{ phase = 'Q'; historicalEpoch = 'HistoricalEpoch'; certificationEpoch = $CertificationEpoch; approved = $true; approvalMarker = 'PHASE_Q_REBASELINE_APPROVED=true'; decisionPath = 'docs/phase-q-rebaseline-decision.md'; runtime = 'Hermes'; provider = 'OpenRouter'; model = $Model; maxSpendUsd = $MaxSpendUsd; createdUtc = [DateTime]::UtcNow.ToString('o') }
    Write-AtomicJson $epochMarker $epochMarkerValue
    Write-AtomicJson $epochRecordPath $epochMarkerValue
    $manifest = [pscustomobject]@{
        phase = 'Q'; historicalEpoch = 'HistoricalEpoch'; certificationEpoch = $CertificationEpoch
        rebaselineDecision = 'docs/phase-q-rebaseline-decision.md'; approvalMarker = 'PHASE_Q_REBASELINE_APPROVED=true'
        matrix = '4 task classes by 3 repetitions'; runtime = 'Hermes'
        provider = 'OpenRouter'; model = $Model; maxSpendUsd = $MaxSpendUsd
        checkoutRootAtCreation = $repoRoot; createdUtc = [DateTime]::UtcNow.ToString('o')
        updatedUtc = [DateTime]::UtcNow.ToString('o'); phaseStatus = 'INCOMPLETE'
        resumeOperations = @(); providerCostCaptureVerified = $false; providerCostCaptureEvidence = $null
        providerReadiness = [pscustomobject]@{ keyPresent = $false; isFreeTier = $null; creditsHttpStatus = $null; totalCreditsUsd = $null; modelReachable = $false; ready = $false }
        runs = @()
    }
    foreach ($task in @('Q-A','Q-B','Q-C','Q-D')) {
        foreach ($rep in 1..3) {
            $manifest.runs += New-RunRecord "$task-$rep" $task $rep
        }
    }
}

# A previously RUNNING entry means ownership is uncertain. Mark INTERRUPTED and stop;
# never launch a duplicate or guess at provider spend.
$script:manifestPath = $manifestPath; $script:summaryPath = $summaryPath; $script:manifest = $manifest
$interrupted = $false
foreach ($run in $manifest.runs) {
    if ($run.status -eq 'RUNNING') {
        $resolvedConsolePath = Resolve-ManifestPath $run.consolePath
        $consoleText = if ($resolvedConsolePath -and (Test-Path -LiteralPath $resolvedConsolePath)) { Get-Content -LiteralPath $resolvedConsolePath -Raw } else { '' }
        $run.completedUtc = [DateTime]::UtcNow.ToString('o')
        $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null; $run.unknownCostAttempts = [Math]::Max(1, [int]$run.unknownCostAttempts)
        if ($consoleText -match '(?i)HTTP\s*402|insufficient credits|billing or credits exhausted') {
            $run.status = 'BLOCKED'; $run.failureClass = 'PROVIDER/BILLING'
            $run.detail = 'Recovered native test invocation interrupted while OpenRouter reported insufficient credits (HTTP 402). WorkItem outcome is failed/incomplete and provider usage/cost remains UNKNOWN.'
        } else {
            $run.status = 'INTERRUPTED'; $run.failureClass = 'UNKNOWN'
            $run.detail = 'Previous process ownership/result is uncertain; no automatic rerun.'
        }
        $interrupted = $true
    }
}
Save-State
if ($interrupted) { $script:manifest = $manifest; Save-State; throw "Interrupted live execution recorded as UNKNOWN cost; manual reconciliation required before more billed runs." }
$uncertifiableTerminalRuns = @($manifest.runs | Where-Object { $_.status -in @('FAIL','BLOCKED','TIMEOUT','CANCELLED','INTERRUPTED') -and $_.costStatus -ne 'REPORTED' })
if ($uncertifiableTerminalRuns.Count -gt 0) {
    throw "Terminal run(s) with non-authoritative or unknown usage/cost block further billed execution: $(@($uncertifiableTerminalRuns | ForEach-Object runId) -join ', ')."
}

Write-Host "phase=Q epoch=$CertificationEpoch runtime=Hermes provider=OpenRouter model=$Model ceilingUsd=$MaxSpendUsd root=$repoRoot"    $reportedTotal = [decimal](($manifest.runs | Measure-Object -Property reportedCostUsd -Sum).Sum)

$estimatedTotal = [decimal](($manifest.runs | Measure-Object -Property estimatedCostUsd -Sum).Sum)
$unknownRuns = @($manifest.runs | Where-Object { $_.costStatus -eq 'UNKNOWN' }).Count
if ($unknownRuns -gt 0) {
    $manifest.phaseStatus = 'INCOMPLETE_COST_UNKNOWN'; Save-State
    throw "Cost ceiling cannot be certified: $unknownRuns run(s)/attempt(s) have unknown cost. No further live runs launched."
}
if ($reportedTotal -ge [decimal]$MaxSpendUsd) { throw "Authoritative reported cost has reached the $MaxSpendUsd USD ceiling; no further run launched." }

# Docker + credentials are checked only after evidence is safely manifested.
& docker info *> $null
if ($LASTEXITCODE -ne 0) { $manifest.phaseStatus = 'BLOCKED_ENVIRONMENT'; Save-State; throw "Docker daemon not reachable; no live execution started." }
$factory = @(& docker ps -a --filter "label=factory=ai-factory" --format "{{.ID}}" | Where-Object { $_ })
if ($factory.Count -gt 0) { $manifest.phaseStatus = 'BLOCKED_ENVIRONMENT'; Save-State; throw "Factory-owned containers exist before benchmark; no live execution started." }

$probeKey = $null
$client = $null
try {
    Add-Type -AssemblyName System.Net.Http
    if (-not (Test-Path (Join-Path $repoRoot '.env.live'))) { throw ".env.live not found; no live execution started." }
    foreach ($line in Get-Content (Join-Path $repoRoot '.env.live')) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^OPENROUTER_API_KEY\s*=\s*(.*)$') { $probeKey = $Matches[1].Trim(); break }
    }
    if ([string]::IsNullOrWhiteSpace($probeKey)) { throw "OPENROUTER_API_KEY missing; no live execution started." }
    $client = New-Object System.Net.Http.HttpClient
    $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $probeKey)
    $keyResponse = $client.GetAsync('https://openrouter.ai/api/v1/key').Result
    $creditResponse = $client.GetAsync('https://openrouter.ai/api/v1/credits').Result
    $endpointResponse = $client.GetAsync("https://openrouter.ai/api/v1/models/$Model/endpoints").Result
    $keyHttp = [int]$keyResponse.StatusCode
    $creditsHttp = [int]$creditResponse.StatusCode
    $modelHttp = [int]$endpointResponse.StatusCode
    if (-not $keyResponse.IsSuccessStatusCode -or -not $creditResponse.IsSuccessStatusCode -or -not $endpointResponse.IsSuccessStatusCode) {
        $manifest.providerReadiness = [pscustomobject]@{ keyPresent = $true; isFreeTier = $null; creditsHttpStatus = $creditsHttp; totalCreditsUsd = $null; modelReachable = $false; ready = $false; keyHttpStatus = $keyHttp; modelHttpStatus = $modelHttp }
        $manifest.phaseStatus = 'BLOCKED_PROVIDER_READINESS'; Save-State
        throw "Non-billed OpenRouter readiness probe failed (statuses: key=$keyHttp, credits=$creditsHttp, model=$modelHttp); no live run started."
    }
    $keyData = ($keyResponse.Content.ReadAsStringAsync().Result | ConvertFrom-Json).data
    $creditData = ($creditResponse.Content.ReadAsStringAsync().Result | ConvertFrom-Json).data
    $modelData = ($endpointResponse.Content.ReadAsStringAsync().Result | ConvertFrom-Json).data
    $isFreeTier = $keyData.is_free_tier -eq $true
    $credits = if ($null -ne $creditData.total_credits) { [decimal]$creditData.total_credits } else { $null }
    $modelReachable = @($modelData.endpoints).Count -gt 0
    $manifest.providerReadiness = [pscustomobject]@{ keyPresent = $true; isFreeTier = $isFreeTier; creditsHttpStatus = $creditsHttp; totalCreditsUsd = $credits; modelReachable = $modelReachable; ready = (-not $isFreeTier -and $null -ne $credits -and $credits -gt 0 -and $modelReachable); keyHttpStatus = $keyHttp; modelHttpStatus = $modelHttp; checkedUtc = [DateTime]::UtcNow.ToString('o') }
    if ($isFreeTier) { $manifest.phaseStatus = 'BLOCKED_PROVIDER_BILLING'; Save-State; throw "OpenRouter key is free-tier; certification requires a paid account. No live run started." }
    if ($null -eq $credits -or $credits -le 0) { $manifest.phaseStatus = 'BLOCKED_PROVIDER_BILLING'; Save-State; throw "OpenRouter reports no positive account credits. No live run started." }
    if (-not $modelReachable) { $manifest.phaseStatus = 'BLOCKED_PROVIDER_READINESS'; Save-State; throw "The explicit certification model has no available endpoint. No live run started." }
    if ($manifest.providerCostCaptureVerified -ne $true -or [string]::IsNullOrWhiteSpace([string]$manifest.providerCostCaptureEvidence)) {
        $manifest.phaseStatus = 'BLOCKED_COST_EVIDENCE'; Save-State
        throw "Authoritative provider-cost capture and AgentRun correlation are not verified in the epoch manifest; no billed run started."
    }
} finally {
    if ($client) { $client.Dispose() }
    Remove-Variable probeKey -ErrorAction SilentlyContinue
}

foreach ($task in $Tasks) {
    if ($task -notin @('Q-A','Q-B','Q-C','Q-D')) { throw "Unknown task '$task'." }
    foreach ($rep in 1..$Reps) {
        $runId = "$task-$rep"
        $run = @($manifest.runs | Where-Object { $_.runId -eq $runId } | Select-Object -First 1)
        if ($run.Count -eq 0) { throw "Run identity $runId is not in the persisted Phase Q matrix." }
        $run = $run[0]
        if ($run.status -in @('PASS','COMPLETED','FAIL','BLOCKED','TIMEOUT','CANCELLED','INTERRUPTED','HISTORICAL_COMPLETE','HISTORICAL_UNCLASSIFIED')) {
            Write-Host "skip $runId status=$($run.status) certification=$($run.certificationStatus)"
            continue
        }
        if (@($manifest.runs | Where-Object { $_.costStatus -eq 'UNKNOWN' }).Count -gt 0) {
            $manifest.phaseStatus = 'INCOMPLETE_COST_UNKNOWN'; Save-State
            throw "Unknown cost blocks additional billed runs."
        }
        $currentCost = [decimal](($manifest.runs | Measure-Object -Property reportedCostUsd -Sum).Sum)
        if ($currentCost -ge [decimal]$MaxSpendUsd) { $run.status = 'NOT_RUN'; $run.detail = 'Authoritative reported-spend ceiling reached before launch.'; Save-State; break }
        $certifiedRemaining = [decimal]$MaxSpendUsd - $currentCost
        $nextRunReserve = [decimal]0.25
        if ($certifiedRemaining -le $nextRunReserve) { $run.status = 'NOT_RUN'; $run.detail = 'Insufficient remaining certifiable budget for the fixed conservative per-run reserve.'; Save-State; break }
        $run.authoritativeCostLimitUsd = $certifiedRemaining - $nextRunReserve
        $run.projectedRemainingBudgetUsd = [double]$certifiedRemaining
        if ($certifiedRemaining -le 0) { $run.status = 'NOT_RUN'; $run.detail = 'No certifiable budget remains before launch.'; Save-State; break }
        $factory = @(& docker ps -a --filter "label=factory=ai-factory" --format "{{.ID}}" | Where-Object { $_ })
        if ($factory.Count -gt 0) { $run.status = 'BLOCKED'; $run.failureClass = 'ENVIRONMENT'; $run.detail = 'Factory container present before launch.'; Save-State; break }

        # Preserve any original evidence, but never silently reuse a matrix identity.
        foreach ($evidencePath in @($run.evidencePath, $run.consolePath)) {
            $resolvedEvidencePath = Resolve-ManifestPath $evidencePath
            if ($resolvedEvidencePath -and (Test-Path -LiteralPath $resolvedEvidencePath)) {
                throw "Run $runId already has retained evidence but is not terminal; refuse implicit retry."
            }
        }
        $run.status = 'RUNNING'; $run.startedUtc = [DateTime]::UtcNow.ToString('o'); $run.completedUtc = $null
        $run.reportedCostUsd = $null; $run.estimatedCostUsd = $null
        $run.costStatus = 'UNKNOWN'; $run.unknownCostAttempts = 0
        $run.attemptNumber = [Math]::Max(0, [int]$run.attemptNumber) + 1
        $run.runIdentity = [guid]::NewGuid().ToString()
        $run.canonicalRunIdentity = $run.runIdentity
        $run.evidencePath = Get-ManifestRelativePath (Join-Path $resultsPath "$runId.txt")
        $run.consolePath = Get-ManifestRelativePath (Join-Path $resultsPath "$runId.console.log")
        Save-State
        $manifest.phaseStatus = 'RUNNING'
        Save-State
        $env:AI_FACTORY_LIVE_BENCHMARK = '1'
        $env:AI_FACTORY_CERT_MODEL = $Model
        $env:AI_FACTORY_PHASE_Q_TASK = $task
        $env:AI_FACTORY_PHASE_Q_EVIDENCE_FILE = Resolve-ManifestPath $run.evidencePath
        $manifest.phaseStatus = 'RUNNING'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $commandFailure = $null
        $LASTEXITCODE = 0
        try {
            & dotnet test apps/tests/AI.Factory.Worker.Tests/AI.Factory.Worker.Tests.csproj --filter 'FullyQualifiedName~PhaseQBenchmarkTests.Hermes_PhaseQ_Run_ThroughFactoryPath' --logger 'console;verbosity=detailed' *> (Resolve-ManifestPath $run.consolePath)
            $run.exitCode = $LASTEXITCODE
        } catch {
            $commandFailure = $_.Exception.GetType().FullName
            $run.exitCode = if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { $LASTEXITCODE } else { 1 }
        } finally {
            $watch.Stop(); $run.durationSeconds = [int]$watch.Elapsed.TotalSeconds
            Remove-Item Env:AI_FACTORY_LIVE_BENCHMARK -ErrorAction SilentlyContinue
            Remove-Item Env:AI_FACTORY_CERT_MODEL -ErrorAction SilentlyContinue
            Remove-Item Env:AI_FACTORY_PHASE_Q_TASK -ErrorAction SilentlyContinue
            Remove-Item Env:AI_FACTORY_PHASE_Q_EVIDENCE_FILE -ErrorAction SilentlyContinue
        }
        $run.completedUtc = [DateTime]::UtcNow.ToString('o')
        Persist-RunEvidence $run
        $resolvedRunEvidencePath = Resolve-ManifestPath $run.evidencePath
        if ($run.exitCode -eq 0 -and $resolvedRunEvidencePath -and (Test-Path -LiteralPath $resolvedRunEvidencePath)) {
            $ev = Get-Content -LiteralPath $resolvedRunEvidencePath -Raw
            $workItemMatch = [regex]::Match($ev, 'PHASEQ projectId=([^ ]+) workItemId=([^ ]+)')
            if ($workItemMatch.Success) { $run.workItemId = $workItemMatch.Groups[2].Value }
            $agentRunMatch = [regex]::Match($ev, 'PHASEQ agentRun id=([^ ]+)')
            if ($agentRunMatch.Success) { $run.agentRunId = $agentRunMatch.Groups[1].Value }
            $allGates = $ev -match 'PHASEQ workItem\.final=Completed' -and $ev -match 'PHASEQ validationRun success=True' -and $ev -match 'PHASEQ reviewRun blocksCompletion=False' -and $ev -match 'PHASEQ workspace\.missingRequired=\[\]' -and $ev -match 'PHASEQ containers\.after15s factoryOwned=0'
            $actualProviderCost = $false
            foreach ($line in ($ev -split "`r?`n")) {
                if ($line -match '^\s*PHASEQ\s+providerUsage=(\{.*\})') {
                    try {
                        $providerUsage = $Matches[1] | ConvertFrom-Json -ErrorAction Stop
                        if ($providerUsage.Source -eq 'openrouter:generation' -and $providerUsage.GenerationId -match '^gen-' -and $providerUsage.CostStatus -eq 'REPORTED' -and $null -ne $providerUsage.CostUsd -and [decimal]$providerUsage.CostUsd -ge 0 -and $providerUsage.AgentRunId -and $providerUsage.AgentRunId -eq $run.agentRunId -and $providerUsage.CertificationEpoch -eq $CertificationEpoch -and -not [string]::IsNullOrWhiteSpace([string]$providerUsage.EvidenceId)) {
                            $actualProviderCost = $true
                            $run.costStatus = 'REPORTED'; $run.reportedCostUsd = [decimal]$providerUsage.CostUsd
                            $run.agentRunId = [string]$providerUsage.AgentRunId; $run.generationId = [string]$providerUsage.GenerationId
                            $run.providerUsageEvidence = [string]$providerUsage.EvidenceId
                        }
                    } catch { throw "Malformed authoritative provider usage record for $runId; fail closed. $_" }
                }
            }
            $authoritative = Get-EvidenceReportedProviderCost $run ([string]$run.agentRunId) $CertificationEpoch
            if ($authoritative) {
                $actualProviderCost = $true
                $run.costStatus = 'REPORTED'; $run.reportedCostUsd = $authoritative.CostUsd
                $run.generationId = $authoritative.GenerationIds -join ','
            $run.providerUsageEvidence = $authoritative.EvidenceIds -join ','
            $run.providerUsageTokens = [pscustomobject]@{ input = $authoritative.InputTokens; output = $authoritative.OutputTokens; total = $authoritative.TotalTokens }
            }
            if ($allGates -and $actualProviderCost -and $run.unknownCostAttempts -eq 0) {
                $run.status = 'PASS'; $run.certificationStatus = 'PASS'; $run.failureClass = $null
                $run.generation = 'PASS'; $run.verification = 'PASS'; $run.review = 'PASS'; $run.acceptance = 'PASS'
                $run.workspaceIntegrity = 'PASS'; $run.credentialAudit = 'PASS'; $run.cleanup = 'PASS'
            } else {
                $run.status = 'FAIL'; $run.failureClass = 'EVIDENCE'
                $run.detail = 'Required Factory gates and/or authoritative, AgentRun-correlated OpenRouter generation billing evidence absent.'
            }
        } elseif ($run.exitCode -ne 0) {
            $resolvedConsolePath = Resolve-ManifestPath $run.consolePath
            $consoleText = if ($resolvedConsolePath -and (Test-Path -LiteralPath $resolvedConsolePath)) { Get-Content -LiteralPath $resolvedConsolePath -Raw } else { '' }
            if ($consoleText -match '(?i)HTTP\s*402|insufficient credits|billing or credits exhausted') {
                $run.status = 'BLOCKED'; $run.failureClass = 'PROVIDER/BILLING'
                $run.detail = 'OpenRouter rejected the live attempt for insufficient credits (HTTP 402); no benchmark completion. Provider usage/cost was not reported and remains UNKNOWN.'
            } else {
                $run.status = 'FAIL'; $run.failureClass = 'EVIDENCE'
                $run.detail = if ($commandFailure) { "Test process raised a terminating invocation error ($commandFailure); inspect preserved console/evidence." } else { 'Test command exited nonzero; inspect preserved console/evidence.' }
            }
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null; $run.unknownCostAttempts = [Math]::Max(1, [int]$run.unknownCostAttempts)

        } else {
            $run.status = 'INTERRUPTED'; $run.costStatus = 'UNKNOWN'; $run.unknownCostAttempts = [Math]::Max(1, [int]$run.unknownCostAttempts)
            $run.failureClass = 'EVIDENCE'; $run.detail = 'No per-run evidence; execution outcome/cost is uncertain.'
        }
        if ($run.costStatus -eq 'UNKNOWN') { $manifest.phaseStatus = 'INCOMPLETE_COST_UNKNOWN' }
        if ($run.costStatus -eq 'REPORTED' -and [decimal]$run.reportedCostUsd -gt $certifiedRemaining) {

            $run.status = 'FAIL'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'GOVERNANCE'
            $run.detail = 'The authoritative billed amount exceeded the remaining certified budget; stop the matrix.'
            $manifest.phaseStatus = 'BLOCKED_COST_CEILING'
        }
        Save-State
        $factoryAfter = @(& docker ps -a --filter "label=factory=ai-factory" --format "{{.ID}}" | Where-Object { $_ })
        if ($factoryAfter.Count -gt 0) { $run.status = 'FAIL'; $run.failureClass = 'CLEANUP_FAILURE'; $manifest.phaseStatus = 'BLOCKED'; Save-State; throw "Factory-owned container leak after $runId; stop matrix." }
        if ($run.costStatus -eq 'UNKNOWN' -or $run.costStatus -ne 'REPORTED') { throw "Run $runId lacks authoritative reported actual cost; stop further live executions." }
        if ($run.status -ne 'PASS') { throw "Run $runId did not pass all gates; stop matrix and classify before retry." }
        $spentSoFar = [decimal](($manifest.runs | Measure-Object -Property reportedCostUsd -Sum).Sum)
        if ($spentSoFar -gt [decimal]$MaxSpendUsd) { throw "Authoritative spend exceeds the certification ceiling; stop matrix immediately." }
        Start-Sleep -Seconds 10
    }
}$complete = @($manifest.runs | Where-Object { $_.status -eq 'PASS' -and $_.certificationStatus -eq 'PASS' -and $_.costStatus -eq 'REPORTED' }).Count -eq 12
$unknownAtEnd = @($manifest.runs | Where-Object { $_.costStatus -eq 'UNKNOWN' }).Count -gt 0
$reportedAtEnd = [decimal](($manifest.runs | Measure-Object -Property reportedCostUsd -Sum).Sum)
$manifest.phaseStatus = if ($unknownAtEnd) { 'INCOMPLETE_COST_UNKNOWN' } elseif ($reportedAtEnd -gt [decimal]$MaxSpendUsd) { 'BLOCKED_COST_CEILING' } elseif ($complete) { 'COMPLETE_PENDING_REPORT' } else { 'INCOMPLETE' }
Save-State
Write-Host "Phase Q status=$($manifest.phaseStatus); machine-readable manifest=$manifestPath; summary=$summaryPath"
