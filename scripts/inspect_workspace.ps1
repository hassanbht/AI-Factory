$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json

Write-Output "=== LocalWorkspace-Input ==="
$json.components.schemas."LocalWorkspace-Input" | ConvertTo-Json -Depth 5 | Write-Output
