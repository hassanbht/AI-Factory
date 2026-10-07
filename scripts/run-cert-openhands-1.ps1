$env:AI_FACTORY_LIVE_CERT_OPENHANDS = '1'
$env:AI_FACTORY_CERT_MODEL = 'qwen/qwen3-coder'
$env:AI_FACTORY_CERT_EVIDENCE_FILE = 'TestResults/openhands-cert-run1.txt'

Write-Host "Starting OpenHands Certification Run 1 with model $env:AI_FACTORY_CERT_MODEL..."
dotnet test apps/tests/AI.Factory.Worker.Tests/AI.Factory.Worker.Tests.csproj --filter "FullyQualifiedName~ModelBackedCertificationTests.OpenHands" --logger "console;verbosity=detailed"
$code = $LASTEXITCODE

Remove-Item Env:AI_FACTORY_LIVE_CERT_OPENHANDS, Env:AI_FACTORY_CERT_MODEL, Env:AI_FACTORY_CERT_EVIDENCE_FILE
Write-Host "OpenHands Certification Run 1 finished with exit code $code"
exit $code
