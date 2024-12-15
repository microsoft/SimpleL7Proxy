curl "https://nvmaoai-teams.openai.azure.com/openai/deployments/gpt35/chat/completions?api-version=2024-02-15-preview" \
      -H "Content-Type: application/json" \
      -H "Authorization: Bearer $(az account get-access-token --resource \"${cognitiveServicesResource}.default\" --query accessToken --output tsv)" \
      -d "{
        \"messages\": [{\"role\":\"system\",\"content\":\"You are an AI assistant that helps people find information.\"},{\"role\":\"user\",\"content\":\"tell me a joke that includes a 2 paragraph story about flowers in a garden\"}],
        \"past_messages\": 10,
        \"max_tokens\": 800,
        \"temperature\": 0.7,
        \"frequency_penalty\": 0,
        \"presence_penalty\": 0,

        \"top_p\": 0.95,
        \"stop\": null
      }"
