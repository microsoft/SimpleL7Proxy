#!/usr/bin/env bash

# WHAT THIS TESTS
#   Streaming response processors and telemetry:
#     - fixed and chunked OpenAI responses remain byte-for-byte intact
#     - TOKENPROCESSOR=AllUsage extracts 41 prompt, 512 completion, 553 total
#     - MultiLineAllUsage extracts input and output token fields
#     - an unknown TOKENPROCESSOR falls back to DefaultStream without data loss
#
# HOW TO TEST
#   Terminal 1: ./test/ManualTests/start_nullservers.sh 10
#   Terminal 2: source test/ManualTests/test10_setup.sh && \
#               dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-launch-profile
#   Terminal 3: ./test/ManualTests/test10_setup.sh verify
#
# WHAT TO EXPECT
#   All proxy bodies match direct backend bodies. The NDJSON proxy event records
#   the expected OpenAI and multiline usage fields, and the unknown processor
#   request completes through the default pass-through processor.

source "$(dirname -- "${BASH_SOURCE[0]}")/reset_proxy_settings.sh"

TEST10_EVENT_LOG="${TEST10_EVENT_LOG:-/tmp/simplel7proxy-test10-events-${UID}.ndjson}"
export TEST10_EVENT_LOG
if [[ "${BASH_SOURCE[0]}" != "$0" ]]; then
	rm -f "$TEST10_EVENT_LOG"
fi

export Host1="host=http://localhost:3000;mode=apim;path=/;probe=/health;processor=DefaultStream"
export LoadBalanceMode=roundrobin
export IterationMode=SinglePass
export MaxAttempts=1
export UseSharedIterators=false
export Workers=2
export PriorityWorkers="1:0"
export PollInterval=500
export PollTimeout=1000
export SuccessRate=100
export AsyncModeEnabled=false
export UseProfiles=false
export UserConfigRequired=false
export EVENT_LOGGERS=file
export LOGFILE_NAME="$TEST10_EVENT_LOG"
export LogToEvents="backend,proxy"
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
	if [[ "${1:-}" != "verify" ]]; then
		echo "Source this script before starting the proxy, then run: $0 verify" >&2
		exit 2
	fi

	set -euo pipefail
	TEST_PROXY_URL="${TEST_PROXY_URL:-http://localhost:8000}"
	TEST_BACKEND_URL="${TEST_BACKEND_URL:-http://localhost:3000}"
	TEST_TIMEOUT_SECONDS="${TEST_TIMEOUT_SECONDS:-30}"
	TEST_TMP="$(mktemp -d)"
	TEST_HEADERS="$TEST_TMP/headers"
	TEST_BODY="$TEST_TMP/body"
	trap 'rm -rf "$TEST_TMP"' EXIT

	test_fail() {
		echo "FAIL: $1" >&2
		[[ -s "$TEST_BODY" ]] && { echo "Response body:" >&2; head -c 2000 "$TEST_BODY" >&2; }
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

	wait_usage_event() {
		local key="$1"
		local jq_filter="$2"
		local deadline=$((SECONDS + TEST_TIMEOUT_SECONDS))
		local line
		while ((SECONDS < deadline)); do
			if [[ -f "$TEST10_EVENT_LOG" ]]; then
				while IFS= read -r line; do
					if jq -e --arg key "$key" \
						'select((.Path // "") | contains($key)) | '"$jq_filter" \
						<<<"$line" >/dev/null 2>&1; then
						return
					fi
				done < "$TEST10_EVENT_LOG"
			fi
		done
		test_fail "usage event for '$key' did not match: $jq_filter"
	}

	compare_proxy_to_backend() {
		local path="$1"
		local streaming="$2"
		local processor="${3:-}"
		local proxy_body="$TEST_TMP/proxy.body"
		local backend_body="$TEST_TMP/backend.body"
		local headers=(-H "X-Streaming: $streaming")
		[[ -n "$processor" ]] && headers+=(-H "X-TokenProcessor: $processor")

		local proxy_status
		proxy_status="$(curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" \
			--output "$proxy_body" --write-out '%{http_code}' "${headers[@]}" "$TEST_PROXY_URL$path")"
		local backend_status
		backend_status="$(curl --silent --show-error --max-time "$TEST_TIMEOUT_SECONDS" \
			--output "$backend_body" --write-out '%{http_code}' "${headers[@]}" "$TEST_BACKEND_URL$path")"
		test_assert_eq 200 "$proxy_status" "$path proxy status"
		test_assert_eq 200 "$backend_status" "$path backend status"
		cmp -s "$proxy_body" "$backend_body" || test_fail "$path proxy body differs from direct backend body"
		cp "$proxy_body" "$TEST_BODY"
	}

	command -v jq >/dev/null 2>&1 || test_fail "jq is required"
	test_wait_ready

	fixed_key="openai-fixed-${RANDOM}-${RANDOM}"
	compare_proxy_to_backend "/openai?case=$fixed_key" false
	wait_usage_event "$fixed_key" \
		'select(."Usage.Prompt_Tokens" == "41" and ."Usage.Completion_Tokens" == "512" and ."Usage.Total_Tokens" == "553")'

	chunked_key="openai-chunked-${RANDOM}-${RANDOM}"
	compare_proxy_to_backend "/openai?case=$chunked_key" true
	wait_usage_event "$chunked_key" \
		'select(."Usage.Prompt_Tokens" == "41" and ."Usage.Completion_Tokens" == "512" and ."Usage.Total_Tokens" == "553")'

	multiline_key="multiline-${RANDOM}-${RANDOM}"
	compare_proxy_to_backend "/multiline?case=$multiline_key" false
	wait_usage_event "$multiline_key" \
		'select(."Usage.Input_Tokens" == "10" and ."Usage.Output_Tokens" == "28")'

	fallback_key="unknown-${RANDOM}-${RANDOM}"
	compare_proxy_to_backend "/file-stream/stream_data.txt?case=$fallback_key" true NotAProcessor

	echo "PASS: fixed/chunked body integrity, processor fallback, and token telemetry"
fi
