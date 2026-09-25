$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json

Write-Output "=== Tool-Input ==="
$json.components.schemas."Tool-Input" | ConvertTo-Json -Depth 5 | Write-Output
