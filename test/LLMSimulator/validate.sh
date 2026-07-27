#!/usr/bin/env bash
# validate.sh — smoke-test every LLM Simulator endpoint.
#
# Usage:
#   ./validate.sh                                  # tests against http://localhost:7071/api
#   BASE=https://myfunc.azurewebsites.net/api ./validate.sh
#   POLICY_TEST_SIMULATOR_URL=https://myapp.example.com ./validate.sh
#   ./validate.sh https://myfunc.azurewebsites.net/api

set -u

TARGET="${1:-${POLICY_TEST_SIMULATOR_URL:-${BASE:-http://localhost:7071}}}"
TARGET="${TARGET%/}"
if [[ "$TARGET" == */api ]]; then
  BASE="$TARGET"
else
  BASE="$TARGET/api"
fi
pass=0
fail=0

check() {
  local name="$1"; local url="$2"; local method="${3:-GET}"; local data="${4:-}"; local expect="${5:-200}"
  local out code ct sz status
  if [[ -n "$data" ]]; then
    out=$(curl -s -o /tmp/validate_body.txt -w "%{http_code}|%{content_type}|%{size_download}" \
          -X "$method" -H "Content-Type: application/json" -d "$data" --max-time 20 "$url")
  else
    out=$(curl -s -o /tmp/validate_body.txt -w "%{http_code}|%{content_type}|%{size_download}" \
          -X "$method" --max-time 20 "$url")
  fi
  code=$(echo "$out" | cut -d'|' -f1)
  ct=$(echo "$out"   | cut -d'|' -f2)
  sz=$(echo "$out"   | cut -d'|' -f3)
  if [[ "$code" == "$expect" ]]; then
    pass=$((pass+1)); status="OK  "
  else
    fail=$((fail+1)); status="FAIL"
  fi
  printf "%s  %-38s code=%s  ct=%-30s bytes=%s\n" "$status" "$name" "$code" "$ct" "$sz"
}

encode_spec() {
  printf "%s" "$1" | base64 | tr -d '\r\n=' | tr '+/' '-_'
}

check_scenario() {
  local name="$1" url="$2" expect="$3" slot="$4" delay_ms="$5" body_mode="$6" header_name="$7" header_value="$8"
  local headers_file body_file code status
  headers_file=$(mktemp)
  body_file=$(mktemp)
  code=$(curl -sS -D "$headers_file" -o "$body_file" -w "%{http_code}" \
    -X POST -H "Content-Type: application/json" -H "x-S7P-ID: validate-$slot" \
    -d '{"messages":[]}' --max-time 20 "$url")

  if [[ "$code" == "$expect" ]] &&
     grep -Fiq "S7P-ID: validate-$slot" "$headers_file" &&
     grep -Fiq "X-Sim-Case: smoke" "$headers_file" &&
     grep -Fiq "X-Sim-Slot: $slot" "$headers_file" &&
     grep -Fiq "X-Sim-Delay-Ms: $delay_ms" "$headers_file" &&
     grep -Fiq "X-Sim-Status: $expect" "$headers_file" &&
     grep -Fiq "X-Sim-Body: $body_mode" "$headers_file" &&
     grep -Fiq "X-Sim-Method: POST" "$headers_file" &&
     grep -Fiq "X-Sim-Path: /api/policy-scenario/$slot/smoke/" "$headers_file" &&
     grep -Fiq "X-Sim-Has-Authorization: false" "$headers_file" &&
     grep -Fiq "X-Sim-Has-Api-Key: false" "$headers_file" &&
     grep -Fiq "$header_name: $header_value" "$headers_file"; then
    pass=$((pass+1)); status="OK  "
  else
    fail=$((fail+1)); status="FAIL"
  fi

  printf "%s  %-38s code=%s  slot=%s  delay=%sms\n" "$status" "$name" "$code" "$slot" "$delay_ms"
  if [[ "$status" == "FAIL" ]]; then
    sed 's/\r$//' "$headers_file"
    cat "$body_file"
    echo
  fi
  rm -f "$headers_file" "$body_file"
}

echo "Base: $BASE"
echo

echo "=== Errors ==="
check "error429"   "$BASE/error/429"  GET "" 429
check "error500"   "$BASE/error/500"  GET "" 500
check "error302"   "$BASE/error/302"  GET "" 302

