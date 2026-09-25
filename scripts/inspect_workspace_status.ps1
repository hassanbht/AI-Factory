$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json

function Show-Schema($name) {
    Write-Output "=================================================="
    Write-Output "SCHEMA: $name"
    $schema = $json.components.schemas.$name
    if ($schema -eq $null) {
        Write-Output "Not found!"
        return
    }
    $schema | ConvertTo-Json -Depth 5 | Write-Output
}

# Find what workspace schema is
$ws = $json.components.schemas.StartConversationRequest.properties.workspace
Write-Output "StartConversationRequest.workspace:"
$ws | ConvertTo-Json -Depth 5 | Write-Output

# Find execution_status
$status = $json.components.schemas.ConversationInfo.properties.execution_status
Write-Output "`nConversationInfo.execution_status:"
$status | ConvertTo-Json -Depth 5 | Write-Output

Show-Schema "ExecutionStatus"
Show-Schema "ConversationExecutionStatus"
Show-Schema "AgentBase-Input"
Show-Schema "AgentSettingsBase"
