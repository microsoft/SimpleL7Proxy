#!/usr/bin/env bash

# WHAT THIS TESTS
#   External .NET health sidecar integration and probe failure thresholds:
#     - proxy status updates make sidecar liveness/readiness/startup healthy
#     - backend health degradation is pushed to readiness/startup while the
#       live proxy keeps liveness healthy
#     - configurable consecutive-failure counts emulate ACA/Kubernetes
#       failureThreshold behavior
#     - backend recovery is pushed back to the sidecar
#     - verify-stale checks sidecar failure after proxy updates stop
#
# HOW TO TEST
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 11
#   Terminal 2: HEALTHPROBE_PORT=9000 dotnet run --project src/HealthProbe/HealthProbe.csproj --no-launch-profile
#   Terminal 3: source test/ManualTests/test11_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 4: ./test/ManualTests/test11_setup.sh verify
#
#   Optional stale-update phase:
#     1. Stop only the proxy; leave the sidecar running.
#     2. Run: ./test/ManualTests/test11_setup.sh verify-stale
#
# WHAT TO EXPECT
#   verify checks healthy, degraded, failureThreshold, and recovered states.
#   verify-stale waits for the .NET sidecar's 20-second stale-update deadline,
#   then requires consecutive failures from liveness/readiness/startup.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

export Host1="host=http://localhost:3000;mode=apim;path=/;probe=/health;processor=DefaultStream"
export LoadBalanceMode=roundrobin
export IterationMode=SinglePass
export MaxAttempts=1
export UseSharedIterators=false
export Workers=2
export PriorityWorkers="1:0"
export PollInterval=250
export PollTimeout=500
export SuccessRate=100
export HealthProbeSidecar="Enabled=true;url=http://localhost:9000"
export AsyncModeEnabled=false
export UseProfiles=false
export UserConfigRequired=false
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
	mode="${1:-}"
	if [[ "$mode" != "verify" && "$mode" != "verify-stale" ]]; then
		echo "Source this script before starting the proxy, then run: $0 verify" >&2
		echo "After stopping only the proxy, optionally run: $0 verify-stale" >&2
		exit 2
	fi

	set -euo pipefail
	TEST_SIDECAR_URL="${TEST_SIDECAR_URL:-http://localhost:9000}"
	TEST_BACKEND_URL="${TEST_BACKEND_URL:-http://localhost:3000}"
	TEST_TIMEOUT_SECONDS="${TEST_TIMEOUT_SECONDS:-35}"
	TEST11_READINESS_FAILURE_THRESHOLD="${TEST11_READINESS_FAILURE_THRESHOLD:-3}"
	TEST11_STARTUP_FAILURE_THRESHOLD="${TEST11_STARTUP_FAILURE_THRESHOLD:-30}"
	TEST11_LIVENESS_FAILURE_THRESHOLD="${TEST11_LIVENESS_FAILURE_THRESHOLD:-3}"

	test_fail() {
		echo "FAIL: $1" >&2
		exit 1
	}

	probe_code() {
		curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
			"$TEST_SIDECAR_URL$1" || true
	}

	wait_probe_code() {
		local path="$1"
		local expected="$2"
		local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
		local status=""
		while ((SECONDS < deadline)); do
			status="$(probe_code "$path")"
			[[ "$status" == "$expected" ]] && return
		done
		test_fail "$path did not reach HTTP $expected; last status was '${status:-none}'"
	}

	assert_consecutive_probe_results() {
		local path="$1"
		local expected="$2"
		local count="$3"
		local index status
		for ((index = 1; index <= count; index++)); do
			status="$(probe_code "$path")"
			[[ "$status" == "$expected" ]] ||
				test_fail "$path failureThreshold sample $index/$count expected $expected, got $status"
		done
	}

	if [[ "$mode" == "verify-stale" ]]; then
		wait_probe_code /liveness 503
		assert_consecutive_probe_results /liveness 503 "$TEST11_LIVENESS_FAILURE_THRESHOLD"
		assert_consecutive_probe_results /readiness 503 "$TEST11_READINESS_FAILURE_THRESHOLD"
		assert_consecutive_probe_results /startup 503 "$TEST11_STARTUP_FAILURE_THRESHOLD"
		echo "PASS: stale sidecar updates exceed configured consecutive probe failure thresholds"
		exit 0
	fi

	curl --fail --silent --show-error "$TEST_BACKEND_URL/test-control/reset" >/dev/null
	wait_probe_code /liveness 200
	wait_probe_code /readiness 200
	wait_probe_code /startup 200

	curl --fail --silent --show-error \
		"$TEST_BACKEND_URL/test-control/health?status=503" >/dev/null
	wait_probe_code /readiness 503
	wait_probe_code /startup 503
	assert_consecutive_probe_results /readiness 503 "$TEST11_READINESS_FAILURE_THRESHOLD"
	assert_consecutive_probe_results /startup 503 "$TEST11_STARTUP_FAILURE_THRESHOLD"
	assert_consecutive_probe_results /liveness 200 "$TEST11_LIVENESS_FAILURE_THRESHOLD"

	curl --fail --silent --show-error \
		"$TEST_BACKEND_URL/test-control/health?status=200" >/dev/null
	wait_probe_code /readiness 200
	wait_probe_code /startup 200
	assert_consecutive_probe_results /readiness 200 "$TEST11_READINESS_FAILURE_THRESHOLD"
	assert_consecutive_probe_results /liveness 200 "$TEST11_LIVENESS_FAILURE_THRESHOLD"

	echo "PASS: sidecar health propagation, failure thresholds, and recovery"
fi
