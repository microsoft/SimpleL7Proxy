#!/usr/bin/env bash
# Metrics Server Benchmark Test Runner
# Starts the metrics server and runs comprehensive throughput/latency benchmarks

set -e

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT="$SCRIPT_DIR/../../.."
METRICS_SERVER_PORT=${METRICS_SERVER_PORT:-5555}
METRICS_SERVER_URL="http://127.0.0.1:$METRICS_SERVER_PORT"

echo "================================"
echo "Metrics Server Benchmark Suite"
echo "================================"
echo ""

# Function to cleanup on exit (only if we started the server)
cleanup() {
    if [[ ! -z "$SERVER_PID" ]]; then
        echo ""
        echo "Shutting down Metrics Server (PID: $SERVER_PID)..."
        kill $SERVER_PID 2>/dev/null || true
        wait $SERVER_PID 2>/dev/null || true
    fi
}
trap cleanup EXIT

# Check if server is already running
echo "Checking if Metrics Server is already running on $METRICS_SERVER_URL..."
if curl -s "$METRICS_SERVER_URL/health" > /dev/null 2>&1; then
    echo "✓ Metrics Server is already running on port $METRICS_SERVER_PORT"
    echo "Proceeding with benchmark test..."
else
    echo "Metrics Server not running on port $METRICS_SERVER_PORT"
    echo "Attempting to start it..."
    
    # Check if metrics server can be built
    METRICS_PROJECT="$REPO_ROOT/src/MetricsServer/MetricsServer.csproj"
    if [[ ! -f "$METRICS_PROJECT" ]]; then
        echo "Error: Metrics Server project not found at $METRICS_PROJECT"
        echo "Make sure METRICS_SERVER_URL is set correctly or start the server manually."
        exit 1
    fi

    # Build metrics server if needed
    echo "Building Metrics Server..."
    cd "$REPO_ROOT"
    dotnet build "$METRICS_PROJECT" -c Release -q

    # Start metrics server in background
    METRICS_SERVER_BIN="$REPO_ROOT/src/MetricsServer/bin/Release/net10.0/MetricsServer"
    if [[ ! -f "$METRICS_SERVER_BIN" ]]; then
        echo "Error: Metrics Server binary not found after build"
        exit 1
    fi

    echo "Starting Metrics Server on port $METRICS_SERVER_PORT..."
    export ASPNETCORE_URLS="http://127.0.0.1:$METRICS_SERVER_PORT"
    export ASPNETCORE_ENVIRONMENT=Production

    # Start server in background and capture PID
    "$METRICS_SERVER_BIN" &
    SERVER_PID=$!

    # Wait for server to start
    echo "Waiting for Metrics Server to start..."
    for i in {1..30}; do
        if curl -s "$METRICS_SERVER_URL/health" > /dev/null 2>&1; then
            echo "✓ Metrics Server is ready"
            break
        fi
        if [[ $i -eq 30 ]]; then
            echo "Error: Metrics Server failed to start"
            exit 1
        fi
        sleep 1
    done
fi

echo ""
echo "Running benchmark tests..."
echo "This will test concurrency levels: 1000, 2000, 3000, 4000, 5000, 6000"
echo "Each level runs for 60 seconds"
echo ""

# Run the benchmark test
export METRICS_SERVER_URL="$METRICS_SERVER_URL"
export METRICS_SERVER_BENCHMARK_REQUIRED=true

cd "$SCRIPT_DIR"
dotnet test SimpleL7Proxy.Test.csproj \
    --filter "Name=MetricsServer_BenchmarkConcurrentRequests" \
    --logger "console;verbosity=detailed" \
    --no-build \
    -c Release \
    -- \
    --report-trx

echo ""
echo "================================"
echo "Benchmark Complete"
echo "================================"