echo
echo "=== Policy scenarios ==="
scenario_a=$(encode_spec '{"delayMs":25,"status":429,"body":"json-error","headers":{"Retry-After":"30","retry-after-ms":["9716"],"X-Test-Values":["one","two"]}}')
scenario_b=$(encode_spec '{"delayMs":0,"status":201,"body":"text","bodyText":"slot-b","headers":{"X-Test-Result":"slot-b"}}')
scenario_invalid=$(encode_spec '{"status":200,"headers":{"Content-Length":"1"}}')
scenario_base="$BASE/policy-scenario"
check_scenario "policy scenario slot a" "$scenario_base/a/smoke/$scenario_a/$scenario_b/openai/v1/chat/completions" 429 a 25 json-error "Retry-After" 30
check_scenario "policy scenario slot b" "$scenario_base/b/smoke/$scenario_a/$scenario_b/openai/v1/chat/completions" 201 b 0 text "X-Test-Result" slot-b
check "policy scenario rejects unsafe header" "$scenario_base/a/invalid/$scenario_invalid/$scenario_b/test" POST '{"messages":[]}' 400

echo
echo "=== Health / Profile ==="
check "health"     "$BASE/health"     GET "" 200
check "profile"    "$BASE/profile"    GET "" 200

echo
echo "=== Azure OpenAI ==="
check "aoai gpt-4o-mini (at-once)"  "$BASE/openai/deployments/gpt-4o-mini/chat/completions?stream=false" POST '{"messages":[{"role":"user","content":"hi"}]}' 200
check "aoai gpt-4o-mini (stream)"   "$BASE/openai/deployments/gpt-4o-mini/chat/completions?stream=true&delay=0" POST '{"messages":[{"role":"user","content":"hi"}]}' 200
check "aoai aoai2"                  "$BASE/openai/deployments/aoai2/chat/completions?stream=false"      POST '{"messages":[]}' 200
check "aoai gpt-5-nano"             "$BASE/openai/deployments/gpt-5-nano/chat/completions?stream=false" POST '{"messages":[]}' 200
check "aoai responses"              "$BASE/openai/v1/responses"                                         POST '{"input":"hi"}' 200
check "aoai embeddings"             "$BASE/openai/deployments/text-embedding-ada-002/embeddings"        POST '{"input":"hi"}' 200

echo
echo "=== OpenAI (public) ==="
check "openai chat"                 "$BASE/v1/chat/completions?stream=false" POST '{"messages":[]}' 200

echo
echo "=== Anthropic (model from body) ==="
check "anthropic haiku"             "$BASE/anthropic/v1/messages" POST '{"model":"claude-3-haiku-20240307","messages":[]}' 200
check "anthropic sonnet-3"          "$BASE/anthropic/v1/messages" POST '{"model":"claude-3-sonnet-20240229","messages":[]}' 200
check "anthropic sonnet-3.5"        "$BASE/anthropic/v1/messages" POST '{"model":"claude-3-5-sonnet-20241022","messages":[]}' 200
check "anthropic sonnet-4"          "$BASE/anthropic/v1/messages" POST '{"model":"claude-sonnet-4-20250514","messages":[]}' 200

echo
echo "=== Gemini ==="
check "gemini flash generateContent"     "$BASE/v1beta/models/gemini-2.0-flash:generateContent"             POST '{"contents":[]}' 200
check "gemini pro streamGenerateContent" "$BASE/v1beta/models/gemini-1.5-pro:streamGenerateContent?delay=0" POST '{"contents":[]}' 200

echo
echo "=== Fixtures ==="
check "samples lorem"               "$BASE/samples/lorem"     GET "" 200
check "samples multiline"           "$BASE/samples/multiline" GET "" 200

echo
echo "=== Delay / Stream ==="
check "delay 100ms"                 "$BASE/delay?delay=100"     GET "" 200
check "streamdelay"                 "$BASE/streamdelay?delay=0" GET "" 200

echo
echo "=== Streaming override (X-Force-Stream: false) ==="
out=$(curl -s -o /dev/null -w "%{content_type}" -X POST \
      -H "Content-Type: application/json" \
      -H "X-Force-Stream: false" \
      -d '{"messages":[]}' --max-time 20 \
      "$BASE/openai/deployments/gpt-4o-mini/chat/completions")
if [[ "$out" == text/plain* ]]; then
  pass=$((pass+1)); printf "OK    %-38s ct=%s\n" "x-force-stream=false flips to plain" "$out"
else
  fail=$((fail+1)); printf "FAIL  %-38s ct=%s (expected text/plain*)\n" "x-force-stream=false flips to plain" "$out"
fi

echo
echo "=== Summary ==="
echo "PASS: $pass   FAIL: $fail"
exit $(( fail > 0 ? 1 : 0 ))
