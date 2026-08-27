#!/usr/bin/env bash

# WHAT THIS TESTS
#   Probe-based health and latency routing with two backends:
#     - latency mode initially selects the faster probe target
#     - an unhealthy host leaves the active pool and traffic fails over
#     - SuccessRate=100 makes any post-startup failed probe remove a host
#       from the rolling 50-sample active pool, then traffic fails over
#     - when all hosts are unhealthy, readiness/startup return 503 while
#       liveness remains 200 and normal admission returns 429
#     - recovered probes return the host and proxy to service
#
#   This is separate from ACA/Kubernetes probe failureThreshold. That setting
#   controls how many failed /readiness or /liveness checks the orchestrator
#   tolerates; test 11 checks consecutive sidecar probe failures.
#
# HOW TO TEST
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 8
#   Terminal 2: source test/ManualTests/test8_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test8_setup.sh verify
#
# WHAT TO EXPECT
#   Port 3001 wins the initial latency comparison. It is then removed, traffic
#   moves to port 3000, both hosts are removed, and port 3001 recovers.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=apim;path=/;probe=/health?delay=60ms;processor=DefaultStream"
export Host2="host=http://localhost:3001;mode=apim;path=/;probe=/health?delay=5ms;processor=DefaultStream"
export LoadBalanceMode=latency
export IterationMode=SinglePass
export MaxAttempts=2
export UseSharedIterators=false
export Workers=2
export PriorityWorkers="1:0"
export PollInterval=250
export PollTimeout=200
export SuccessRate=100
export DefaultTTLSecs=30
export Timeout=5000
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
	TEST_BACKEND_1_URL="${TEST_BACKEND_1_URL:-http://localhost:3000}"
	TEST_BACKEND_2_URL="${TEST_BACKEND_2_URL:-http://localhost:3001}"
	TEST_TIMEOUT_SECONDS="${TEST_TIMEOUT_SECONDS:-30}"
	TEST_TMP="$(mktemp -d)"
	TEST_HEADERS="$TEST_TMP/headers"
	TEST_BODY="$TEST_TMP/body"
	trap 'rm -rf "$TEST_TMP"' EXIT

	test_request() {
		local path="$1"
		TEST_STATUS="$(curl --silent --show-error --max-time 5 \
			--dump-header "$TEST_HEADERS" --output "$TEST_BODY" --write-out '%{http_code}' \
			"$TEST_PROXY_URL$path" || true)"
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

	wait_proxy_code() {
		local path="$1"
		local expected="$2"
		local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
		local status=""
		while ((SECONDS < deadline)); do
			status="$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
				"$TEST_PROXY_URL$path" || true)"
			[[ "$status" == "$expected" ]] && return
		done
		test_fail "$path did not reach HTTP $expected; last status was '${status:-none}'"
	}

	wait_backend() {
		local expected="$1"
		local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
		local selected=""
		while ((SECONDS < deadline)); do
			test_request "/success?health-check=${RANDOM}"
			selected="$(test_header BackendHost)"
			[[ "$TEST_STATUS" == 200 && "$selected" == "$expected" ]] && return
		done
		test_fail "latency routing did not select $expected; last selection was '${selected:-none}'"
	}

	command -v jq >/dev/null 2>&1 || test_fail "jq is required"
	curl --fail --silent --show-error "$TEST_BACKEND_1_URL/test-control/reset" >/dev/null
	curl --fail --silent --show-error "$TEST_BACKEND_2_URL/test-control/reset" >/dev/null
	wait_proxy_code /startup 200

	wait_backend "http://localhost:3001"

	curl --fail --silent --show-error \
		"$TEST_BACKEND_2_URL/test-control/health?status=503" >/dev/null
	wait_backend "http://localhost:3000"

	curl --fail --silent --show-error \
		"$TEST_BACKEND_1_URL/test-control/health?status=503" >/dev/null
	wait_proxy_code /readiness 503
	wait_proxy_code /startup 503
	wait_proxy_code /liveness 200

	test_request "/success"
	test_assert_eq 429 "$TEST_STATUS" "zero-active-host admission status"
	grep -Fq "No active hosts" "$TEST_BODY" || test_fail "zero-active-host response did not name the cause"

	curl --fail --silent --show-error \
		"$TEST_BACKEND_2_URL/test-control/health?status=200" >/dev/null
	wait_proxy_code /readiness 200
	wait_backend "http://localhost:3001"

	curl --fail --silent --show-error \
		"$TEST_BACKEND_1_URL/test-control/health?status=200" >/dev/null

	host1_probes="$(curl --silent --show-error "$TEST_BACKEND_1_URL/test-control/state" | jq -r '.health_requests')"
	host2_probes="$(curl --silent --show-error "$TEST_BACKEND_2_URL/test-control/state" | jq -r '.health_requests')"
	((host1_probes > 0 && host2_probes > 0)) || test_fail "both probeable hosts must receive health probes"

	echo "PASS: probe latency, active-pool failover, health degradation, and recovery"
fi
