$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json

Write-Output "=== LocalWorkspace-Input ==="
$json.components.schemas."LocalWorkspace-Input" | ConvertTo-Json -Depth 5 | Write-Output

Write-Output "`n=== Agent-Input.llm ==="
$json.components.schemas."Agent-Input".properties.llm | ConvertTo-Json -Depth 5 | Write-Output

$llmRef = $json.components.schemas."Agent-Input".properties.llm.'$ref'
if ($llmRef) {
    $llmName = $llmRef.Split('/')[-1]
    Write-Output "`n=== $llmName ==="
    $json.components.schemas.$llmName | ConvertTo-Json -Depth 5 | Write-Output
}
