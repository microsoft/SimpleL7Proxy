#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
report_path=$(mktemp "${TMPDIR:-/tmp}/simplel7proxy-priority-report.XXXXXX")

cleanup() {
    rm -f -- "$report_path"
}
trap cleanup EXIT

set +e
PRIORITY_LOAD_REPORT_PATH="$report_path" \
    dotnet test "$script_dir/SimpleL7Proxy.Test.csproj" -- \
    --filter "FullyQualifiedName~PriorityLoadIntegrationTests.BuiltInPriorities_ProcessOneThousandConcurrentCurlRequests"
test_status=$?
set -e

if [[ -s "$report_path" ]]; then
    printf '\nPriority load statistics\n\n'
    cat -- "$report_path"
elif [[ $test_status -eq 0 ]]; then
    echo "Priority load test passed but did not produce a statistics report." >&2
    exit 1
fi

exit "$test_status"