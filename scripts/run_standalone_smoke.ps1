$envContent = Get-Content 'C:\AI-Factory\.env.live'
$apiKey = ""
foreach ($line in $envContent) {
    if ($line -match "^OPENROUTER_API_KEY=(.+)$") {
        $apiKey = $matches[1].Trim()
    }
}

$tempWs = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "ai-factory-openhands-spike")
$pocFile = [System.IO.Path]::Combine($tempWs, "OPENHANDS_POC.txt")
if (Test-Path $pocFile) {
    Remove-Item -Force $pocFile
}

Write-Output "--- Starting Gate E0 Standalone Smoke ---"
Write-Output "Target Workspace: $tempWs"

# 1. Create conversation with agent_settings (CodeActAgent)
$createPayload = @{
    workspace = @{
        kind = "LocalWorkspace"
        working_dir = "/workspace"
    }
    agent_settings = @{
        agent_kind = "openhands"
        agent = "CodeActAgent"
        llm = @{
            model = "openrouter/deepseek/deepseek-v4-flash-0731"
            api_key = $apiKey
            base_url = "https://openrouter.ai/api/v1"
        }
    }
} | ConvertTo-Json -Depth 5

$url = "http://127.0.0.1:48080"
$headers = @{
    "X-Session-API-Key" = "test-session-key-spike-e0-987654"
    "Content-Type" = "application/json"
}

$conv = Invoke-RestMethod -Uri "$url/api/conversations" -Method Post -Headers $headers -Body $createPayload
$convId = $conv.id
Write-Output "1. Conversation created: $convId (Status: $($conv.execution_status))"

# 2. Send task message with run = true
$taskText = "Please create a file named OPENHANDS_POC.txt in the current directory (/workspace) with the exact content: Hello from OpenHands AI Factory Spike`nThen finish."
$msgPayload = @{
    role = "user"
    content = @(
        @{
            type = "text"
            text = $taskText
        }
    )
    run = $true
} | ConvertTo-Json -Depth 5

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$sendRes = Invoke-RestMethod -Uri "$url/api/conversations/$convId/events" -Method Post -Headers $headers -Body $msgPayload
Write-Output "2. Task message sent with run=true. Response success: $($sendRes.success)"

# 3. Poll conversation status until finished, error, or timeout
$maxPollSeconds = 120
$pollInterval = 3
$finalStatus = ""

while ($sw.Elapsed.TotalSeconds -lt $maxPollSeconds) {
    Start-Sleep -Seconds $pollInterval
    $statusRes = Invoke-RestMethod -Uri "$url/api/conversations/$convId" -Method Get -Headers $headers
    $currentStatus = $statusRes.execution_status
    Write-Output "   Polling [$([int]$sw.Elapsed.TotalSeconds)s]: execution_status = $currentStatus"
    if ($currentStatus -eq "finished" -or $currentStatus -eq "error") {
        $finalStatus = $currentStatus
        break
    }
    if ($currentStatus -eq "idle" -and $sw.Elapsed.TotalSeconds -gt 10) {
        $finalStatus = $currentStatus
        break
    }
}

$sw.Stop()
Write-Output "3. Execution stopped. Final execution_status: $finalStatus in $($sw.ElapsedMilliseconds) ms"

# 4. Fetch agent_final_response
try {
    $finalResp = Invoke-RestMethod -Uri "$url/api/conversations/$convId/agent_final_response" -Method Get -Headers $headers
    Write-Output "4. Agent Final Response: $($finalResp.response)"
} catch {
    Write-Output "4. Agent Final Response: could not fetch ($($_))"
}

# 5. Verify the file exists on host
Write-Output "5. Verifying file on host at $pocFile..."
if (Test-Path $pocFile) {
    $content = Get-Content -Raw $pocFile
    Write-Output "   FILE FOUND! Content:"
    Write-Output "   '$content'"
    if ($content.Trim() -eq "Hello from OpenHands AI Factory Spike") {
        Write-Output "   >>> SUCCESS: Content exactly matches! <<<"
    } else {
        Write-Output "   >>> PARTIAL: Content differs: '$content' <<<"
    }
} else {
    Write-Output "   >>> ERROR: File does not exist! <<<"
    Get-ChildItem $tempWs | ForEach-Object { Write-Output "   Found in dir: $($_.Name)" }
}
