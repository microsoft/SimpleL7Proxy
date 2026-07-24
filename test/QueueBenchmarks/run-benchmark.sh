#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd "$script_dir/../.." && pwd)
project="$script_dir/QueueBenchmarks.csproj"
benchmark_dll="$script_dir/bin/Release/net10.0/QueueBenchmarks.dll"

label="current"
duration_seconds=60
workers=1000
capacity=1000
probe_every=10000
shutdown_timeout_seconds=30
results_dir="$script_dir/results"
build=true

usage() {
    cat <<'EOF'
Usage: ./test/QueueBenchmarks/run-benchmark.sh [options]

Options:
  --label NAME               Result label, such as before or after (default: current)
  --duration SECONDS         Measured duration (default: 60)
  --workers COUNT            Concurrent queue workers (default: 1000)
  --capacity COUNT           New-work admission limit (default: 1000)
  --probe-every COUNT        Insert one probe every COUNT requests; 0 disables probes (default: 10000)
  --shutdown-timeout SECONDS Maximum drain/cancellation time (default: 30)
  --results-dir PATH         Output directory (default: test/QueueBenchmarks/results)
  --no-build                 Run the existing Release binary without rebuilding
  -h, --help                 Show this help

Examples:
  ./test/QueueBenchmarks/run-benchmark.sh --label before
  ./test/QueueBenchmarks/run-benchmark.sh --label after
  ./test/QueueBenchmarks/run-benchmark.sh --label smoke --duration 2 --results-dir /tmp/queue-results
EOF
}

require_value() {
    if [[ $# -lt 2 || -z "$2" ]]; then
        echo "Missing value for $1" >&2
        usage >&2
        exit 2
    fi
}

require_non_negative_integer() {
    if [[ ! "$2" =~ ^[0-9]+$ ]]; then
        echo "$1 must be a non-negative integer: $2" >&2
        exit 2
    fi
}

require_positive_integer() {
    require_non_negative_integer "$1" "$2"
    if (( $2 == 0 )); then
        echo "$1 must be greater than zero" >&2
        exit 2
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --label)
            require_value "$@"
            label=$2
            shift 2
            ;;
        --duration)
            require_value "$@"
            require_positive_integer "$1" "$2"
            duration_seconds=$2
            shift 2
            ;;
        --workers)
            require_value "$@"
            require_positive_integer "$1" "$2"
            workers=$2
            shift 2
            ;;
        --capacity)
            require_value "$@"
            require_positive_integer "$1" "$2"
            capacity=$2
            shift 2
            ;;
        --probe-every)
            require_value "$@"
            require_non_negative_integer "$1" "$2"
            probe_every=$2
            shift 2
            ;;
        --shutdown-timeout)
            require_value "$@"
            require_positive_integer "$1" "$2"
            shutdown_timeout_seconds=$2
            shift 2
            ;;
        --results-dir)
            require_value "$@"
            results_dir=$2
            shift 2
            ;;
        --no-build)
            build=false
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

for command_name in dotnet git sha256sum timeout awk tee; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command not found: $command_name" >&2
        exit 1
    fi
done

safe_label=$(printf '%s' "$label" | tr -c '[:alnum:]_.-' '_')
timestamp=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$results_dir"
raw_result="$results_dir/${timestamp}-${safe_label}.log"
formatted_result="$results_dir/${timestamp}-${safe_label}.summary.txt"

cd "$repo_root"

if [[ "$build" == true ]]; then
    echo "Building QueueBenchmarks (Release)..."
    dotnet build "$project" \
        -c Release \
        /property:GenerateFullPaths=true \
        /consoleloggerparameters:NoSummary
fi

if [[ ! -f "$benchmark_dll" ]]; then
    echo "Benchmark binary not found: $benchmark_dll" >&2
    echo "Run without --no-build or build the project first." >&2
    exit 1
fi

git_commit=$(git rev-parse HEAD)
git_branch=$(git branch --show-current)
git_branch=${git_branch:-detached}
queue_diff_hash=$(git diff -- \
    src/SimpleL7Proxy/Queue/ConcurrentPriQueue.cs \
    src/SimpleL7Proxy/Queue/ConcurrentSignal.cs \
    src/SimpleL7Proxy/Queue/PriorityQueue.cs \
    src/SimpleL7Proxy/Queue/PriorityQueueItem.cs | sha256sum | awk '{print $1}')
queue_source_hashes=$(sha256sum \
    src/SimpleL7Proxy/Queue/ConcurrentPriQueue.cs \
    src/SimpleL7Proxy/Queue/ConcurrentSignal.cs \
    src/SimpleL7Proxy/Queue/PriorityQueue.cs \
    src/SimpleL7Proxy/Queue/PriorityQueueItem.cs)
