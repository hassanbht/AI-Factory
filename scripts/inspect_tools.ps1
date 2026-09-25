$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json

Write-Output "=== Agent-Input.tools ==="
$json.components.schemas."Agent-Input".properties.tools | ConvertTo-Json -Depth 5 | Write-Output
