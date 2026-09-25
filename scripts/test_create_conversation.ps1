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
    agent = @{
        kind = "Agent"
        llm = @{
            model = "openrouter/deepseek/deepseek-v4-flash-0731:free"
            api_key = $apiKey
            base_url = "https://openrouter.ai/api/v1"
        }
        tools = @(
            @{ name = "TerminalTool" },
            @{ name = "FileEditorTool" }
        )
    }
} | ConvertTo-Json -Depth 5

$url = "http://127.0.0.1:48080/api/conversations"
$headers = @{
    "X-Session-API-Key" = "test-session-key-spike-e0-987654"
    "Content-Type" = "application/json"
}

Write-Output "Sending POST to $url..."
try {
    $resp = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $payload
    Write-Output "Conversation created with tools successfully!"
    Write-Output "Conversation ID: $($resp.id)"
    Write-Output "Tools: $(($resp.agent.tools | ForEach-Object { $_.name }) -join ', ')"
    $resp | ConvertTo-Json -Depth 5 | Set-Content "C:\AI-Factory\scripts\created_conversation_with_tools.json"
} catch {
    Write-Output "Error: $_"
    if ($_.Exception.Response) {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        $body = $reader.ReadToEnd()
        Write-Output "Response body: $body"
    }
}
