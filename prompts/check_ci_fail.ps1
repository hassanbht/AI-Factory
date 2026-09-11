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

$jobId = "102237717416"
$log = Invoke-RestMethod -Uri "https://api.github.com/repos/hassanbht/AI-Factory/actions/jobs/$jobId/logs" -Headers $headers
Set-Content -Path "C:\Users\Adak\.gemini\antigravity-ide\brain\423a2109-87b0-4560-ad3e-c7e23638a8d5\scratch\ci_build_test_fail.log" -Value $log

$lines = $log -split "`r?`n"
Write-Host "Total log lines: $($lines.Length)"
$failed = $lines | Where-Object { $_ -match "Failed:" -or $_ -match "Failed " -or $_ -match "Error:" -or $_ -match "\[FAIL\]" }
$failed | Select-Object -Last 30
