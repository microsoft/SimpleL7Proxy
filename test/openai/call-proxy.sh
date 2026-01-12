# Usage: callaoai <hostalias> [requesttype] [jsonfile] [--rid response_id]
# hostalias: nvmtr2, nvmtr3, etc. (see HOSTS below)
# requesttype: request, response, check, get-response, or empty (defaults to request)
# jsonfile: JSON payload file to use (defaults to openai_call1.json, not used for get-response)
# --rid response_id: Response ID for get-response requests


# Default API key (fallback if not specified in HOSTMAP)
# source env.sh

# Map host aliases to hostname|folder|apikey (use empty after | for no folder, use empty for default APIKEY)
declare -A HOSTMAP
HOSTMAP["tr2"]="nvmtr2apim.azure-api.net|bf2|"
HOSTMAP["tr2-o"]="nvmtr2apim.azure-api.net|resp|"
HOSTMAP["apim2"]="nvmmcpapim2.azure-api.net||"
HOSTMAP["local-ai"]="localhost:8000|openai|"
HOSTMAP["local-echo"]="localhost:8000|echo|"
HOSTMAP["local-resp"]="localhost:8000|resp|"
HOSTMAP["local-direct"]="localhost:8000|api2/openai|"
HOSTMAP["local"]="localhost:8000||"
HOSTMAP["openai3"]="nvmopenai3.openai.azure.com|openai|"
HOSTMAP["nvm2"]="nvm2.openai.azure.com|openai|"
HOSTMAP["foundry"]="localhost:8000|aif2/openai|"
HOSTMAP["null"]="localhost:3000||"
HOSTMAP["aca"]="simplel7dev.agreeableisland-74a4ba5f.eastus.azurecontainerapps.io|"
HOSTMAP["aca-resp"]="simplel7dev.agreeableisland-74a4ba5f.eastus.azurecontainerapps.io|resp"
# Add more host aliases as needed:
# HOSTMAP["nvmtr3"]="nvmtr3apim.azure-api.net|somefolder|custom-api-key"


# Map request types to HTTP method and partial URLs (format: "METHOD /url")
declare -A URLS
URLS["4.1chat"]="POST /deployments/gpt-4.1/chat/completions?api-version=2025-01-01-preview"
URLS["4.1request"]="POST /v1/responses"
URLS["4.1response"]="GET /v1/responses"
URLS["chat"]="POST /deployments/d1/chat/completions?api-version=2025-01-01-preview"
URLS["request"]="POST /responses?api-version=2025-04-01-preview"
URLS["check"]="POST /deployments/d1/chat/check?api-version=2025-01-01-preview"
URLS["direct-chat"]="POST /openai/v1/chat/completions?api-version=2024-05-01-preview"
URLS["multiline"]="POST /multiline"
URLS["resource"]="GET /resource?param1=sample"
URLS["400"]="POST /400error"
URLS["401"]="POST /401error"
URLS["403"]="POST /403error"
URLS["404"]="POST /404error"
URLS["412"]="POST /412error"
URLS["421"]="POST /421error"
URLS["429"]="POST /429error"
URLS["500"]="POST /500error"
URLS["health"]="GET /health"
URLS["sleep10"]="POST /delay10seconds"
URLS["sleep100"]="POST /delay100seconds"
URLS["sleep200"]="POST /delay200seconds"
URLS["sleep400"]="POST /delay400seconds"
URLS["sleep800"]="POST /delay800seconds"
URLS["4o-mini.txt"]="POST /file/4o-mini.txt"
URLS["claude-3.5"]="POST /file/claude-3.5-haiku.txt"
URLS["claude-sonnet"]="POST /file/claude-sonnet-3.5.txt"
URLS["embeddings"]="POST /file/embeddings.txt"
URLS["gemini-2.5"]="POST /file/gemini-2.5.txt"
URLS["gemini-2.5-pro.txt"]="POST /file/gemini-2.5-pro.txt"
URLS["gemini-2.5-pro-chat"]="POST /file/gemini-2.5-pro-chat.txt"
URLS["gemini-2.5-pro-stream"]="POST /file/gemini-2.5-pro-stream.txt"
URLS["gpt5-nano-response"]="POST /file/gpt5-nano-response.txt"
URLS["gpt5-nano"]="POST /file/gpt5-nano.txt"
URLS["api"]="POST /openai/v1//chat/completions"
URLS["echo"]="GET /echo/resource?param1=sample"


# Parse command line arguments
verbose=""
asyncmode="false"
debugmode="false"
expiredelta="900"
response_id=""
custom_apikey=""
show_timestamps="true"
args=()

# Process arguments to extract flags and positional parameters
i=0
while [ $i -lt $# ]; do
  ((i++))
  case "${!i}" in
    -v)
      verbose="-v"
      ;;
    -a|--async)
      asyncmode="true"
      ;;
    -d|--debug)
      debugmode="true"
      ;;
    -n|--no-timestamps)
      show_timestamps="false"
      ;;
    --rid)
      ((i++))
      if [ $i -le $# ]; then
        response_id="${!i}"
      else
        echo "Error: --rid requires a value"
        exit 1
      fi
      ;;
    --key)
      ((i++))
      if [ $i -le $# ]; then
        custom_apikey="${!i}"
      else
        echo "Error: --key requires a value"
        exit 1
      fi
      ;;
    *)
      args+=("${!i}")
      ;;
  esac
