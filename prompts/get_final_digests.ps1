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

$jobId = "102244651589"
$log = Invoke-RestMethod -Uri "https://api.github.com/repos/hassanbht/AI-Factory/actions/jobs/$jobId/logs" -Headers $headers
Set-Content -Path "C:\Users\Adak\.gemini\antigravity-ide\brain\423a2109-87b0-4560-ad3e-c7e23638a8d5\scratch\docker_release_run4.log" -Value $log

$lines = $log -split "`r?`n"
Write-Host "Total lines: $($lines.Length)"
$lines | Where-Object { $_ -match "api_digest" -or $_ -match "worker_digest" -or $_ -match "Published image digests" -or $_ -match "ghcr.io/hassanbht/ai-factory" } | Select-Object -Last 20
