param([string]$ResultsDir = "TestResults/PhaseQ/certification-epoch-2")

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot
$resultsPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ResultsDir))
$manifestPath = Join-Path $resultsPath 'manifest.json'
$summaryPath = Join-Path $resultsPath 'summary.json'
$epochPath = Join-Path $resultsPath 'epoch.json'
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
    if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $fullPath.Substring($prefix.Length).Replace('\', '/') }
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

function Save-PartialManifest($Value) {
    $Value.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-AtomicJson $manifestPath $Value
    $summary = [pscustomobject]@{
        phase = $Value.phase; historicalEpoch = $Value.historicalEpoch; certificationEpoch = $Value.certificationEpoch
        rebaselineDecision = $Value.rebaselineDecision; approvalMarker = $Value.approvalMarker; phaseStatus = $Value.phaseStatus
        runtime = $Value.runtime; provider = $Value.provider; model = $Value.model; maxSpendUsd = $Value.maxSpendUsd
        providerReadiness = $Value.providerReadiness; providerCostCaptureVerified = $Value.providerCostCaptureVerified
        totals = [pscustomobject]@{ required = @($Value.runs).Count; pass = @($Value.runs | Where-Object { $_.status -eq 'PASS' }).Count; fail = @($Value.runs | Where-Object { $_.status -eq 'FAIL' }).Count; blocked = @($Value.runs | Where-Object { $_.status -eq 'BLOCKED' }).Count; timeout = @($Value.runs | Where-Object { $_.status -eq 'TIMEOUT' }).Count; cancelled = @($Value.runs | Where-Object { $_.status -eq 'CANCELLED' }).Count; interrupted = @($Value.runs | Where-Object { $_.status -eq 'INTERRUPTED' }).Count; notRun = @($Value.runs | Where-Object { $_.status -eq 'NOT_RUN' }).Count; certified = @($Value.runs | Where-Object { $_.certificationStatus -eq 'PASS' }).Count; historicalCarriedForward = 0 }
        runs = @($Value.runs)
    }
    Write-AtomicJson $summaryPath $summary
}

if (Test-Path $manifestPath) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.certificationEpoch -ne 'PhaseQ-Certification-Epoch-2' -or $manifest.approvalMarker -ne 'PHASE_Q_REBASELINE_APPROVED=true') {
        throw 'Manifest is not an explicitly approved Phase Q certification epoch; refusing to rewrite it.'
    }
    if (@($manifest.runs).Count -ne 12) { throw 'Certification epoch manifest must contain exactly 12 matrix identities.' }
    if (-not (Test-Path -LiteralPath $epochPath) -or -not (Test-Path -LiteralPath $epochRecordPath)) { throw 'Certification manifest has no complete epoch record; refusing to rewrite it.' }
    $epoch = Get-Content -LiteralPath $epochPath -Raw | ConvertFrom-Json
    $epochRecord = Get-Content -LiteralPath $epochRecordPath -Raw | ConvertFrom-Json
    if ($epoch.certificationEpoch -ne $manifest.certificationEpoch -or $epoch.approved -ne $true -or $epochRecord.certificationEpoch -ne $manifest.certificationEpoch -or $epochRecord.approved -ne $true -or $epochRecord.approvalMarker -ne 'PHASE_Q_REBASELINE_APPROVED=true') { throw 'Epoch marker, audit record, and manifest disagree or approval is absent.' }
    foreach ($run in $manifest.runs) {
        $run.evidencePath = Get-ManifestRelativePath $run.evidencePath
        $run.consolePath = Get-ManifestRelativePath $run.consolePath
        $evidenceCandidate = Resolve-ManifestPath $run.evidencePath
        $consoleCandidate = Resolve-ManifestPath $run.consolePath
        $ev = if ($evidenceCandidate -and (Test-Path -LiteralPath $evidenceCandidate)) { $evidenceCandidate } elseif ($consoleCandidate -and (Test-Path -LiteralPath $consoleCandidate)) { $consoleCandidate } else { $null }
        if (-not $ev) { continue }
        $evidenceText = Get-Content -LiteralPath $ev -Raw
        $blocks = [regex]::Matches($evidenceText, '(?ms)^\s*PHASEQ agentRun id=([^ ]+).*?^\s*PHASEQ\s+metadata=(\{[^\r\n]+\})')
        $providerUsageRecords = [regex]::Matches($evidenceText, '(?m)^\s*PHASEQ\s+providerUsage=(\{[^\r\n]+\})')
        $costByAttempt = @{}; $reported = 0.0; $estimated = 0.0; $unknown = 0
        foreach ($block in $blocks) {
            $attemptId = $block.Groups[1].Value
            if ($costByAttempt.ContainsKey($attemptId)) { continue }
            try { $meta = $block.Groups[2].Value | ConvertFrom-Json -ErrorAction Stop }
            catch { $unknown++; continue }
            $amount = 0.0
            if ($null -eq $meta.CostUsd -or -not [double]::TryParse([string]$meta.CostUsd, [Globalization.NumberStyles]::Any, [Globalization.CultureInfo]::InvariantCulture, [ref]$amount)) { $unknown++; continue }
            $costByAttempt[$attemptId] = $true
            if ([string]$meta.CostSource -match 'estimated') { $estimated += $amount } else { $reported += $amount }
        }
        $matchedUsage = @()
        $seenGenerationIds = @{}
        $usageCorrelationInvalid = $false
        foreach ($usageRecord in $providerUsageRecords) {
            try { $usage = $usageRecord.Groups[1].Value | ConvertFrom-Json -ErrorAction Stop }
            catch { throw "Malformed provider-usage evidence for $($run.runId); refusing to summarize." }
            $validShape = $usage.Source -eq 'openrouter:generation' -and $usage.GenerationId -match '^gen-[0-9A-Za-z-]+$' -and $usage.CostStatus -eq 'REPORTED' -and $null -ne $usage.CostUsd -and [decimal]$usage.CostUsd -ge 0 -and -not [string]::IsNullOrWhiteSpace([string]$usage.EvidenceId)
            if (-not $validShape -or $usage.AgentRunId -ne $run.agentRunId -or $usage.CertificationEpoch -ne $manifest.certificationEpoch) {
                $usageCorrelationInvalid = $true
                continue
            }
            $generationId = [string]$usage.GenerationId
            if ($seenGenerationIds.ContainsKey($generationId)) { throw "Duplicate generation ID $generationId in provider evidence for $($run.runId)." }
            $seenGenerationIds[$generationId] = $true
            $matchedUsage += $usage
        }
        if ($usageCorrelationInvalid) {
            $run.status = 'FAIL'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'EVIDENCE'
            $run.detail = 'Provider usage contains missing or mismatched AgentRun/epoch correlation; certification denied.'
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null
            $unknown = [Math]::Max(1,$unknown)
        }
        if ($run.generationId -and $matchedUsage.Count -eq 0) {
            $run.status = 'FAIL'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'EVIDENCE'
            $run.detail = 'A billed run lacks a matching authoritative generation usage record.'
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null
            $unknown = [Math]::Max(1,$unknown)
        }
        if ($matchedUsage.Count -gt 0 -and $blocks.Count -gt 0) {
            $run.status = 'FAIL'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'EVIDENCE'
            $run.detail = 'Estimated Hermes telemetry cannot be merged into reported provider spend.'
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null
            $unknown = [Math]::Max(1,$unknown)
        }
        if ($blocks.Count -eq 0 -and $providerUsageRecords.Count -eq 0) { $unknown++ }
        if ($matchedUsage.Count -gt 0 -and -not $usageCorrelationInvalid -and -not ($run.generationId -and $matchedUsage.Count -eq 0) -and $blocks.Count -eq 0) {
            $run.reportedCostUsd = [Math]::Round([double](($matchedUsage | Measure-Object -Property CostUsd -Sum).Sum),8)
            $run.costStatus = 'REPORTED'
            $run.generationId = @($matchedUsage | ForEach-Object { [string]$_.GenerationId }) -join ','
            $run.providerUsageEvidence = @($matchedUsage | ForEach-Object { [string]$_.EvidenceId }) -join ','
        } else {
            $run.reportedCostUsd = $null
            $run.costStatus = if ($usageCorrelationInvalid -or $unknown -gt 0 -or $run.generationId) { 'UNKNOWN' } elseif ($estimated -gt 0) { 'ESTIMATED' } else { 'UNKNOWN' }
        }
        $reported = 0.0
        $run.estimatedCostUsd = [Math]::Round($estimated,8)
        $run.unknownCostAttempts = $unknown

        $consoleText = if ($consoleCandidate -and (Test-Path -LiteralPath $consoleCandidate)) { Get-Content -LiteralPath $consoleCandidate -Raw } else { '' }
        $providerFailure = $consoleText -match '(?i)HTTP\s*402|insufficient credits|billing or credits exhausted'
        if ($providerFailure) {
            $run.status = 'BLOCKED'; $run.failureClass = 'PROVIDER/BILLING'; $run.detail = 'OpenRouter rejected the live attempt for insufficient credits (HTTP 402); no benchmark completion. Provider usage/cost was not reported and remains UNKNOWN.'
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null; $run.unknownCostAttempts = [Math]::Max(1, $unknown)
        } elseif ($run.status -eq 'RUNNING') {
            $run.status = 'INTERRUPTED'; $run.failureClass = 'EVIDENCE'; $run.detail = 'Manifest showed RUNNING at inspection; status conservatively recovered as INTERRUPTED/UNKNOWN.'; $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null; $run.unknownCostAttempts = [Math]::Max(1,$unknown)
        } elseif ($run.status -eq 'BLOCKED' -and $run.failureClass -eq 'BILLING') {
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null; $run.unknownCostAttempts = [Math]::Max(1, $unknown)
        }
        if ($run.status -in @('PASS','COMPLETED') -and $run.generation -eq 'PASS' -and $run.costStatus -ne 'REPORTED') {
            $run.status = 'FAIL'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'EVIDENCE'
            $run.detail = 'A completed billed execution has no matching authoritative provider cost record.'
            $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null; $run.unknownCostAttempts = [Math]::Max(1,$run.unknownCostAttempts)
        }
    }
    $manifest.runs = @($manifest.runs)
} elseif (Test-Path $summaryPath) {
    throw 'Certification summary exists without its source manifest; refusing lossy reconstruction.'
} else {
    if (Test-Path -LiteralPath (Join-Path $resultsPath 'epoch.json') -or Test-Path -LiteralPath (Join-Path $resultsPath 'epoch-record.json')) {
        throw 'Certification epoch marker exists without its source manifest; refusing to invent matrix state.'
    }
    $approvalPath = Join-Path $repoRoot 'docs/phase-q-rebaseline-decision.md'
    if (-not (Test-Path -LiteralPath $approvalPath) -or (Get-Content -LiteralPath $approvalPath -Raw) -notmatch 'PHASE_Q_REBASELINE_APPROVED=true') {
        throw 'No explicit repository-visible approval exists for certification epoch creation.'
    }
    $manifest = [pscustomobject]@{
        phase = 'Q'; historicalEpoch = 'HistoricalEpoch'; certificationEpoch = 'PhaseQ-Certification-Epoch-2'
        approvalMarker = 'PHASE_Q_REBASELINE_APPROVED=true'; phaseStatus = 'INCOMPLETE'
        runtime = 'Hermes'; provider = 'OpenRouter'; model = 'deepseek/deepseek-v4-flash-0731'; maxSpendUsd = 2.00
        providerReadiness = [pscustomobject]@{ keyPresent = $true; isFreeTier = $true; creditsHttpStatus = 200; totalCreditsUsd = 0; modelReachable = $true; ready = $false }
        providerCostCaptureVerified = $false; runs = @()
    }
    foreach ($task in @('Q-A','Q-B','Q-C','Q-D')) {
        foreach ($rep in 1..3) {
            $id = "$task-$rep"
            $ev = Join-Path $resultsPath "$id.txt"
            $console = Join-Path $resultsPath "$id.console.log"
            $status = 'NOT_RUN'
            $detail = 'No evidence found.'
            $evidencePath = $null
            $reportedCost = 0.0
            $estimatedCost = 0.0
            $unknownAttempts = 0
            if (Test-Path $ev) { $status = 'HISTORICAL_UNCLASSIFIED'; $detail = 'Legacy evidence; not certified or rerun automatically.'; $evidencePath = $ev }
            elseif (Test-Path $console) { $status = 'HISTORICAL_UNCLASSIFIED'; $detail = 'Console-only legacy evidence; run outcome/cost not certified.'; $evidencePath = $console }
            if ($evidencePath) {
                $text = Get-Content -LiteralPath $evidencePath -Raw
                $blocks = [regex]::Matches($text, '(?ms)^PHASEQ agentRun id=([^ ]+).*?^PHASEQ\s+metadata=(\{[^\r\n]+\})')
                $seen = @{}
                foreach ($block in $blocks) {
                    $attemptId = $block.Groups[1].Value
                    if ($seen.ContainsKey($attemptId)) { continue }
                    $seen[$attemptId] = $true
                    try { $meta = $block.Groups[2].Value | ConvertFrom-Json -ErrorAction Stop }
                    catch { $unknownAttempts++; continue }
                    $amount = 0.0
                    if ($null -eq $meta.CostUsd -or -not [double]::TryParse([string]$meta.CostUsd, [Globalization.NumberStyles]::Any, [Globalization.CultureInfo]::InvariantCulture, [ref]$amount)) { $unknownAttempts++; continue }
                    if ([string]$meta.CostSource -match 'estimated') { $estimatedCost += $amount } else { $reportedCost += $amount }
                }
                if ($seen.Count -eq 0) { $unknownAttempts++ }
            }
            $legacyConsoleText = if (Test-Path $console) { Get-Content -LiteralPath $console -Raw } else { '' }
            if ($legacyConsoleText -match '(?i)HTTP\s*402|insufficient credits|billing or credits exhausted') {
                $status = 'BLOCKED'; $detail = 'OpenRouter rejected the live attempt for insufficient credits (HTTP 402); no benchmark completion. Provider usage/cost was not reported and remains UNKNOWN.'
            }
            $manifest.runs += [pscustomobject]@{
                runId = $id; task = $task; repetition = $rep; status = $status; certificationStatus = 'NOT_CERTIFIED'; detail = $detail
                failureClass = if ($status -eq 'BLOCKED') { 'PROVIDER/BILLING' } else { $null }
                historicalEpoch = 'HistoricalEpoch'; certificationEpoch = 'PhaseQ-Certification-Epoch-2'
                runtime = 'Hermes'; hermesImage = 'nousresearch/hermes-agent:0.20.6'; provider = 'OpenRouter'; model = 'deepseek/deepseek-v4-flash-0731'
                workItemId = $null; agentRunId = $null; generationId = $null; startedUtc = $null; completedUtc = $null
                generation = 'NOT_RUN'; verification = 'NOT_RUN'; review = 'NOT_RUN'; acceptance = 'NOT_RUN'
                workspaceIntegrity = 'NOT_RUN'; credentialAudit = 'NOT_RUN'; cleanup = 'NOT_RUN'; providerUsageEvidence = $null
                evidencePath = if ($evidencePath) { Get-ManifestRelativePath $evidencePath } else { $null }
                consolePath = if (Test-Path $console) { Get-ManifestRelativePath $console } else { $null }
                reportedCostUsd = if ($status -eq 'BLOCKED') { $null } else { [Math]::Round($reportedCost,8) }
                estimatedCostUsd = if ($status -eq 'BLOCKED') { $null } else { [Math]::Round($estimatedCost,8) }
                unknownCostAttempts = if ($status -eq 'BLOCKED') { [Math]::Max(1,$unknownAttempts) } else { $unknownAttempts }
                costStatus = if ($status -eq 'BLOCKED') { 'UNKNOWN' } elseif ($unknownAttempts -gt 0 -or -not $evidencePath) { 'UNKNOWN' } elseif ($reportedCost -gt 0) { 'REPORTED' } elseif ($estimatedCost -gt 0) { 'ESTIMATED' } else { 'UNKNOWN' }
            }
        }
    }
}

