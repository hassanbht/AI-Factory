$env:AI_FACTORY_LIVE_CERT_HERMES = '1'
$env:AI_FACTORY_CERT_MODEL = 'deepseek/deepseek-v4-flash-0731'
$env:AI_FACTORY_CERT_EVIDENCE_FILE = 'TestResults/hermes-cert-run2.txt'

Write-Host "Starting Hermes Certification Run 2 with model $env:AI_FACTORY_CERT_MODEL..."
dotnet test apps/tests/AI.Factory.Worker.Tests/AI.Factory.Worker.Tests.csproj --filter "FullyQualifiedName~ModelBackedCertificationTests.Hermes" --logger "console;verbosity=detailed"
$code = $LASTEXITCODE

Remove-Item Env:AI_FACTORY_LIVE_CERT_HERMES, Env:AI_FACTORY_CERT_MODEL, Env:AI_FACTORY_CERT_EVIDENCE_FILE
Write-Host "Hermes Certification Run 2 finished with exit code $code"
exit $code
