#!/usr/bin/env bash

# WHAT THIS TESTS
#   Inbound admission security and profile status:
#     - missing and invalid inbound keys return HTTP 403
#     - either configured key is accepted
#     - App ID allowlisting rejects unknown applications
#     - unknown profiles are rejected
#     - suspended users are rejected before reaching the backend
#     - rejected requests never reach the nullserver
#
# HOW TO TEST
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 9
#   Terminal 2: source test/ManualTests/test9_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test9_setup.sh verify
#
# WHAT TO EXPECT
#   Missing/bad keys, invalid App IDs, unknown profiles, and suspended users
#   return 403 with zero backend calls. key-one and key-two both authorize an
#   active user with allowed-app.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=direct;path=/;processor=DefaultStream"
export LoadBalanceMode=roundrobin
export IterationMode=SinglePass
export MaxAttempts=1
export UseSharedIterators=false
export Workers=2
export PriorityWorkers="1:0"
export AsyncModeEnabled=false
export CBErrorThreshold=50

export UseProfiles=true
export UserConfigRequired=true
export UserConfigUrl="http://localhost:3000/file/test9_profiles.json"
export SuspendedUserConfigUrl="http://localhost:3000/file/test9_suspended.json"
export UserIDFieldName=userId
export UserProfileHeader=X-UserProfile
export UserConfigRefreshIntervalSecs=3600
export UniqueUserHeaders=X-Profile-Marker

export ValidateAuthConfig="enabled=true;mode=key;header=X-Test-Key"
export ValidateAuthKey1=key-one
export ValidateAuthKey2=key-two
export ValidateAuthAppID=true
export ValidateAuthAppIDUrl="http://localhost:3000/file/test9_auth_appids.json"
export ValidateAuthAppIDHeader=X-Test-App-ID
export ValidateAuthAppFieldName=authAppID
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

	test_fail() {
		echo "FAIL: $1" >&2
		[[ -s "$TEST_BODY" ]] && { echo "Response body:" >&2; cat "$TEST_BODY" >&2; }
		exit 1
	}

	test_assert_eq() {
		[[ "$2" == "$1" ]] || test_fail "$3: expected '$1', got '$2'"
	}

	test_assert_contains() {
		grep -Fqi "$1" "$TEST_BODY" || test_fail "$2: missing '$1'"
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

	assert_rejected_without_backend() {
		local path="$1"
		local expected_text="$2"
		shift 2
		local before_count
		before_count="$(test_backend_count "$path")"
		test_request "$path" "$@"
		test_assert_eq 403 "$TEST_STATUS" "$path rejection status"
		test_assert_contains "$expected_text" "$path rejection response"
		test_assert_eq "$before_count" "$(test_backend_count "$path")" "$path backend count"
	}

	command -v jq >/dev/null 2>&1 || test_fail "jq is required"
	curl --fail --silent --show-error "$TEST_BACKEND_URL/test-control/reset" >/dev/null
	test_wait_ready

	suffix="${RANDOM}-${RANDOM}"
	common_headers=(-H "X-Test-App-ID: allowed-app" -H "X-UserProfile: active-user")

	assert_rejected_without_backend "/auth-missing-$suffix" "No auth provided" \
		"${common_headers[@]}"

	assert_rejected_without_backend "/auth-invalid-$suffix" "Invalid Auth Key" \
		-H "X-Test-Key: wrong-key" "${common_headers[@]}"

	assert_rejected_without_backend "/app-invalid-$suffix" "Invalid AuthAppID" \
		-H "X-Test-Key: key-one" -H "X-Test-App-ID: denied-app" \
		-H "X-UserProfile: active-user"

	assert_rejected_without_backend "/profile-unknown-$suffix" "unknown-user" \
		-H "X-Test-Key: key-one" -H "X-Test-App-ID: allowed-app" \
		-H "X-UserProfile: unknown-user"

	test_request "/success?auth=key-one-$suffix" \
		-H "X-Test-Key: key-one" "${common_headers[@]}"
	test_assert_eq 200 "$TEST_STATUS" "first key status"

	test_request "/success?auth=key-two-$suffix" \
		-H "X-Test-Key: key-two" "${common_headers[@]}"
	test_assert_eq 200 "$TEST_STATUS" "second key status"

	suspended_path="/suspended-$suffix"
	before_count="$(test_backend_count "$suspended_path")"
	test_request "$suspended_path" \
		-H "X-Test-Key: key-one" -H "X-Test-App-ID: allowed-app" \
		-H "X-UserProfile: suspended-user"
	test_assert_eq 403 "$TEST_STATUS" "suspended user status"
	test_assert_contains "suspend" "suspended user response"
	test_assert_eq "$before_count" "$(test_backend_count "$suspended_path")" "suspended user backend count"

	echo "PASS: inbound keys, App ID allowlist, profile admission, and suspension"
fi
