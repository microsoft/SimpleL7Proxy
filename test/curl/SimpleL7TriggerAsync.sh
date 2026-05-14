#!/usr/bin/env bash
# Trigger one or more async requests against SimpleL7Proxy.
#
# Usage:
#   ./SimpleL7TriggerAsync.sh                 # single request (default)
#   ./SimpleL7TriggerAsync.sh -n 10           # 10 requests, sequential
#   ./SimpleL7TriggerAsync.sh -n 10 -p        # 10 requests, all in parallel
#   ./SimpleL7TriggerAsync.sh -n 50 -p -c 5   # 50 requests, max 5 concurrent

set -u

URL="https://simplel7dev.wittybeach-67bb528b.eastus.azurecontainerapps.io/api/delay?delay=40000"
DATA_FILE="sample.txt"

N=1
PARALLEL=0
CONCURRENCY=0   # 0 = unlimited (only used when PARALLEL=1)

usage() {
    echo "Usage: $0 [-n count] [-p] [-c concurrency]" >&2
    echo "  -n count        Number of requests to send (default: 1)" >&2
    echo "  -p              Send requests in parallel (default: sequential)" >&2
    echo "  -c concurrency  Max parallel requests at once (0 = unlimited)" >&2
    exit 1
}

while getopts ":n:pc:h" opt; do
    case "$opt" in
        n) N="$OPTARG" ;;
        p) PARALLEL=1 ;;
        c) CONCURRENCY="$OPTARG" ;;
        h|*) usage ;;
    esac
done

if ! [[ "$N" =~ ^[0-9]+$ ]] || [ "$N" -lt 1 ]; then
    echo "Error: -n must be a positive integer" >&2
    exit 1
fi

send_one() {
    local i="$1"
    curl -sS -i \
        "$URL" \
        -H "X-UserProfile: 123456" \
        -H "S7PAsyncMode: true" \
        -H "X-Request-Index: $i" \
        -d "@${DATA_FILE}"
    echo
    echo "----- request $i done -----"
}

if [ "$PARALLEL" -eq 0 ]; then
    for ((i = 1; i <= N; i++)); do
        send_one "$i"
    done
else
    if [ "$CONCURRENCY" -gt 0 ]; then
        # Throttled parallel: keep at most CONCURRENCY background jobs.
        for ((i = 1; i <= N; i++)); do
            send_one "$i" &
            while [ "$(jobs -rp | wc -l)" -ge "$CONCURRENCY" ]; do
                wait -n 2>/dev/null || sleep 0.05
            done
        done
    else
        # Unlimited parallel: fire them all.
        for ((i = 1; i <= N; i++)); do
            send_one "$i" &
        done
    fi
    wait
fi

