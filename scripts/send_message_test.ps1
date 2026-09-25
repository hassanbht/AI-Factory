$url = 'http://127.0.0.1:48080'
$convId = '1aafc279-e32e-4267-9b4e-b7271076936d'
$headers = @{
    'X-Session-API-Key' = 'test-session-key-spike-e0-987654'
    'Content-Type' = 'application/json'
}
$taskText = "Please create a file named OPENHANDS_POC.txt in the current working directory (/workspace) with the exact content: Hello from OpenHands AI Factory Spike`nThen finish."
$msgPayload = @{
    role = 'user'
    content = @(
        @{
            type = 'text'
            text = $taskText
        }
    )
    run = $true
} | ConvertTo-Json -Depth 5

Write-Output "Sending message to $convId..."
try {
    $res = Invoke-RestMethod -Uri "$url/api/conversations/$convId/events" -Method Post -Headers $headers -Body $msgPayload
    Write-Output "Sent successfully!"
    $res | ConvertTo-Json | Write-Output
} catch {
    Write-Output "Error: $_"
    if ($_.Exception.Response) {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream)
        Write-Output "Body: $($reader.ReadToEnd())"
    }
}