load_average_start=$(awk '{print $1 " " $2 " " $3}' /proc/loadavg)
hard_timeout_seconds=$((duration_seconds + shutdown_timeout_seconds + 15))

{
    echo "result_timestamp_utc=$timestamp"
    echo "result_label=$label"
    echo "git_commit=$git_commit"
    echo "git_branch=$git_branch"
    echo "queue_diff_sha256=$queue_diff_hash"
    echo "load_average_start=$load_average_start"
    echo "$queue_source_hashes"
} | tee "$raw_result"

echo
echo "Running ${duration_seconds}s queue benchmark..."
set +e
timeout \
    --signal=TERM \
    --kill-after=5s \
    "${hard_timeout_seconds}s" \
    dotnet "$benchmark_dll" \
        --workers "$workers" \
        --duration-seconds "$duration_seconds" \
        --capacity "$capacity" \
        --probe-every "$probe_every" \
        --shutdown-timeout-seconds "$shutdown_timeout_seconds" \
        --label "$label" 2>&1 | tee -a "$raw_result"
benchmark_status=${PIPESTATUS[0]}
set -e

load_average_end=$(awk '{print $1 " " $2 " " $3}' /proc/loadavg)
{
    echo "load_average_end=$load_average_end"
    echo "benchmark_exit_code=$benchmark_status"
} | tee -a "$raw_result"

if (( benchmark_status != 0 )); then
    echo >&2
    echo "Benchmark failed with exit code $benchmark_status." >&2
    echo "Raw output: $raw_result" >&2
    exit "$benchmark_status"
fi

overall_line=$(grep '^overall ' "$raw_result" | tail -n 1 || true)
if [[ -z "$overall_line" ]]; then
    echo "Benchmark completed without an overall result line." >&2
    echo "Raw output: $raw_result" >&2
    exit 1
fi

awk -v label="$label" \
    -v timestamp="$timestamp" \
    -v commit="$git_commit" \
    -v branch="$git_branch" \
    -v diff_hash="$queue_diff_hash" \
    -v load_start="$load_average_start" \
    -v load_end="$load_average_end" '
function grouped(value,    text, result, length_value, first, item_index) {
    text = sprintf("%.0f", value)
    length_value = length(text)
    first = length_value % 3
    if (first == 0) first = 3
    result = substr(text, 1, first)
    for (item_index = first + 1; item_index <= length_value; item_index += 3) {
        result = result "," substr(text, item_index, 3)
    }
    return result
}
function row(name, value) {
    printf "| %-29s | %22s |\n", name, value
}
BEGIN {
    split("", values)
}
/^overall / {
    for (item_index = 2; item_index <= NF; item_index++) {
        split($item_index, pair, "=")
        values[pair[1]] = pair[2]
    }
}
END {
    final_consumed = values["consumed_at_deadline"] + values["drained_after_deadline"]
    lost = values["accepted"] - final_consumed
    print "+-------------------------------+------------------------+"
    printf "| Queue Benchmark: %-36s |\n", label
    print "+-------------------------------+------------------------+"
    row("Measured duration", sprintf("%.3f s", values["elapsed_ms"] / 1000))
    row("Accepted requests", grouped(values["accepted"]))
    row("Consumed at deadline", grouped(values["consumed_at_deadline"]))
    row("Drained after deadline", grouped(values["drained_after_deadline"]))
    row("Backlog at deadline", grouped(values["backlog_at_deadline"]))
    row("Lost requests", grouped(lost))
    row("Admission retries", grouped(values["admission_retries"]))
    row("Accepted throughput", grouped(values["accepted_rps"]) " req/s")
    row("Consumed throughput", grouped(values["consumed_rps"]) " req/s")
    row("Queue delay p50", sprintf("%.2f us", values["p50_us"]))
    row("Queue delay p95", sprintf("%.2f us", values["p95_us"]))
    row("Queue delay p99", sprintf("%.2f us", values["p99_us"]))
    row("Maximum sampled delay", sprintf("%.2f us", values["max_us"]))
    row("Latency samples", grouped(values["latency_samples"]))
    row("Allocation/request", sprintf("%.2f bytes", values["allocated_bytes_per_request"]))
    row("CPU utilization", sprintf("%.2f%%", values["cpu_utilization_percent"]))
    print "+-------------------------------+------------------------+"
    printf "Timestamp (UTC): %s\n", timestamp
    printf "Git:             %s (%s)\n", substr(commit, 1, 12), branch
    printf "Queue diff hash: %s\n", diff_hash
    printf "Load avg start:  %s\n", load_start
    printf "Load avg end:    %s\n", load_end
}
' "$raw_result" | tee "$formatted_result"

echo
echo "Raw result:       $raw_result"
echo "Formatted result: $formatted_result"