$envContent = Get-Content 'C:\AI-Factory\.env.live'
$apiKey = ""
foreach ($line in $envContent) {
    if ($line -match "^OPENROUTER_API_KEY=(.+)$") {
        $apiKey = $matches[1].Trim()
    }
}

$payload = @{
    workspace = @{
        kind = "LocalWorkspace"
        working_dir = "/workspace"
    }
    agent_settings = @{
        agent_kind = "openhands"
        agent = "CodeActAgent"
        llm = @{
            model = "openrouter/deepseek/deepseek-v4-flash-0731:free"
            api_key = $apiKey
            base_url = "https://openrouter.ai/api/v1"
        }
    }
} | ConvertTo-Json -Depth 5

$url = "http://127.0.0.1:48080/api/conversations"
$headers = @{
    "X-Session-API-Key" = "test-session-key-spike-e0-987654"
    "Content-Type" = "application/json"
}

Write-Output "Sending POST with agent_settings to $url..."
try {
    $resp = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $payload
    Write-Output "Conversation created with agent_settings successfully!"
    Write-Output "Conversation ID: $($resp.id)"
    Write-Output "Agent type: $($resp.agent.GetType().FullName)"
    $resp | ConvertTo-Json -Depth 5 | Set-Content "C:\AI-Factory\scripts\created_with_agent_settings.json"
} catch {
    Write-Output "Error: $_"
    if ($_.Exception.Response) {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body = $reader.ReadToEnd()
        Write-Output "Response body: $body"
    }
}
