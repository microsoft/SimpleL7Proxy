#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
nullserver_dir="$script_dir/../nullserver/Python"

usage() {
    echo "Usage: $0 <test-number> [--check]" >&2
    echo "Example: $0 4" >&2
}

if [[ $# -lt 1 || $# -gt 2 ]]; then
    usage
    exit 2
fi

test_number="${1#test}"
if [[ ! "$test_number" =~ ^([1-9]|10|11)$ ]]; then
    usage
    exit 2
fi

check_only=false
if [[ $# -eq 2 ]]; then
    if [[ "$2" != "--check" ]]; then
        usage
        exit 2
    fi
    check_only=true
fi

test_script="$script_dir/test${test_number}_setup.sh"
if [[ ! -f "$test_script" ]]; then
    echo "ERROR: Test setup script was not found: $test_script" >&2
    exit 1
fi

mapfile -t ports < <(
    sed -nE \
        's/^[[:space:]]*export[[:space:]]+Host[^=]*=.*host=http:\/\/(localhost|127\.0\.0\.1):([0-9]+).*/\2/p' \
        "$test_script" | awk '!seen[$0]++'
)
if [[ ${#ports[@]} -eq 0 ]]; then
    echo "ERROR: Test $test_number does not declare a local nullserver in a Host setting." >&2
    exit 1
fi

baseline_env=(
    "RATE_LIMIT_REQUESTS_PER_5_SECONDS=0"
    "RATE_LIMIT_TOKENS_PER_MINUTE=0"
    "NULL_SERVER_FAIL_FIRST_N=0"
    "NULL_SERVER_FAIL_FIRST_STATUS=429"
    "NULL_SERVER_DELAY_MS=0"
    "NULL_SERVER_QUIET=false"
)

for port in "${ports[@]}"; do
    if [[ ! "$port" =~ ^[0-9]+$ || "$port" -lt 1 || "$port" -gt 65535 ]]; then
        echo "ERROR: Invalid nullserver port in test $test_number: '$port'" >&2
        exit 1
    fi
done

if [[ "$check_only" == true ]]; then
    echo "Test $test_number nullserver configuration is valid:"
    for port in "${ports[@]}"; do
        printf '  port %s\n' "$port"
    done
    exit 0
fi

command -v python3 >/dev/null 2>&1 || {
    echo "ERROR: python3 is required." >&2
    exit 1
}
command -v curl >/dev/null 2>&1 || {
    echo "ERROR: curl is required." >&2
    exit 1
}

pids=()

stop_servers() {
    if [[ ${#pids[@]} -eq 0 ]]; then
        return
    fi

    trap - EXIT INT TERM
    kill "${pids[@]}" >/dev/null 2>&1 || true
    wait "${pids[@]}" >/dev/null 2>&1 || true
    echo "Stopped nullservers for test $test_number."
}

trap stop_servers EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

for index in "${!ports[@]}"; do
    (
        cd "$nullserver_dir"
        exec env "${baseline_env[@]}" \
            python3 stream_server.py --port "${ports[$index]}" --debug
    ) &
    pids+=("$!")
done

for index in "${!ports[@]}"; do
    deadline=$((SECONDS + 10))
    status=""
    while ((SECONDS < deadline)); do
        if ! kill -0 "${pids[$index]}" >/dev/null 2>&1; then
            echo "ERROR: Nullserver on port ${ports[$index]} stopped during startup." >&2
            exit 1
        fi

        status="$(curl --silent --output /dev/null --max-time 1 --write-out '%{http_code}' \
            "http://localhost:${ports[$index]}/health" || true)"
        [[ "$status" == 200 ]] && break
    done

    if [[ "$status" != 200 ]]; then
        echo "ERROR: Nullserver on port ${ports[$index]} did not become healthy." >&2
        exit 1
    fi
done

echo "Nullservers for test $test_number are ready on ports: ${ports[*]}"
echo "Keep this terminal open. Press Ctrl+C to stop all nullservers."

set +e
wait -n "${pids[@]}"
status=$?
set -e

echo "ERROR: A nullserver stopped unexpectedly (exit $status)." >&2
exit 1