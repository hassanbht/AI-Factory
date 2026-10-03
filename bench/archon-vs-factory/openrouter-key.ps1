# Dot-sourced by start-factory.ps1 and run-factory.ps1: loads OPENROUTER_API_KEY from the gitignored .env.live
# into this process only (the user-level OPENROUTER_API_KEY points at an account without credits). Never printed.
$envFile = Join-Path $PSScriptRoot '..\..\.env.live'
if (-not (Test-Path $envFile)) { throw ".env.live not found at $envFile" }
$keyLine = Get-Content $envFile | Where-Object { $_ -match '^\s*OPENROUTER_API_KEY\s*=' } | Select-Object -First 1
if (-not $keyLine) { throw "OPENROUTER_API_KEY missing in .env.live" }
$env:OPENROUTER_API_KEY = ($keyLine -split '=', 2)[1].Trim().Trim('"')
