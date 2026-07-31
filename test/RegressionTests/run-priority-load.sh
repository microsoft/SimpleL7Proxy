#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/run-regression-master.sh"
report_path=$(mktemp "${TMPDIR:-/tmp}/simplel7proxy-priority-report.XXXXXX")

cleanup() {
    rm -f -- "$report_path"
}
trap cleanup EXIT

set +e
export PRIORITY_LOAD_REPORT_PATH="$report_path"
export REGRESSION_POST_TEST_OUTPUT_FILE="$report_path"
export REGRESSION_POST_TEST_OUTPUT_TITLE="Priority load statistics"
regression_run \
    "Priority load" \
    "FullyQualifiedName~PriorityLoadIntegrationTests.BuiltInPriorities_ProcessOneThousandConcurrentCurlRequests"
test_status=$?
unset PRIORITY_LOAD_REPORT_PATH
unset REGRESSION_POST_TEST_OUTPUT_FILE
unset REGRESSION_POST_TEST_OUTPUT_TITLE
set -e

if [[ ! -s "$report_path" && $test_status -eq 0 ]]; then
    echo "Priority load test passed but did not produce a statistics report." >&2
    exit 1
fi

exit "$test_status"