done

hostalias="${args[0],,}"
requesttype="${args[1],,}"
jsonfile="${args[2]}"

if [ -z "$hostalias" ]; then
  echo "Usage: call-proxy <hostalias> [requesttype] [jsonfile] [--rid response_id] [--key api_key] [-v] [-a|--async] [-d|--debug] [-n|--no-timestamps]"
  echo " Examples:"
  echo "  ./call-proxy.sh local multiline openai_call1.json -v -a"
  echo "  ./call-proxy.sh local embeddings openai_call1.json -v -a"
  echo "  ./call-proxy.sh local 429 openai_call1.json -v"
  echo "  ./call-proxy.sh foundry request openai_call-bg.json -v"
  echo "  ./call-proxy.sh openai3 chat openai_call1.json -v"
  echo "  ./call-proxy.sh local-ai chat openai_call1.json -v -a"
  echo "  ./call-proxy.sh local gemini-2.5-pro--chat"
  echo "  ./call-proxy.sh tr2-o 4.1request openai_call-bg.json"
  echo "  ./call-proxy.sh local-resp 4.1response --rid <response_id> "
  echo "  ./call-proxy.sh local chat openai_call1.json --key abcdef"
  echo "  ./call-proxy.sh local chat openai_call1.json -n"
  exit 1
fi

# Default requesttype to 'request' if empty or not recognized
if [ -z "$requesttype" ] || [[ -z "${URLS[$requesttype]}" ]]; then
  requesttype="request"
fi

# Handle special case: if response with --rid, use response-get instead
if [ "$requesttype" = "response" ] && [ -n "$response_id" ]; then
  requesttype="response-get"
fi

# Default jsonfile to 'openai_call1.json' if empty
if [ -z "$jsonfile" ]; then
  jsonfile="openai_call1.json"
fi

# Validate response_id for GET-only request types
if [[ ("$requesttype" = "get-response" || "$requesttype" = "4.1response" || "$requesttype" = "response-get") && -z "$response_id" ]]; then
  echo "Error: $requesttype requires a response ID"
  echo "Usage: callaoai <hostalias> $requesttype [jsonfile] --rid <response_id>"
  exit 1
fi

# Parse host mapping - now supports hostname|folder|apikey format
IFS='|' read -r hostname folder apikey <<< "${HOSTMAP[$hostalias]}"
if [ -z "$hostname" ]; then
  echo "Unknown host alias: $hostalias"
  exit 1
fi
if [ -z "$folder" ]; then
  folder=""
fi
# Use host-specific API key if provided, otherwise use default
if [ -z "$apikey" ]; then
  apikey="$DEFAULT_APIKEY"
fi

# Parse method and URL from URLS mapping
IFS=' ' read -r http_method partialurl <<< "${URLS[$requesttype]}"
if [ -z "$http_method" ] || [ -z "$partialurl" ]; then
  echo "Error: Invalid request type configuration for: $requesttype"
  exit 1
fi

if [[ "$hostname" == localhost* ]]; then
  scheme="http"
else
  scheme="https"
fi

# Handle GET requests with response ID - append response_id to URL
if [[ "$http_method" = "GET" && -n "$response_id" ]]; then
  partialurl="$partialurl/$response_id"
fi

# Only insert folder if not empty
if [ -n "$folder" ]; then
  fullurl="$scheme://$hostname/$folder$partialurl"
else
  fullurl="$scheme://$hostname$partialurl"
fi

if [ "$show_timestamps" = "true" ]; then
  echo "[$(date +%s%3N)] Calling: $fullurl"
else
  echo "Calling: $fullurl"
fi
echo "----------------------------------------"

# Execute curl with appropriate method
curl_cmd=(
  curl -X "$http_method" "$fullurl"
  -H "Content-Type: application/json; charset=UTF-8"
  -H "Ocp-Apim-Subscription-Key: $APIMKEY"
  -H "S7PTTL: $expiredelta"
  -H "test: x" -H "xx: Value1" -H "X-UserProfile: 123456"
)

# Add api-key header if --key parameter was provided
if [ -n "$custom_apikey" ]; then
  curl_cmd+=( -H "api-key: $custom_apikey" )
else
  curl_cmd+=( -H "api-key: $apikey" )
fi

if [ "$asyncmode" = "true" ]; then
  curl_cmd+=( -H "AsyncMode: true" )
fi

if [ "$debugmode" = "true" ]; then
  curl_cmd+=( -H "S7PDEBUG: true" )
fi

curl_cmd+=( --no-buffer $verbose )

if [ "$http_method" = "GET" ]; then
  curl_cmd+=(-w "\n----------------------------------------\n[Status Code: %{http_code}]\n")
  if [ "$show_timestamps" = "true" ]; then
    "${curl_cmd[@]}" | while IFS= read -r line; do
      echo "[$(date +%s%3N)] $line"
    done
  else
    "${curl_cmd[@]}"
  fi
else
  curl_cmd+=(-d @"$jsonfile" -w "\n----------------------------------------\n[Status Code: %{http_code}]\n")
  if [ "$show_timestamps" = "true" ]; then
    "${curl_cmd[@]}" | while IFS= read -r line; do
      echo "[$(date +%s%3N)] $line"
    done
  else
    "${curl_cmd[@]}"
  fi
fi