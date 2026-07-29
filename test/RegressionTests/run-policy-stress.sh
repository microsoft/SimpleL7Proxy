#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/run-regression-master.sh"
report_path=$(mktemp "${TMPDIR:-/tmp}/simplel7proxy-policy-stress-report.XXXXXX")

cleanup() {
    rm -f -- "$report_path"
}
trap cleanup EXIT

touch "$report_path"

regression_initialize "APIM policy stress"
regression_prepare_execution "APIM policy stress"
filter="FullyQualifiedName~PolicyScenarioIntegrationTests.V31Policy_SustainsOneThousandRequestorsForThirtyMinutes"
declare -a test_command
regression_build_command test_command "$filter"
command_text=$(regression_format_command "${test_command[@]}")
started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)

set +e
POLICY_STRESS_REPORT_PATH="$report_path" "${test_command[@]}" >"$REGRESSION_CONSOLE_LOG" 2>&1 &
test_pid=$!

tail -n +1 -f "$report_path" &
tail_pid=$!

wait "$test_pid"
test_status=$?
kill "$tail_pid" 2>/dev/null
wait "$tail_pid" 2>/dev/null
set -e

{
    printf '\nFinal stress report\n\n'
    cat "$report_path"
} | tee -a "$REGRESSION_CONSOLE_LOG"

completed_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
regression_finalize_execution "$test_status" "$command_text" "$started_utc" "$completed_utc"

exit "$test_status"