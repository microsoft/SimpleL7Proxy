#!/bin/bash

# WHAT THIS TESTS
#   Priority-group ordering and acceptable-priority filtering for catch-all
#   backends. Equal-group hosts must preserve their configured order.
#
# HOW TO TEST
#   Use fresh terminals so environment variables from another test setup do not
#   affect this scenario. Run these commands from the repository root.
#
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 1
#   Terminal 2: source test/ManualTests/test1_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test1_setup.sh verify
#
# WHAT TO EXPECT
#   high selects Host1, medium selects Host2, and low selects Host3. Priority 4
#   has no eligible host and returns HTTP 503 with Attempts: 0. A failing high-
#   priority request tries Host1, Host2, and Host3 in that exact order.
#
source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=direct;prioritygroup=1;acceptablepriorities=1"
export Host2="host=http://localhost:3001;mode=direct;prioritygroup=2;acceptablepriorities=1:2"
export Host3="host=http://localhost:3002;mode=direct;prioritygroup=2;acceptablepriorities=1:2:3"
export LoadBalanceMode=prioritygroup
export IterationMode=SinglePass
export MaxAttempts=10
export UseSharedIterators=false
export PriorityKeyHeader=S7PPriorityKey
export PriorityKeys=high,medium,low,none
export PriorityValues=1,2,3,4
export DefaultPriority=1
export CBErrorThreshold=50
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
	if [[ "${1:-}" != "verify" ]]; then
		echo "Source this script before starting the proxy, then run: $0 verify" >&2
		exit 2
	fi

	set -euo pipefail
	TEST_PROXY_URL="${TEST_PROXY_URL:-http://localhost:8000}"
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

	test_attempt_hosts() {
		sed -n 's/.*"Backend-Host": "\([^"]*\)".*/\1/p' "$TEST_BODY" | paste -sd, -
	}

	test_fail() {
		echo "FAIL: $1" >&2
		[[ -s "$TEST_BODY" ]] && { echo "Response body:" >&2; cat "$TEST_BODY" >&2; }
		exit 1
	}

	test_assert_eq() {
		[[ "$2" == "$1" ]] || test_fail "$3: expected '$1', got '$2'"
	}

	test_request "/success" -H "S7PPriorityKey: high"
	test_assert_eq 200 "$TEST_STATUS" "high-priority status"
	test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "high-priority backend"

	test_request "/success" -H "S7PPriorityKey: medium"
	test_assert_eq 200 "$TEST_STATUS" "medium-priority status"
	test_assert_eq "http://localhost:3001" "$(test_header BackendHost)" "medium-priority backend"

	test_request "/success" -H "S7PPriorityKey: low"
	test_assert_eq 200 "$TEST_STATUS" "low-priority status"
	test_assert_eq "http://localhost:3002" "$(test_header BackendHost)" "low-priority backend"

	test_request "/success" -H "S7PPriorityKey: none"
	test_assert_eq 503 "$TEST_STATUS" "no-eligible-priority status"
	test_assert_eq 0 "$(test_header Attempts)" "no-eligible-priority attempts"

	test_request "/500error" -H "S7PPriorityKey: high"
	test_assert_eq 500 "$TEST_STATUS" "priority-group failure status"
	test_assert_eq 3 "$(test_header Attempts)" "priority-group failure attempts"
	test_assert_eq \
		"http://localhost:3000,http://localhost:3001,http://localhost:3002" \
		"$(test_attempt_hosts)" \
		"priority-group attempt order"

	echo "PASS: priority-group ordering and acceptable-priority filtering"
fi
