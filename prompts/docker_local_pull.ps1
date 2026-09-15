$credInput = "protocol=https`nhost=github.com`n`n"
$credOutput = $credInput | git credential fill
$token = ""
foreach ($line in ($credOutput -split "`r?`n")) {
    if ($line.StartsWith("password=")) {
        $token = $line.Substring(9)
        break
    }
}

$token | docker login ghcr.io -u hassanbht --password-stdin

$apiImage = "ghcr.io/hassanbht/ai-factory/api@sha256:53bc943cdce03a33835ce22f604e9af7d58f61b892a566592ef3fb4c2d7512e4"
$workerImage = "ghcr.io/hassanbht/ai-factory/worker@sha256:c72b2496f60e4b200d258e75a8c33294dc3cd276f282736acd1af3bbcebaba50"

Write-Host "Pulling published API: $apiImage"
docker pull $apiImage

Write-Host "Pulling published Worker: $workerImage"
docker pull $workerImage
