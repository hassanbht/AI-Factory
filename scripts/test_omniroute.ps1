$envContent = Get-Content 'C:\AI-Factory\.env.live'
$omniKey = ""
foreach ($line in $envContent) {
    if ($line -match "^OMNIROUTE_API_KEY=(.+)$") {
        $omniKey = $matches[1].Trim()
    }
}

$url = "http://127.0.0.1:48080"
$headers = @{
    "X-Session-API-Key" = "test-session-key-spike-e0-987654"
    "Content-Type" = "application/json"
}

Write-Output "--- Testing OmniRoute Spike ---"

$createPayload = @{
    workspace = @{
        kind = "LocalWorkspace"
        working_dir = "/workspace"
    }
    agent_settings = @{
        agent_kind = "openhands"
        agent = "CodeActAgent"
        llm = @{
            model = "openai/agentrouter/deepseek-v4-flash"
            api_key = $omniKey
            base_url = "http://host.docker.internal:20128/v1"
        }
    }
} | ConvertTo-Json -Depth 5

try {
    $conv = Invoke-RestMethod -Uri "$url/api/conversations" -Method Post -Headers $headers -Body $createPayload
    $convId = $conv.id
    Write-Output "Created conversation with OmniRoute: $convId"

    $msgPayload = @{
        role = "user"
        content = @(
            @{
                type = "text"
                text = "Say hello in one word."
            }
        )
        run = $true
    } | ConvertTo-Json -Depth 5

    $sendRes = Invoke-RestMethod -Uri "$url/api/conversations/$convId/events" -Method Post -Headers $headers -Body $msgPayload
    Write-Output "Message sent with run=true. Polling for result..."

    Start-Sleep -Seconds 4
    $statusRes = Invoke-RestMethod -Uri "$url/api/conversations/$convId" -Method Get -Headers $headers
    Write-Output "Status after 4s: $($statusRes.execution_status)"
} catch {
    Write-Output "Error: $_"
    if ($_.Exception.Response) {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        Write-Output "Body: $($reader.ReadToEnd())"
    }
}
