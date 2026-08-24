#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
python_command=${PYTHON_COMMAND:-python3}
load_profile=${1:-single}

case "$load_profile" in
    single|--single)
        default_request_count=1
        default_max_workers=1
        ;;
    enormous|--enormous)
        default_request_count=10000
        default_max_workers=1000
        ;;
    -h|--help)
        printf '%s\n' \
            "Usage: $(basename "$0") [single|enormous]" \
            "" \
            "  single    Send one request with oversized headers (default)." \
            "  enormous  Send 10,000 requests with up to 1,000 concurrent workers." \
            "" \
            "Environment variables override all profile values, including" \
            "TARGET_URL, OVERSIZED_HEADER_COUNT, OVERSIZED_HEADER_SIZE_BYTES," \
            "REQUEST_COUNT, MAX_WORKERS, and REQUEST_TIMEOUT_SECONDS."
        exit 0
        ;;
    *)
        echo "Unknown load profile: $load_profile" >&2
        echo "Run $(basename "$0") --help for usage." >&2
        exit 2
        ;;
esac

export TARGET_URL=${TARGET_URL:-"http://localhost:8000/echo/resource?param1=sample"}
export OVERSIZED_HEADER_COUNT=${OVERSIZED_HEADER_COUNT:-32}
export OVERSIZED_HEADER_SIZE_BYTES=${OVERSIZED_HEADER_SIZE_BYTES:-4096}
export REQUEST_COUNT=${REQUEST_COUNT:-$default_request_count}
export MAX_WORKERS=${MAX_WORKERS:-$default_max_workers}
export REQUEST_TIMEOUT_SECONDS=${REQUEST_TIMEOUT_SECONDS:-30}

printf 'Load profile: %s; requests: %s; workers: %s\n' \
    "$load_profile" "$REQUEST_COUNT" "$MAX_WORKERS"

if ! command -v "$python_command" >/dev/null 2>&1; then
    echo "Python executable not found: $python_command" >&2
    exit 1
fi

if ! "$python_command" -c "import requests, urllib3" >/dev/null 2>&1; then
    echo "Missing Python dependencies. Install them with: $python_command -m pip install requests urllib3" >&2
    exit 1
fi

exec "$python_command" "$script_dir/echo_oversized_headers_test.py"