export APIKEY=c7e6f087cf984d43bbded523538246e9
export APIMKEY=e2376ce1252a44869e01dd2574da4a1b

curl "https://nvmopenai3.openai.azure.com/openai/deployments/d1/chat/completions?api-version=2025-01-01-preview" \
  -H "Content-Type: application/json" \
  -H "Ocp-Apim-Subscription-Key: $APIMKEY" \
  -H "Ocp-Apim-Trace: true" \
  -H "api-key: $APIKEY" \
  -d @openai_call1.json -v
