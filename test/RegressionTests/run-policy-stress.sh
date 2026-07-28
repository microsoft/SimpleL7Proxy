#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
report_path=$(mktemp "${TMPDIR:-/tmp}/simplel7proxy-policy-stress-report.XXXXXX")

cleanup() {
    rm -f -- "$report_path"
}
trap cleanup EXIT

touch "$report_path"

set +e
POLICY_STRESS_REPORT_PATH="$report_path" \
    dotnet test "$script_dir/SimpleL7Proxy.Test.csproj" -- \
    --filter "FullyQualifiedName~PolicyScenarioIntegrationTests.V31Policy_SustainsOneThousandRequestorsForThirtyMinutes" &
test_pid=$!

tail -n +1 -f "$report_path" &
tail_pid=$!

wait "$test_pid"
test_status=$?
kill "$tail_pid" 2>/dev/null
wait "$tail_pid" 2>/dev/null
set -e

printf '\nFinal stress report\n\n'
cat "$report_path"

exit "$test_status"