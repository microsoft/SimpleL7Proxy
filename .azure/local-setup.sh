#!/bin/bash

# Local-only setup for SimpleL7Proxy
echo "========================================================"
echo "  SimpleL7Proxy Local Setup"
echo "========================================================"
echo ""

# Simple prompt helper (non-recursive). Usage: read_input "Prompt" "default"
read_input() {
  local prompt="$1"
  local default="$2"
  local input=""

  if [ -n "$default" ]; then
    prompt="$prompt [$default]"
  fi

  # Non-interactive fallback: return default or empty
  if [ ! -t 0 ]; then
    if [ -n "$default" ]; then
      printf "%s\n" "$default"
      return 0
    else
      printf "\n"
      return 1
    fi
  fi

  # Read input from terminal
  read -r -p "$prompt: " input || input=""

  if [ -z "$input" ] && [ -n "$default" ]; then
    input="$default"
  fi

  printf "%s" "$input"
}

# Defaults (can be inherited from env variables)
environment="${environment:-dev}"
region="${env_AZURE_LOCATION:-westus2}"

echo ""
echo "🔧 Local development: minimal prompts to configure local run"
echo ""

# Backend selection
echo "🔎 Local backend options: choose how the proxy will reach your APIs"
echo "   Options: 'apim' = existing APIM gateway, 'null' = local null/mock server, 'real' = real backend URLs"
backend_mode=$(read_input "Backend (apim/null/real)" "real")

case "$backend_mode" in
  apim)
    apim_url=$(read_input "APIM gateway URL (e.g. https://myapim.azure-api.net)" "")
    backend_urls="$apim_url"
    ;;
  null)
    echo ""
    echo "Provide one or more local mock server URLs (include port numbers)."
    echo "Example: http://localhost:3000 or http://localhost:3000,http://localhost:3001"
    mock_urls=$(read_input "Local mock server URL(s)" "http://localhost:3000")
    backend_urls="$mock_urls"
    ;;
  *)
    backend_urls=""
    ;;
esac

# Proxy port
proxy_port=$(read_input "Local proxy listen port" "5000")

# Backend URLs (if not provided above)
if [ -z "$backend_urls" ]; then
  default_urls="${env_BACKEND_HOST_URLS:-}"
  backend_urls=$(read_input "Enter comma-separated list of backend URLs" "$default_urls")
  while [ -z "$backend_urls" ]; do
    echo ""
    echo "❌ Backend URLs are required!"
    echo "   Example: https://api1.example.com,https://api2.example.com"
    backend_urls=$(read_input "Enter comma-separated list of backend URLs" "")
  done
fi

# Timeout
default_timeout_val="${env_DEFAULT_REQUEST_TIMEOUT:-100000}"
default_timeout=$(read_input "Enter default request timeout in milliseconds" "$default_timeout_val")

## Persist local config to the script's own directory (robust to cwd)
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
local_config_file="$script_dir/local-dev.env"
mkdir -p "$(dirname "$local_config_file")"
echo "# Local Development Configuration" > "$local_config_file"
echo "ENVIRONMENT_NAME=$environment" >> "$local_config_file"
[ -n "$subscription" ] && echo "AZURE_SUBSCRIPTION_ID=$subscription" >> "$local_config_file"
echo "AZURE_LOCATION=$region" >> "$local_config_file"
echo "BACKEND_HOST_URLS=$backend_urls" >> "$local_config_file"
echo "DEFAULT_REQUEST_TIMEOUT=$default_timeout" >> "$local_config_file"

# Look for defaults file in env-templates and append its exported vars to the env file
defaults_file="$script_dir/env-templates/local-dev-env-defaults.sh"
if [ -f "$defaults_file" ]; then
  echo "" >> "$local_config_file"
  echo "# Seeded defaults" >> "$local_config_file"
  # Convert exports to KEY=VALUE lines for env file
  sed -n 's/^export \([A-Za-z0-9_]*\)=\(.*\)/\1=\2/p' "$defaults_file" >> "$local_config_file"
fi

echo "✅ Local development configuration saved to: $local_config_file"
echo ""
echo "🎉 Local setup complete. Run 'cd src/SimpleL7Proxy && dotnet run' to start locally."

exit 0
