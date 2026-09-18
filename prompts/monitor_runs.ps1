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

$runs = Invoke-RestMethod -Uri "https://api.github.com/repos/hassanbht/AI-Factory/actions/runs" -Headers $headers
$recent = $runs.workflow_runs | Where-Object { $_.id -in @("34280413603", "34280430620") }
$recent | Select-Object id, name, status, conclusion, created_at | Format-Table -AutoSize

foreach ($r in $recent) {
    Write-Host "--- Jobs for $($r.name) (ID: $($r.id)) ---"
    $jobs = Invoke-RestMethod -Uri "https://api.github.com/repos/hassanbht/AI-Factory/actions/runs/$($r.id)/jobs" -Headers $headers
    $jobs.jobs | Select-Object id, name, status, conclusion, started_at, completed_at | Format-Table -AutoSize
}
