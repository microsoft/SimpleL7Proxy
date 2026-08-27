#!/bin/bash

# WHAT THIS TESTS
#   MultiPass retry behavior and the global MaxAttempts limit. Failed requests must
#   cycle through Host1 -> Host2 and begin again at Host1 until five attempts occur.
#
# HOW TO TEST
#   Use fresh terminals so environment variables from another test setup do not
#   affect this scenario. Run these commands from the repository root.
#
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 3
#   Terminal 2: source test/ManualTests/test3_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test3_setup.sh verify
#
# WHAT TO EXPECT
#   The response is HTTP 412 with Attempts: 5 and contains
#   "Maximum backend attempts reached (5)." The attempt order is port 3000,
#   port 3001, port 3000, port 3001, port 3000. A request that repeatedly opens
#   both host circuits also stops at five lifetime attempts across requeue cycles,
#   reports the accumulated requeue delay, and makes no sixth backend call.
source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=direct;prioritygroup=1;acceptablepriorities=1:2:3;retryafter=true"
export Host2="host=http://localhost:3001;mode=direct;prioritygroup=2;acceptablepriorities=1:2:3;retryafter=true"
export LoadBalanceMode=prioritygroup
export IterationMode=MultiPass
export MaxAttempts=5
export UseSharedIterators=false
export CBErrorThreshold=50
export CBTimeslice=60
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
	if [[ "${1:-}" != "verify" ]]; then
		echo "Source this script before starting the proxy, then run: $0 verify" >&2
		exit 2
	fi

	set -euo pipefail
	TEST_PROXY_URL="${TEST_PROXY_URL:-http://localhost:8000}"
	TEST_BACKEND_1_URL="${TEST_BACKEND_1_URL:-http://localhost:3000}"
	TEST_BACKEND_2_URL="${TEST_BACKEND_2_URL:-http://localhost:3001}"
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

	test_request "/500error"
	test_assert_eq 412 "$TEST_STATUS" "single-cycle max-attempt status"
	test_assert_eq 5 "$(test_header Attempts)" "single-cycle attempts"
	test_assert_eq 5 "$(test_header Lifetime-Attempts)" "single-cycle lifetime attempts"
	test_assert_contains "Maximum backend attempts reached (5)." "single-cycle response"
	test_assert_eq \
		"http://localhost:3000,http://localhost:3001,http://localhost:3000,http://localhost:3001,http://localhost:3000" \
		"$(test_attempt_hosts)" \
		"single-cycle attempt order"

	retry_path="/retry-after-once"
	before_1="$(test_backend_count "$TEST_BACKEND_1_URL" "$retry_path")"
	before_2="$(test_backend_count "$TEST_BACKEND_2_URL" "$retry_path")"
	request_key="lifetime-${RANDOM}-${RANDOM}"
	test_request "$retry_path?key=$request_key&retryAfterMs=25&failures=3"
	test_assert_eq 412 "$TEST_STATUS" "lifetime max-attempt status"
	test_assert_eq 1 "$(test_header Attempts)" "final-cycle attempts"
	test_assert_eq 5 "$(test_header Lifetime-Attempts)" "lifetime max-attempt count"
	test_assert_contains "Maximum backend attempts reached (5)." "lifetime max-attempt response"

	requeue_delay="$(test_header Request-Requeue-Delay)"
	[[ -n "$requeue_delay" ]] || test_fail "missing cumulative Request-Requeue-Delay"
	awk -v value="$requeue_delay" 'BEGIN { exit !(value + 0 >= 400) }' ||
		test_fail "cumulative requeue delay was only ${requeue_delay}ms"

	after_1="$(test_backend_count "$TEST_BACKEND_1_URL" "$retry_path")"
	after_2="$(test_backend_count "$TEST_BACKEND_2_URL" "$retry_path")"
	total_backend_calls=$((after_1 - before_1 + after_2 - before_2))
	test_assert_eq 5 "$total_backend_calls" "lifetime backend call count"

	echo "PASS: MultiPass order, lifetime MaxAttempts, and cumulative requeue delay"
fi
