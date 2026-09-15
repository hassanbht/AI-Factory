$credInput = "protocol=https`nhost=github.com`n`n"
$credOutput = $credInput | git credential fill
$token = ""
foreach ($line in ($credOutput -split "`r?`n")) {
    if ($line.StartsWith("password=")) {
        $token = $line.Substring(9)
        break
    }
}

$headers = @{
    "Authorization" = "Bearer $token"
    "Accept" = "application/vnd.github.v3+json"
    "User-Agent" = "AI-Factory-Agent"
}

$jobId = "102237833809"
$log = Invoke-RestMethod -Uri "https://api.github.com/repos/hassanbht/AI-Factory/actions/jobs/$jobId/logs" -Headers $headers
$lines = $log -split "`r?`n"
Write-Host "Stability gate log lines: $($lines.Length)"
$lines | Select-Object -Last 15
