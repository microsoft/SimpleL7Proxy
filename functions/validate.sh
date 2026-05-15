#!/usr/bin/env bash
# validate.sh — smoke-test every LLM Simulator endpoint.
#
# Usage:
#   ./validate.sh                                  # tests against http://localhost:7071/api
#   BASE=https://myfunc.azurewebsites.net/api ./validate.sh
#   ./validate.sh https://myfunc.azurewebsites.net/api

set -u

BASE="${1:-${BASE:-http://localhost:7071/api}}"
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

echo "Base: $BASE"
echo

echo "=== Errors ==="
check "error429"   "$BASE/error/429"  GET "" 429
check "error500"   "$BASE/error/500"  GET "" 500
check "error302"   "$BASE/error/302"  GET "" 302

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
