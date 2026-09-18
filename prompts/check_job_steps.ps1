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

$job = Invoke-RestMethod -Uri "https://api.github.com/repos/hassanbht/AI-Factory/actions/jobs/102237833809" -Headers $headers
$job | Select-Object id, name, status, conclusion, started_at, completed_at | Format-Table -AutoSize
$job.steps | Select-Object number, name, status, conclusion | Format-Table -AutoSize
