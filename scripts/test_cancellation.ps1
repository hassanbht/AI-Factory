$envContent = Get-Content 'C:\AI-Factory\.env.live'
$apiKey = ""
foreach ($line in $envContent) {
    if ($line -match "^OPENROUTER_API_KEY=(.+)$") {
        $apiKey = $matches[1].Trim()
    }
}

$url = "http://127.0.0.1:48080"
$headers = @{
    "X-Session-API-Key" = "test-session-key-spike-e0-987654"
    "Content-Type" = "application/json"
}

Write-Output "--- Testing OpenHands Interruption / Cancellation ---"

# 1. Create conversation
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

$conv = Invoke-RestMethod -Uri "$url/api/conversations" -Method Post -Headers $headers -Body $createPayload
$convId = $conv.id
Write-Output "Created conversation: $convId"

# 2. Send a task that will take a while
$msgPayload = @{
    role = "user"
    content = @(
        @{
            type = "text"
            text = "Run a bash command that sleeps for 25 seconds: sleep 25"
        }
    )
    run = $true
} | ConvertTo-Json -Depth 5

$sendRes = Invoke-RestMethod -Uri "$url/api/conversations/$convId/events" -Method Post -Headers $headers -Body $msgPayload
Write-Output "Sent long-running task with run=true."

# 3. Wait 1 second so it enters 'running'
Start-Sleep -Seconds 1
$status1 = (Invoke-RestMethod -Uri "$url/api/conversations/$convId" -Method Get -Headers $headers).execution_status
Write-Output "Status right after start: $status1"

# 4. Trigger Interrupt
Write-Output "Sending POST /interrupt..."
$interruptRes = Invoke-RestMethod -Uri "$url/api/conversations/$convId/interrupt" -Method Post -Headers $headers
Write-Output "Interrupt response: $(ConvertTo-Json $interruptRes)"

# 5. Observe status after interrupt
Start-Sleep -Seconds 1
$status2 = (Invoke-RestMethod -Uri "$url/api/conversations/$convId" -Method Get -Headers $headers).execution_status
Write-Output "Status after interrupt: $status2"

Start-Sleep -Seconds 2
$status3 = (Invoke-RestMethod -Uri "$url/api/conversations/$convId" -Method Get -Headers $headers).execution_status
Write-Output "Status after 3 seconds: $status3"
