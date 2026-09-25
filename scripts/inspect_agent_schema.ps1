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

Show-Schema "LocalWorkspace-Input"
Show-Schema "Agent-Input"
Show-Schema "LLMConfig"