$runs = @($manifest.runs | Sort-Object task, repetition)
$requiredIds = @('Q-A-1','Q-A-2','Q-A-3','Q-B-1','Q-B-2','Q-B-3','Q-C-1','Q-C-2','Q-C-3','Q-D-1','Q-D-2','Q-D-3')
$actualIds = @($runs | ForEach-Object { [string]$_.runId })
if ($actualIds.Count -ne 12 -or @($actualIds | Select-Object -Unique).Count -ne 12 -or @($requiredIds | Where-Object { $_ -notin $actualIds }).Count -gt 0) {
    throw 'Certification epoch must contain each of the 12 required identities exactly once; refusing to produce a summary.'
}
foreach ($run in $runs) {
    if ($run.status -eq 'RUNNING') {
        $run.status = 'INTERRUPTED'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'EVIDENCE'
        $run.detail = 'Recovered a persisted RUNNING identity as INTERRUPTED; no implicit retry is permitted.'
        $run.costStatus = 'UNKNOWN'; $run.reportedCostUsd = $null
        $run.unknownCostAttempts = [Math]::Max(1,[int]$run.unknownCostAttempts)
        $run.completedUtc = [DateTime]::UtcNow.ToString('o')
    }
}
$preUnknownAttempts = [int](($runs | Measure-Object -Property unknownCostAttempts -Sum).Sum)
$preCertifiedPasses = @($runs | Where-Object { $_.status -eq 'PASS' -and $_.certificationStatus -eq 'PASS' -and $_.costStatus -eq 'REPORTED' }).Count
$manifest.phaseStatus = if ($manifest.providerReadiness -and $manifest.providerReadiness.ready -eq $false) { 'BLOCKED_PROVIDER_BILLING' } elseif ($preUnknownAttempts -gt 0) { 'INCOMPLETE_COST_UNKNOWN' } elseif ($manifest.providerCostCaptureVerified -ne $true) { 'BLOCKED_COST_EVIDENCE' } elseif ($preCertifiedPasses -eq 12) { 'COMPLETE_CANDIDATE' } else { 'INCOMPLETE' }
$known = 0.0; $estimated = 0.0; $unknownAttempts = 0
$pass = 0; $fail = 0; $notRun = 0; $historical = 0
Write-Host "# Phase Q partial/final evidence"
Write-Host "phaseStatus=$($manifest.phaseStatus) runtime=$($manifest.runtime) model=$($manifest.model) ceilingUsd=$($manifest.maxSpendUsd)"
Write-Host ''
Write-Host '| Task | Run | Status | Certification | Reported USD | Estimated USD | Unknown attempts | Detail |'
Write-Host '|---|---|---|---|---:|---:|---:|---|'
foreach ($run in $runs) {
    $allowedOutcomes = @('PASS','FAIL','BLOCKED','TIMEOUT','CANCELLED','INTERRUPTED','NOT_RUN','RUNNING')
    if ($run.status -notin $allowedOutcomes) { throw "Non-terminal or legacy outcome '$($run.status)' in certification epoch for $($run.runId)." }
    if ($run.status -eq 'PASS' -and ($run.certificationStatus -ne 'PASS' -or $run.costStatus -ne 'REPORTED' -or [int]$run.unknownCostAttempts -gt 0)) {
        $run.status = 'FAIL'; $run.certificationStatus = 'NOT_CERTIFIED'; $run.failureClass = 'EVIDENCE'
        $run.detail = 'PASS refused: certification requires explicit PASS status, authoritative REPORTED provider billing, and zero unresolved unknown-cost attempts.'
    }
    $reported = if ($null -ne $run.reportedCostUsd) { [double]$run.reportedCostUsd } else { 0.0 }
    $estimate = if ($null -ne $run.estimatedCostUsd) { [double]$run.estimatedCostUsd } else { 0.0 }
    $unknown = [int]$run.unknownCostAttempts
    $known += $reported; $estimated += $estimate; $unknownAttempts += $unknown
    if ($run.status -eq 'PASS' -and $run.certificationStatus -eq 'PASS' -and $run.costStatus -eq 'REPORTED' -and [int]$run.unknownCostAttempts -eq 0) { $pass++ }
    elseif ($run.status -in @('FAIL','TIMEOUT','CANCELLED','INTERRUPTED')) { $fail++ }
    elseif ($run.status -in @('NOT_RUN','BLOCKED')) { $notRun++ }
    elseif ($run.status -in @('HISTORICAL_COMPLETE','HISTORICAL_UNCLASSIFIED')) { $historical++ }
    $detail = ([string]$run.detail -replace '\|','/').Trim()
    Write-Host "| $($run.task) | $($run.runId) | $($run.status) | $($run.certificationStatus) | $reported | $estimate | $unknown | $detail |"
}
$total = $runs.Count
$costStatus = if ($known + $estimated -gt [double]$manifest.maxSpendUsd) { 'FAIL' }
    elseif ($unknownAttempts -gt 0) { 'LIMITED_UNKNOWN' }
    elseif ($manifest.providerReadiness -and $manifest.providerReadiness.ready -eq $false) { 'LIMITED_PROVIDER_BILLING' }
    elseif ($manifest.providerCostCaptureVerified -ne $true) { 'LIMITED_COST_EVIDENCE' }
    elseif ($pass -ne 12) { 'LIMITED_INCOMPLETE' }
    else { 'CERTIFIABLE' }
