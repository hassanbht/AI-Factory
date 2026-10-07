#!/usr/bin/env bash
# Phase P.1 — R1 clean build, then full default regression x3, TRX parsed via XML parser.
set -u
SUMMARY=/tmp/regress-final-summary.txt
: > "$SUMMARY"

echo "=== CLEAN BUILD (--no-incremental) ===" >> "$SUMMARY"
dotnet build apps/AI.Factory.slnx --no-incremental -v q > /tmp/clean-build.log 2>&1
echo "build exit=$?" >> "$SUMMARY"
grep -E "Warning\(s\)|Error\(s\)" /tmp/clean-build.log >> "$SUMMARY"
grep -cE " error " /tmp/clean-build.log >> "$SUMMARY"

for i in 1 2 3; do
  rm -rf apps/tests/TestResults apps/tests/*/TestResults
  dotnet test apps/AI.Factory.slnx --no-build --logger trx > "/tmp/final-run-$i.log" 2>&1
  code=$?
  echo "=== RUN $i (dotnet test exit=$code) ===" >> "$SUMMARY"
  python scripts/parse-trx.py apps/tests >> "$SUMMARY" 2>&1
  echo "PARSE_EXIT=$?" >> "$SUMMARY"
done
cat "$SUMMARY"
echo "ALL_DONE"
