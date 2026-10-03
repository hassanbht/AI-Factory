# Polls an AI Factory project until it needs a human or ends, logging each status change with a timestamp.
param(
    [Parameter(Mandatory)][string]$ProjectId,
    [Parameter(Mandatory)][string]$RunName,
    [string]$Api = 'http://localhost:5223',
    [int]$TimeoutMinutes = 60,
    [int]$PollSeconds = 15
)

$log = "C:\bench-runs\$RunName.log"
$stopAt = 'HumanReview', 'Completed', 'Failed', 'Cancelled', 'Paused'
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$last = $null
while ((Get-Date) -lt $deadline) {
    try {
        $p = Invoke-RestMethod "$Api/api/projects/$ProjectId"
        if ($p.status -ne $last) {
            "STATUS $((Get-Date).ToString('o')) $($p.status) $($p.currentError)" | Add-Content $log
            $last = $p.status
        }
        if ($p.status -in $stopAt) { "END $((Get-Date).ToString('o')) $($p.status)" | Add-Content $log; break }
    } catch {
        "POLL-ERROR $((Get-Date).ToString('o')) $($_.Exception.Message)" | Add-Content $log
    }
    Start-Sleep -Seconds $PollSeconds
}
if ((Get-Date) -ge $deadline) { "TIMEOUT $((Get-Date).ToString('o')) last=$last" | Add-Content $log }
Get-Content $log