$phaseStatus = if ($manifest.providerReadiness -and $manifest.providerReadiness.ready -eq $false) { 'BLOCKED_PROVIDER_BILLING' } elseif ($unknownAttempts -gt 0) { 'INCOMPLETE_COST_UNKNOWN' } elseif ($manifest.providerCostCaptureVerified -ne $true) { 'BLOCKED_COST_EVIDENCE' } elseif ($pass -eq 12 -and $fail -eq 0) { 'COMPLETE_CANDIDATE' } else { 'INCOMPLETE' }
$manifest.phaseStatus = $phaseStatus
Write-Host ''
Write-Host "totalRuns=$total passedCertified=$pass failedOrInterrupted=$fail notRunOrBlocked=$notRun historicalUncertified=$historical"
Write-Host "reportedCostUsd=$([Math]::Round($known,8)) estimatedCostUsd=$([Math]::Round($estimated,8)) unknownCostAttempts=$unknownAttempts costCeilingStatus=$costStatus"
Write-Host "certificationEpoch=$($manifest.certificationEpoch) readinessReady=$($manifest.providerReadiness.ready) authoritativeCostCaptureVerified=$($manifest.providerCostCaptureVerified)"
Write-Host "derivedPhaseStatus=$phaseStatus"
Write-Host "manifest=$manifestPath"
Write-Host "summary=$summaryPath"
if ($manifest -is [System.Collections.IDictionary]) {
    $manifest['phaseStatus'] = $phaseStatus
    $manifest['updatedUtc'] = [DateTime]::UtcNow.ToString('o')
} else {
    $manifest.phaseStatus = $phaseStatus
    $manifest.updatedUtc = [DateTime]::UtcNow.ToString('o')
}
Write-AtomicJson $manifestPath $manifest
$summary = [pscustomobject]@{
    phase = $manifest.phase; historicalEpoch = $manifest.historicalEpoch; certificationEpoch = $manifest.certificationEpoch
    rebaselineDecision = $manifest.rebaselineDecision; approvalMarker = $manifest.approvalMarker; phaseStatus = $phaseStatus
    runtime = $manifest.runtime; provider = $manifest.provider; model = $manifest.model; maxSpendUsd = $manifest.maxSpendUsd
    providerReadiness = $manifest.providerReadiness; providerCostCaptureVerified = $manifest.providerCostCaptureVerified
    totals = [pscustomobject]@{ required = $total; pass = $pass; fail = $fail; blocked = @($runs | Where-Object { $_.status -eq 'BLOCKED' }).Count; timeout = @($runs | Where-Object { $_.status -eq 'TIMEOUT' }).Count; cancelled = @($runs | Where-Object { $_.status -eq 'CANCELLED' }).Count; interrupted = @($runs | Where-Object { $_.status -eq 'INTERRUPTED' }).Count; notRun = @($runs | Where-Object { $_.status -eq 'NOT_RUN' }).Count; certified = $pass; unknownCostAttempts = $unknownAttempts; reportedCostUsd = [Math]::Round($known,8); estimatedCostUsd = [Math]::Round($estimated,8); costCeilingStatus = $costStatus; historicalCarriedForward = 0 }
    runs = @($manifest.runs)
}
Write-AtomicJson $summaryPath $summary
