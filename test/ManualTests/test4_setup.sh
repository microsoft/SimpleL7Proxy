#!/bin/bash

# WHAT THIS TESTS
#   Named Path_* routing, prefix stripping, declared host order, route-level
#   IterationMode, route-level MaxAttempts, longest-prefix matching, and
#   request-header precedence:
#     /api  -> Host1, Host2, Host3; route SinglePass
#     /api2 -> Host2, Host1; route MultiPass with MaxAttempts=4
#
# HOW TO TEST
#   Use fresh terminals so environment variables from another test setup do not
#   affect this scenario. Run these commands from the repository root.
#
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 4
#   Terminal 2: source test/ManualTests/test4_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test4_setup.sh verify
#
# WHAT TO EXPECT
#   /api/success returns HTTP 200 from port 3000;
#   /api2/success returns HTTP 200 from port 3001. Both are forwarded as /success after prefix stripping.
#   /api/500error returns HTTP 500 after ports 3000, 3001, 3002 (3 attempts).
#   /api2/500error returns HTTP 412 after ports 3001, 3000, 3001, 3000
#       (4 attempts).
#   /api/500error with S7P-Iterator: MultiPass overrides the route mode and
#       returns HTTP 412 after 10 attempts. A valid SinglePass header overrides
#       /api2 MultiPass; an invalid header falls back to the route mode.
#   /api/special uses the longest prefix and never falls through to /api when its
#       own host is ineligible. /keep preserves its prefix. /success is unmatched,
#       returns HTTP 503 with Attempts: 0, and does not strand the worker.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=direct;prioritygroup=1;acceptablepriorities=1:2:3:4"
export Host2="host=http://localhost:3001;mode=direct;prioritygroup=2;acceptablepriorities=1:2:3:4"
export Host3="host=http://localhost:3002;mode=direct;prioritygroup=3;acceptablepriorities=1:2:3"

export Path_api="prefix=/api;hosts=Host1:Host2:Host3;stripprefix=true;iterationmode=SinglePass;maxattempts=10"
export Path_api2="prefix=/api2;hosts=Host2:Host1;stripprefix=true;iterationmode=MultiPass;maxattempts=4"
export Path_api_special="prefix=/api/special;hosts=Host3;stripprefix=true;iterationmode=SinglePass"
export Path_inherit="prefix=/inherit;hosts=Host3:Host1;stripprefix=true"
export Path_keep="prefix=/keep;hosts=Host1;stripprefix=false"

export LoadBalanceMode=prioritygroup
export IterationMode=SinglePass
export MaxAttempts=7
export UseSharedIterators=true
export PriorityKeyHeader=S7PPriorityKey
export PriorityKeys=high,medium,low,none
export PriorityValues=1,2,3,4
export DefaultPriority=1
export CBErrorThreshold=50
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

