#!/usr/bin/env bash

# WHAT THIS TESTS
#   Request lifetime and response contracts:
#     - successful responses include the six standard proxy headers
#     - invalid TTL values return HTTP 400 without reaching the backend
#     - expired TTL values return HTTP 412 without reaching the backend
#     - decimal, absolute Unix, and ISO 8601 TTL values are accepted
#     - S7PTimeout bounds one backend attempt and returns HTTP 408
#     - a configured acceptable HTTP 500 is passed through unchanged
#     - direct-mode hosts are not health-probed
#
# HOW TO TEST
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 6
#   Terminal 2: source test/ManualTests/test6_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test6_setup.sh verify
#
# WHAT TO EXPECT
#   The verifier prints PASS after checking TTL parsing, timeout behavior,
#   response headers, acceptable-status pass-through, and direct-mode probing.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=direct;path=/;processor=DefaultStream"
export LoadBalanceMode=roundrobin
export IterationMode=SinglePass
export MaxAttempts=1
export UseSharedIterators=false
export Workers=2
export PriorityWorkers="1:0"
export DefaultTTLSecs=10
export Timeout=2000
export AcceptableStatusCodes="200,202,400,401,403,404,408,410,412,417,500"
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
    trap 'rm -rf "$TEST_TMP"' EXIT

    test_request() {
        local path="$1"
        shift
        TEST_STATUS="$(curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" \
            --dump-header "$TEST_HEADERS" --output "$TEST_BODY" --write-out '%{http_code}' \
            "$@" "$TEST_PROXY_URL$path")"
    }

    test_header() {
        awk -v target="$1" '
            {
                line = $0
                sub(/\r$/, "", line)
                colon = index(line, ":")
                if (colon > 0 && tolower(substr(line, 1, colon - 1)) == tolower(target)) {
                    value = substr(line, colon + 1)
                    sub(/^[[:space:]]+/, "", value)
                }
            }
            END { print value }
        ' "$TEST_HEADERS"
    }

    test_fail() {
        echo "FAIL: $1" >&2
        [[ -s "$TEST_BODY" ]] && { echo "Response body:" >&2; cat "$TEST_BODY" >&2; }
        exit 1
    }

    test_assert_eq() {
        [[ "$2" == "$1" ]] || test_fail "$3: expected '$1', got '$2'"
    }

    test_assert_contains() {
        grep -Fq "$1" "$TEST_BODY" || test_fail "$2: missing '$1'"
    }

    test_assert_header_present() {
        [[ -n "$(test_header "$1")" ]] || test_fail "$2: missing header '$1'"
    }

    test_backend_count() {
        curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" \
            "$TEST_BACKEND_URL/stress-stats" | jq -r --arg path "$1" '.[$path] // 0'
    }

    test_wait_ready() {
        local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
        while ((SECONDS < deadline)); do
            [[ "$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
                "$TEST_PROXY_URL/startup" || true)" == 200 ]] && return
        done
        test_fail "proxy did not become ready within ${TEST_TIMEOUT_SECONDS}s"
    }

    command -v jq >/dev/null 2>&1 || test_fail "jq is required"
    curl --fail --silent --show-error "$TEST_BACKEND_URL/test-control/reset" >/dev/null
    test_wait_ready

    test_request "/success"
    test_assert_eq 200 "$TEST_STATUS" "baseline status"
    test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "baseline backend"
    test_assert_eq 1 "$(test_header Attempts)" "baseline attempts"
    test_assert_eq 1 "$(test_header Lifetime-Attempts)" "baseline lifetime attempts"
    test_assert_header_present Request-Queue-Duration "baseline queue duration"
    test_assert_header_present Request-Process-Duration "baseline process duration"
    test_assert_header_present Total-Latency "baseline total latency"

    suffix="${RANDOM}-${RANDOM}"
    invalid_path="/ttl-invalid-$suffix"
    before_count="$(test_backend_count "$invalid_path")"
    test_request "$invalid_path" -H "S7PTTL: not-a-ttl"
    test_assert_eq 400 "$TEST_STATUS" "invalid TTL status"
    test_assert_contains "Invalid TTL format" "invalid TTL response"
    test_assert_eq "$before_count" "$(test_backend_count "$invalid_path")" "invalid TTL backend count"

    expired_path="/ttl-expired-$suffix"
    before_count="$(test_backend_count "$expired_path")"
    test_request "$expired_path" -H "S7PTTL: 0"
    test_assert_eq 412 "$TEST_STATUS" "expired TTL status"
    test_assert_contains "Request TTL expired" "expired TTL response"
    test_assert_eq "$before_count" "$(test_backend_count "$expired_path")" "expired TTL backend count"

    test_request "/success?ttl=decimal-$suffix" -H "S7PTTL: 1.5"
    test_assert_eq 200 "$TEST_STATUS" "decimal TTL status"

    unix_expiry="$(( $(date -u +%s) + 30 ))"
    test_request "/success?ttl=unix-$suffix" -H "S7PTTL: +$unix_expiry"
    test_assert_eq 200 "$TEST_STATUS" "absolute Unix TTL status"

    iso_expiry="$(date -u -d '+30 seconds' '+%Y-%m-%dT%H:%M:%SZ')"
    test_request "/success?ttl=iso-$suffix" -H "S7PTTL: $iso_expiry"
    test_assert_eq 200 "$TEST_STATUS" "ISO TTL status"

    test_request "/delay?delay=500ms&case=timeout-$suffix" -H "S7PTimeout: 100"
    test_assert_eq 408 "$TEST_STATUS" "per-attempt timeout status"
    test_assert_eq 1 "$(test_header Attempts)" "per-attempt timeout attempts"

    test_request "/500error?case=acceptable-$suffix"
    test_assert_eq 500 "$TEST_STATUS" "acceptable 500 status"
    test_assert_eq 1 "$(test_header Attempts)" "acceptable 500 attempts"
    test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "acceptable 500 backend"
    test_assert_contains "Error 500 occurred!" "acceptable 500 pass-through body"

    health_requests="$(curl --silent --show-error "$TEST_BACKEND_URL/test-control/state" | jq -r '.health_requests')"
    test_assert_eq 0 "$health_requests" "direct-mode backend probe count"

    echo "PASS: TTL formats, attempt timeout, response headers, acceptable status, and direct mode"
fi
