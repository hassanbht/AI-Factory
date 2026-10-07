param([string]$ResultsDir = "TestResults/P2")

# Parses TRX with the XML parser; skips are counted from UnitTestResult outcomes, not inferred.
foreach ($run in (Get-ChildItem -Path $ResultsDir -Directory | Sort-Object Name)) {
  $t = 0; $p = 0; $f = 0; $s = 0; $badSkip = 0
  foreach ($file in (Get-ChildItem -LiteralPath $run.FullName -Filter *.trx)) {
    [xml]$x = Get-Content -LiteralPath $file.FullName -Raw
    $results = @($x.TestRun.Results.UnitTestResult)
    $ft = $results.Count
    $fp = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
    $ff = @($results | Where-Object { $_.outcome -eq 'Failed' }).Count
    $skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' })
    $fs = $skipped.Count
    foreach ($r in $skipped) {
      $msg = [string]$r.Output.StdOut + [string]$r.Output.ErrorInfo.Message
      if ($msg -notmatch 'LIVE OPT-IN NOT ENABLED|ENVIRONMENT BLOCKED') { $badSkip++ ; Write-Host "  non-gate skip: $($r.testName)" }
    }
    $asm = ($x.TestRun.TestDefinitions.UnitTest | Select-Object -First 1).storage | Split-Path -Leaf
    Write-Host ("  {0}: total={1} passed={2} failed={3} skipped={4}" -f $asm, $ft, $fp, $ff, $fs)
    $t += $ft; $p += $fp; $f += $ff; $s += $fs
  }
  Write-Host ("{0}: TOTAL={1} passed={2} failed={3} skipped={4} skipsWithoutGateReason={5}" -f $run.Name, $t, $p, $f, $s, $badSkip)
}
