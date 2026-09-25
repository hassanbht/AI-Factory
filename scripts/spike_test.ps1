$url = "http://127.0.0.1:48080"

Write-Output "=== Test 1: No Key ==="
curl.exe -s -w "\nHTTP_STATUS: %{http_code}\n" "$url/api/conversations"

Write-Output "`n=== Test 2: Invalid Key ==="
curl.exe -s -w "\nHTTP_STATUS: %{http_code}\n" -H "X-Session-API-Key: invalid-key-xyz" "$url/api/conversations"

Write-Output "`n=== Test 3: Valid Key ==="
curl.exe -s -w "\nHTTP_STATUS: %{http_code}\n" -H "X-Session-API-Key: test-session-key-spike-e0-987654" "$url/api/conversations"

Write-Output "`n=== Test 4: OpenAPI JSON ==="
curl.exe -s -o "C:\AI-Factory\scripts\openhands-openapi.json" -w "OpenAPI HTTP_STATUS: %{http_code}\n" "$url/openapi.json"
$len = (Get-Item "C:\AI-Factory\scripts\openhands-openapi.json").Length
Write-Output "OpenAPI file size: $len bytes"
