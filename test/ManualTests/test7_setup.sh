#!/usr/bin/env bash

# WHAT THIS TESTS
#   Deterministic priority dispatch and queue admission:
#     - one held backend request occupies the only general worker
#     - low, medium, and high requests are confirmed queued before release
#     - the full queue rejects one additional request with HTTP 429
#     - after release, queued requests reach the backend high, medium, low
#
# HOW TO TEST
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 7
#   Terminal 2: source test/ManualTests/test7_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test7_setup.sh verify
#
# WHAT TO EXPECT
#   The nullserver barrier removes timing assumptions. The verifier waits for
#   queue depths 1, 2, and 3, observes a 429 overflow, releases the worker, and
#   confirms backend arrival order blocker, high, medium, low.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=direct;path=/;processor=DefaultStream"
export LoadBalanceMode=roundrobin
export IterationMode=SinglePass
export MaxAttempts=1
export UseSharedIterators=false
export Workers=1
export PriorityWorkers="1:0"
export MaxQueueLength=3
export PriorityKeyHeader=S7PPriorityKey
export PriorityKeys=high,medium,low
export PriorityValues=1,2,3
export DefaultPriority=2
export DefaultTTLSecs=60
export Timeout=60000
export AsyncModeEnabled=false
export UseProfiles=false
export UserConfigRequired=false
export CBErrorThreshold=50
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    if [[ "${1:-}" != "verify" ]]; then
        echo "Source this script before starting the proxy, then run: $0 verify" >&2
        exit 2
    fi

    set -euo pipefail
    TEST_PROXY_URL="${TEST_PROXY_URL:-http://localhost:8000}"
    TEST_BACKEND_URL="${TEST_BACKEND_URL:-http://localhost:3000}"
    TEST_TIMEOUT_SECONDS="${TEST_TIMEOUT_SECONDS:-20}"
    TEST_TMP="$(mktemp -d)"
    TEST_HEADERS="$TEST_TMP/headers"
    TEST_BODY="$TEST_TMP/body"
    request_pids=()

    test_cleanup() {
        curl --silent --max-time 2 "$TEST_BACKEND_URL/test-control/release" >/dev/null 2>&1 || true
        for pid in "${request_pids[@]}"; do
            kill "$pid" >/dev/null 2>&1 || true
        done
        rm -rf "$TEST_TMP"
    }
    trap test_cleanup EXIT

    test_fail() {
        echo "FAIL: $1" >&2
        [[ -s "$TEST_BODY" ]] && { echo "Response body:" >&2; cat "$TEST_BODY" >&2; }
        exit 1
    }

    test_assert_eq() {
        [[ "$2" == "$1" ]] || test_fail "$3: expected '$1', got '$2'"
    }

    test_wait_ready() {
        local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
        while ((SECONDS < deadline)); do
            [[ "$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
                "$TEST_PROXY_URL/startup" || true)" == 200 ]] && return
        done
        test_fail "proxy did not become ready within ${TEST_TIMEOUT_SECONDS}s"
    }

    test_wait_arrival() {
        local sequence="$1"
        local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
        while ((SECONDS < deadline)); do
            if curl --silent --show-error --max-time 2 "$TEST_BACKEND_URL/test-control/state" |
                jq -e --arg sequence "$sequence" '.arrivals | any(.sequence == $sequence)' >/dev/null; then
                return
            fi
        done
        test_fail "backend did not observe sequence '$sequence'"
    }

    test_wait_queue_depth() {
        local expected="$1"
        local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
        local depth
        while ((SECONDS < deadline)); do
            depth="$(curl --silent --show-error --max-time 2 "$TEST_PROXY_URL/health" |
                sed -n 's/.*Request Queue  : \([0-9][0-9]*\).*/\1/p' | tail -n 1)"
            [[ "$depth" == "$expected" ]] && return
        done
        test_fail "request queue did not reach depth $expected; last depth was '${depth:-unknown}'"
    }

    launch_request() {
        local name="$1"
        local priority="$2"
        local path="$3"
        curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" \
            --output "$TEST_TMP/$name.body" --write-out '%{http_code}' \
            -H "S7PPriorityKey: $priority" \
            -H "x-Request-Sequence: $name" \
            "$TEST_PROXY_URL$path" > "$TEST_TMP/$name.status" &
        request_pids+=("$!")
    }

    command -v jq >/dev/null 2>&1 || test_fail "jq is required"
    curl --fail --silent --show-error "$TEST_BACKEND_URL/test-control/reset" >/dev/null
    test_wait_ready
    curl --fail --silent --show-error "$TEST_BACKEND_URL/test-control/hold" >/dev/null

    launch_request blocker high "/test-hold?timeout=30"
    test_wait_arrival blocker

    launch_request low low "/queue-low"
    test_wait_queue_depth 1
    launch_request medium medium "/queue-medium"
    test_wait_queue_depth 2
    launch_request high high "/queue-high"
    test_wait_queue_depth 3

    overflow_status="$(curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" \
        --dump-header "$TEST_HEADERS" --output "$TEST_BODY" --write-out '%{http_code}' \
        -H "S7PPriorityKey: high" -H "x-Request-Sequence: overflow" \
        "$TEST_PROXY_URL/queue-overflow")"
    test_assert_eq 429 "$overflow_status" "queue overflow status"
    grep -Fq "Queue is full" "$TEST_BODY" || test_fail "queue overflow response did not name the full queue"

    arrivals_before_release="$(curl --silent --show-error "$TEST_BACKEND_URL/test-control/state" |
        jq -r '.arrivals | map(.sequence) | join(",")')"
    test_assert_eq blocker "$arrivals_before_release" "arrivals before barrier release"

    curl --fail --silent --show-error "$TEST_BACKEND_URL/test-control/release" >/dev/null
    for pid in "${request_pids[@]}"; do
        wait "$pid"
    done
    request_pids=()

    for name in blocker low medium high; do
        test_assert_eq 200 "$(cat "$TEST_TMP/$name.status")" "$name response status"
    done

    test_wait_arrival low
    arrival_order="$(curl --silent --show-error "$TEST_BACKEND_URL/test-control/state" |
        jq -r '.arrivals | map(.sequence) | join(",")')"
    test_assert_eq "blocker,high,medium,low" "$arrival_order" "priority dispatch order"

    if curl --silent --show-error "$TEST_BACKEND_URL/test-control/state" |
        jq -e '.arrivals | any(.sequence == "overflow")' >/dev/null; then
        test_fail "queue-overflow request reached the backend"
    fi

    echo "PASS: deterministic priority dispatch and queue-full admission"
fi
