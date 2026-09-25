$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json

function Show-Endpoint($path, $method) {
    Write-Output "=================================================="
    Write-Output "ENDPOINT: $method $path"
    $ep = $json.paths.$path.$method
    if ($ep -eq $null) {
        Write-Output "Not found!"
        return
    }
    Write-Output "Summary: $($ep.summary)"
    if ($ep.requestBody) {
        $schemaRef = $ep.requestBody.content."application/json".schema.'$ref'
        Write-Output "Request Schema Ref: $schemaRef"
        if ($schemaRef) {
            $schemaName = $schemaRef.Split('/')[-1]
            $schema = $json.components.schemas.$schemaName
            Write-Output "Request Schema ($schemaName):"
            $schema | ConvertTo-Json -Depth 5 | Write-Output
        }
    }
    if ($ep.responses) {
        Write-Output "Responses:"
        foreach ($code in $ep.responses.PSObject.Properties.Name) {
            $resp = $ep.responses.$code
            $rRef = $resp.content."application/json".schema.'$ref'
            Write-Output "  Code $($code): $($resp.description) (Ref: $rRef)"
        }
    }
}

Show-Endpoint "/api/conversations" "post"
Show-Endpoint "/api/conversations/{conversation_id}" "get"
Show-Endpoint "/api/conversations/{conversation_id}/run" "post"
Show-Endpoint "/api/conversations/{conversation_id}/events" "post"
Show-Endpoint "/api/conversations/{conversation_id}/agent_final_response" "get"
Show-Endpoint "/api/conversations/{conversation_id}/interrupt" "post"
