#!/bin/bash
# WHAT THIS TESTS
#   Legacy per-host path filtering, prefix stripping, and priority-group ordering:
#     /api  -> port 3000, then port 3001
#     /api2 -> port 3001, then port 3000
#
# HOW TO TEST
#   Use fresh terminals so environment variables from another test setup do not
#   affect this scenario. Run these commands from the repository root.
#
#   Terminal 1: cd test/nullserver/Python && python3 stream_server.py --port 3000 -d
#   Terminal 2: cd test/nullserver/Python && python3 stream_server.py --port 3001 -d
#   Terminal 3: cd src/SimpleL7Proxy && source ./test2_setup.sh && dotnet run --no-launch-profile
#   Terminal 4: ./src/SimpleL7Proxy/test2_setup.sh verify
#
# WHAT TO EXPECT
#   /api/success returns HTTP 200 from port 3000; /api2/success returns HTTP 200
#   from port 3001. The nullservers receive /success because the prefix is stripped.
#   The two /500error responses return HTTP 500 and list their two attempts in the
#   route orders above. /apix does not match /api. Query strings survive prefix
#   stripping. Unmatched paths return HTTP 503 with Attempts: 0.
unset Host_api_A Host_api_B Host_api2_A Host_api2_B Host1 Host2 Host3 Path_api Path_api2
unset IterationMode MaxAttempts UseSharedIterators

export Host_api_A="host=http://localhost:3000;mode=direct;path=api;prioritygroup=1"
export Host_api_B="host=http://localhost:3001;mode=direct;path=api;prioritygroup=2"
export Host_api2_A="host=http://localhost:3001;mode=direct;path=api2;prioritygroup=1"
export Host_api2_B="host=http://localhost:3000;mode=direct;path=api2;prioritygroup=2"

export LoadBalanceMode=prioritygroup
export IterationMode=SinglePass
export MaxAttempts=10
export UseSharedIterators=false
export CBErrorThreshold=50

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

	test_request "/api/success"
	test_assert_eq 200 "$TEST_STATUS" "/api success status"
	test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "/api selected backend"

	test_request "/api2/success"
	test_assert_eq 200 "$TEST_STATUS" "/api2 success status"
	test_assert_eq "http://localhost:3001" "$(test_header BackendHost)" "/api2 selected backend"

	test_request "/api/500error"
	test_assert_eq 500 "$TEST_STATUS" "/api failure status"
	test_assert_eq 2 "$(test_header Attempts)" "/api failure attempts"
	test_assert_eq \
		"http://localhost:3000,http://localhost:3001" \
		"$(test_attempt_hosts)" \
		"/api attempt order"

	test_request "/api2/500error"
	test_assert_eq 500 "$TEST_STATUS" "/api2 failure status"
	test_assert_eq 2 "$(test_header Attempts)" "/api2 failure attempts"
	test_assert_eq \
		"http://localhost:3001,http://localhost:3000" \
		"$(test_attempt_hosts)" \
		"/api2 attempt order"

	test_request "/apix/success"
	test_assert_eq 503 "$TEST_STATUS" "segment-boundary status"
	test_assert_eq 0 "$(test_header Attempts)" "segment-boundary attempts"

	test_request "/success"
	test_assert_eq 503 "$TEST_STATUS" "unmatched-path status"
	test_assert_eq 0 "$(test_header Attempts)" "unmatched-path attempts"

	stripped_path="/verify-strip-${RANDOM}-${RANDOM}"
	before_count="$(test_backend_count "$TEST_BACKEND_1_URL" "$stripped_path")"
	test_request "/api$stripped_path"
	test_assert_eq 200 "$TEST_STATUS" "stripped-path status"
	after_count="$(test_backend_count "$TEST_BACKEND_1_URL" "$stripped_path")"
	test_assert_eq "$((before_count + 1))" "$after_count" "stripped backend path count"

	request_key="legacy-query-${RANDOM}-${RANDOM}"
	test_request "/api/retry-after-once?key=$request_key&retryAfterMs=25&throttlePort=3001"
	test_assert_eq 200 "$TEST_STATUS" "query-preservation status"
	test_assert_eq 1 "$(test_header Attempts)" "query-preservation attempts"
	test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "query-preservation backend"

	echo "PASS: legacy path filtering, ordering, stripping, boundaries, and query preservation"
fi