export Workers=100

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
	if [[ "${1:-}" != "verify" ]]; then
		echo "Source this script before starting the proxy, then run: $0 verify" >&2
		exit 2
	fi

	set -euo pipefail
	TEST_PROXY_URL="${TEST_PROXY_URL:-http://localhost:8000}"
	TEST_BACKEND_1_URL="${TEST_BACKEND_1_URL:-http://localhost:3000}"
	TEST_TIMEOUT_SECONDS="${TEST_TIMEOUT_SECONDS:-20}"
	command -v jq >/dev/null 2>&1 || { echo "FAIL: jq is required" >&2; exit 1; }
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

	test_attempt_hosts() {
		sed -n 's/.*"Backend-Host": "\([^"]*\)".*/\1/p' "$TEST_BODY" | paste -sd, -
	}

	test_backend_count() {
		curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" "$1/stress-stats" |
			jq -r --arg path "$2" '.[$path] // 0'
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

	test_request "/success"
	test_assert_eq 503 "$TEST_STATUS" "unmatched route status"
	test_assert_eq 0 "$(test_header Attempts)" "unmatched route attempts"

	test_request "/api/success"
	test_assert_eq 200 "$TEST_STATUS" "/api success after unmatched request"
	test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "/api selected backend"
	grep -Fq "Congrats! You did it!" "$TEST_BODY" || test_fail "/api prefix was not stripped"

	test_request "/api2/success"
	test_assert_eq 200 "$TEST_STATUS" "/api2 success status"
	test_assert_eq "http://localhost:3001" "$(test_header BackendHost)" "/api2 selected backend"

	test_request "/api/special/success"
	test_assert_eq 200 "$TEST_STATUS" "longest-prefix status"
	test_assert_eq "http://localhost:3002" "$(test_header BackendHost)" "longest-prefix backend"
	grep -Fq "Congrats! You did it!" "$TEST_BODY" || test_fail "longest route prefix was not stripped"

	test_request "/api/special/success" -H "S7PPriorityKey: none"
	test_assert_eq 503 "$TEST_STATUS" "matched-route no-fallback status"
	test_assert_eq 0 "$(test_header Attempts)" "matched-route no-fallback attempts"

	test_request "/api/500error"
	test_assert_eq 500 "$TEST_STATUS" "route SinglePass status"
	test_assert_eq 3 "$(test_header Attempts)" "route SinglePass attempts"
	test_assert_eq \
		"http://localhost:3000,http://localhost:3001,http://localhost:3002" \
		"$(test_attempt_hosts)" \
		"route SinglePass order"

	test_request "/api2/500error"
	test_assert_eq 412 "$TEST_STATUS" "route MultiPass status"
	test_assert_eq 4 "$(test_header Attempts)" "route MaxAttempts override"
	test_assert_contains "Maximum backend attempts reached (4)." "route MaxAttempts response"
	test_assert_eq \
		"http://localhost:3001,http://localhost:3000,http://localhost:3001,http://localhost:3000" \
		"$(test_attempt_hosts)" \
		"route MultiPass order"

	test_request "/api2/500error" -H "S7P-Iterator: SinglePass"
	test_assert_eq 500 "$TEST_STATUS" "header SinglePass override status"
	test_assert_eq 2 "$(test_header Attempts)" "header SinglePass override attempts"

	test_request "/api2/500error" -H "S7P-Iterator: invalid"
	test_assert_eq 412 "$TEST_STATUS" "invalid header route fallback status"
	test_assert_eq 4 "$(test_header Attempts)" "invalid header route fallback attempts"

	test_request "/api/500error" -H "S7P-Iterator: MultiPass"
	test_assert_eq 412 "$TEST_STATUS" "header MultiPass override status"
	test_assert_eq 10 "$(test_header Attempts)" "route MaxAttempts over global MaxAttempts"
	test_assert_contains "Maximum backend attempts reached (10)." "header MultiPass response"

	test_request "/inherit/500error"
	test_assert_eq 500 "$TEST_STATUS" "inherited global mode status"
	test_assert_eq 2 "$(test_header Attempts)" "inherited global mode attempts"
	test_assert_eq \
		"http://localhost:3002,http://localhost:3000" \
		"$(test_attempt_hosts)" \
		"inherited route order"

	kept_path="/keep/verify-keep-${RANDOM}-${RANDOM}"
	before_count="$(test_backend_count "$TEST_BACKEND_1_URL" "$kept_path")"
	test_request "$kept_path"
	test_assert_eq 200 "$TEST_STATUS" "preserved-prefix status"
	after_count="$(test_backend_count "$TEST_BACKEND_1_URL" "$kept_path")"
	test_assert_eq "$((before_count + 1))" "$after_count" "preserved backend path count"

	test_request "/apix/success"
	test_assert_eq 503 "$TEST_STATUS" "named-route segment-boundary status"
	test_assert_eq 0 "$(test_header Attempts)" "named-route segment-boundary attempts"

	echo "PASS: named-route order, modes, limits, precedence, matching, and stripping"
fi
