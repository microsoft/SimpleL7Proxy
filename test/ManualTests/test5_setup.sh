#!/usr/bin/env bash
# WHAT THIS TESTS
#   User-profile matching and the request validation pipeline:
#     - missing and unknown profiles return HTTP 403
#     - required headers and profile-backed allowlists return HTTP 417 on failure
#     - exact and wildcard allowlist matches reach the backend
#     - a profile rule adds x-Request-Sequence before the backend call
#     - StripRequestHeaders prevents x-S7PID from reaching the backend
#     - StripResponseHeaders removes x-Random-Header from the backend response
#
# HOW TO TEST
#   Use fresh terminals so environment variables from another setup do not
#   affect this scenario. Run these commands from the repository root.
#
#   Terminal 1: cd test/nullserver/Python && python3 stream_server.py --port 3000 -d
#               The nullserver serves test5_profiles.json from this directory.
#   Terminal 2: source test/ManualTests/test5_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test5_setup.sh verify
#
# WHAT TO EXPECT
#   The verifier rejects missing/unknown profiles and invalid headers, accepts
#   exact and wildcard model values, observes x-Request-Sequence=profile-rule-added,
#   observes x-S7PID=N/A because the request value was stripped, and confirms
#   x-Random-Header is absent while the unstripped Random-Header remains.

unset Host_api_A Host_api_B Host_api2_A Host_api2_B Host1 Host2 Host3
unset Path_api Path_api2 Path_api_special Path_inherit Path_keep
unset UseProfiles UserConfigRequired UserConfigUrl UserIDFieldName UserProfileHeader
unset UserConfigRefreshIntervalSecs UserSoftDeleteTTLMinutes UniqueUserHeaders
unset RequiredHeaders ValidateHeaders DisallowedHeaders StripRequestHeaders StripResponseHeaders
unset ValidateAuthAppID ValidateAuthAppIDUrl ValidateAuthConfig ValidateAuthKey1 ValidateAuthKey2
unset IterationMode MaxAttempts UseSharedIterators

export Host1="host=http://localhost:3000;mode=direct;path=/;processor=DefaultStream"
export LoadBalanceMode=prioritygroup
export IterationMode=SinglePass
export MaxAttempts=1
export UseSharedIterators=false
export CBErrorThreshold=50

export UseProfiles=true
export UserConfigRequired=true
export UserConfigUrl="http://localhost:3000/file/test5_profiles.json"
export UserIDFieldName=userId
export UserProfileHeader=X-UserProfile
export UserConfigRefreshIntervalSecs=3600
export UserSoftDeleteTTLMinutes=360
export UniqueUserHeaders=X-Profile-Marker

export RequiredHeaders=X-Correlation-ID
export ValidateHeaders="X-Requested-Model=X-Allowed-Models"
export DisallowedHeaders=
export StripRequestHeaders="X-Allowed-Models,x-S7PID"
export StripResponseHeaders=x-Random-Header

export ValidateAuthAppID=false
export ValidateAuthAppIDUrl=
export ValidateAuthConfig="enabled=false,mode=none,header=S7P-KEY"
export ValidateAuthKey1=
export ValidateAuthKey2=
export AsyncModeEnabled=false
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

	test_assert_header_absent() {
		[[ -z "$(test_header "$1")" ]] || test_fail "$2: unexpected header '$1'"
	}

	test_wait_ready() {
		local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
		local status
		while ((SECONDS < deadline)); do
			status="$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
				"$TEST_PROXY_URL/startup" || true)"
			[[ "$status" == 200 ]] && return
			sleep 1
		done
		test_fail "proxy did not become ready within ${TEST_TIMEOUT_SECONDS}s"
	}

	test_wait_ready

	# A required profile header is enforced before ordinary required-header checks.
	test_request "/profile-rule" \
		-H "X-Correlation-ID: test5-missing-profile" \
		-H "X-Requested-Model: gpt-4o"
	test_assert_eq 403 "$TEST_STATUS" "missing profile status"
	test_assert_contains "User profile not found" "missing profile response"

	test_request "/profile-rule" \
		-H "X-UserProfile: unknown-profile" \
		-H "X-Correlation-ID: test5-unknown-profile" \
		-H "X-Requested-Model: gpt-4o"
	test_assert_eq 403 "$TEST_STATUS" "unknown profile status"
	test_assert_contains "unknown-profile" "unknown profile response"

	# Explicit RequiredHeaders still applies after profile enrichment.
	test_request "/profile-rule" \
		-H "X-UserProfile: profile-a" \
		-H "X-Requested-Model: gpt-4o"
	test_assert_eq 417 "$TEST_STATUS" "missing required header status"
	test_assert_contains "X-Correlation-ID" "missing required header response"

	# ValidateHeaders auto-adds its source header to RequiredHeaders.
	test_request "/profile-rule" \
		-H "X-UserProfile: profile-a" \
		-H "X-Correlation-ID: test5-missing-model"
	test_assert_eq 417 "$TEST_STATUS" "missing validation source status"
	test_assert_contains "X-Requested-Model" "missing validation source response"

	# The profile allowlist accepts an exact match. A client-supplied allowlist is
	# removed before profile enrichment, so it cannot replace the stored policy.
	test_request "/profile-rule" \
		-H "X-UserProfile: profile-a" \
		-H "X-Correlation-ID: test5-exact" \
		-H "X-Requested-Model: gpt-4o" \
		-H "X-Allowed-Models: attacker-only" \
		-H "x-S7PID: client-secret"
	test_assert_eq 200 "$TEST_STATUS" "exact allowlist status"
	test_assert_eq "http://localhost:3000" "$(test_header BackendHost)" "profile backend"
	test_assert_eq "profile-rule-added" "$(test_header x-Request-Sequence)" "profile rule header"
	test_assert_eq "N/A" "$(test_header x-S7PID)" "stripped request header reflection"
	test_assert_eq "Random-Value" "$(test_header Random-Header)" "unstripped response header"
	test_assert_header_absent x-Random-Header "stripped response header"

	# Trailing '*' in the profile allowlist enables case-insensitive prefix matching.
	test_request "/profile-rule" \
		-H "X-UserProfile: profile-a" \
		-H "X-Correlation-ID: test5-wildcard" \
		-H "X-Requested-Model: GPT-4.1"
	test_assert_eq 200 "$TEST_STATUS" "wildcard allowlist status"
	test_assert_eq "profile-rule-added" "$(test_header x-Request-Sequence)" "wildcard profile rule header"

	# A different profile has a narrower allowlist and must reject the same value.
	test_request "/profile-rule" \
		-H "X-UserProfile: profile-b" \
		-H "X-Correlation-ID: test5-profile-b" \
		-H "X-Requested-Model: gpt-4o"
	test_assert_eq 417 "$TEST_STATUS" "profile-specific allowlist status"
	test_assert_contains "Validation check failed" "profile-specific allowlist response"

	test_request "/profile-rule" \
		-H "X-UserProfile: profile-a" \
		-H "X-Correlation-ID: test5-invalid-model" \
		-H "X-Requested-Model: gpt-3.5"
	test_assert_eq 417 "$TEST_STATUS" "invalid allowlist status"
	test_assert_contains "Validation check failed" "invalid allowlist response"

	echo "PASS: profile matching, header validation, header exclusion, and profile rule enrichment"
fi