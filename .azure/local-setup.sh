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

# Derive Host1 from the first backend URL and prompt for a probe path
# Use APIM-specific default probe path when the backend_mode is 'apim'
first_backend=$(printf "%s" "$backend_urls" | awk -F, '{print $1}')
# Always use the first backend URL as Host1 (no extra prompt). Only ask for the probe path.
Host1="$first_backend"
if [ "$backend_mode" = "apim" ]; then
  Probe_path1_default="/status-0123456789abcdef"
else
  Probe_path1_default="/health"
fi
Probe_path1=$(read_input "Probe path for Host1 (Probe_path1)" "$Probe_path1_default")

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
echo "DEFAULT_REQUEST_TIMEOUT=$default_timeout" >> "$local_config_file"
echo "# Primary backend host and probe" >> "$local_config_file"
# Ensure Host1 is exported here so child processes see it and it won't be overridden by seeded defaults
echo "export Host1=$Host1" >> "$local_config_file"
echo "export Probe_path1=$Probe_path1" >> "$local_config_file"
# Export port (queried by the script) so the dynamic section contains all interactive values
echo "export Port=$proxy_port" >> "$local_config_file"

# Look for defaults file in env-templates and append its exported vars to the env file
static_defaults="$script_dir/env-templates/local-dev-env-static-defaults.sh"
legacy_defaults="$script_dir/env-templates/local-dev-env-defaults.sh"

# Append static defaults. Prefer a dedicated static defaults file if present. If not, fall back to the
# legacy combined defaults file but skip any keys that were queried interactively so they don't get
# overwritten by the appended template.
if [ -f "$static_defaults" ]; then
  echo "" >> "$local_config_file"
  echo "# Seeded static defaults" >> "$local_config_file"
  # Append static defaults preserving comments and exported assignments
  awk '
  { 
    if ($0 ~ /^\s*#/) { print $0; next }
    if ($0 ~ /^\s*export[[:space:]]+[A-Za-z0-9_]+=.*/) {
      sub(/^[[:space:]]*export[[:space:]]+/, "export "); print; next
    }
    if ($0 ~ /^\s*[A-Za-z0-9_]+=.*/) {
      sub(/^[[:space:]]*/, ""); print "export "$0; next
    }
  }' "$static_defaults" >> "$local_config_file"
elif [ -f "$legacy_defaults" ]; then
  echo "" >> "$local_config_file"
  echo "# Seeded defaults (legacy file; queried keys skipped)" >> "$local_config_file"
  # Skip keys that are provided interactively so they remain authoritative
  awk '
  { 
    if ($0 ~ /^\s*#/) { print $0; next }
    # Skip keys that are queried by this script to avoid overwriting them
    if ($0 ~ /^\s*(export[[:space:]]+)?(Host1|Probe_path1|Port|DEFAULT_REQUEST_TIMEOUT|ENVIRONMENT_NAME|AZURE_LOCATION)=.*/) { next }
    if ($0 ~ /^\s*export[[:space:]]+[A-Za-z0-9_]+=.*/) {
      sub(/^[[:space:]]*export[[:space:]]+/, "export "); print; next
    }
    if ($0 ~ /^\s*[A-Za-z0-9_]+=.*/) {
      sub(/^[[:space:]]*/, ""); print "export "$0; next
    }
  }' "$legacy_defaults" >> "$local_config_file"
fi

echo "✅ Local development configuration saved to: $local_config_file"
echo ""
echo "======================================"
echo "🎉 Local setup complete."

# Provide instructions for null/mock server and starting the proxy
if [ "$backend_mode" = "null" ]; then
  # Extract first host/port from backend_urls (expecting http://host:port)
  first_host=$(printf "%s" "$backend_urls" | awk -F, '{print $1}')
  # parse port if present
  port=$(printf "%s" "$first_host" | sed -n 's|.*:\([0-9]*\)$|\1|p')
  if [ -z "$port" ]; then
    port="3000"
  fi

  cat <<EOF
You will need two terminals in order to run this scenario:
 * The null server
 * The proxy server

  Before running any test commands below, source the generated env file in the same shell so the
  environment variables are available to the commands (example):

  > source .azure/local-dev.env
EOF

    # show both Python and .NET options (do not do per-file existence checks)
    py_dir="$script_dir/../test/nullserver/Python"
    dotnet_dir="$script_dir/../test/nullserver/dotnet"
  sed -e "s|{{PY_DIR}}|$py_dir|g" \
    -e "s|{{DOTNET_DIR}}|$dotnet_dir|g" \
    -e "s|{{START_CMD_PY}}|source $local_config_file && python3 stream_server.py --port \$BACKEND1_PORT|g" \
    -e "s|{{START_CMD_DOTNET}}|source $local_config_file && dotnet run --urls http://localhost:\$BACKEND1_PORT|g" \
    "$script_dir/scenarios/null_server.txt" | sed 's/^/  /'
  # Print proxy run instructions using template
  sed -e "s|{{LOCAL_CONFIG_FILE}}|$local_config_file|g" "$script_dir/scenarios/proxy_run.txt" | sed 's/^/  /'
else
  sed -e "s|{{LOCAL_CONFIG_FILE}}|$local_config_file|g" "$script_dir/scenarios/proxy_run.txt" | sed 's/^/  /'
fi

cat <<EOF
  After both have started, you can run some quick commands to test.

  Quick test examples (after starting server/proxy):
    curl -v http://localhost:\$BACKEND1_PORT\$Probe_path1
    curl -v \$Host1\$Probe_path1
EOF

exit 0
