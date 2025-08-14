export APIKEY=c7e6f087cf984d43bbded523538246e9
export APIMKEY=e2376ce1252a44869e01dd2574da4a1b

echo "[$(date +%s%3N)] STARTING "

curl "http://localhost:8000/openai/deployments/d1/chat/completions?api-version=2025-01-01-preview" \
  -H "Content-Type: application/json; charset=UTF-8" \
  -H "Ocp-Apim-Subscription-Key: $APIMKEY" \
  -H "Ocp-Apim-Trace: true" \
  -H "api-key: $APIKEY" \
  -H "test: x" -H "xx: Value1" -H "X-UserProfile: 123456" \
  --no-buffer \
  -d @openai_call1.json -v |  while IFS= read -r line; do 
  echo "[$(date +%s%3N)] $line"
done





