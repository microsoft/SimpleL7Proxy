# Metrics Server Benchmark Test

## Overview

The `MetricsServerBenchmarkTests` class provides comprehensive performance benchmarking for the Metrics Server under sustained concurrent load.

**What it tests:**
- Metrics Server throughput at various concurrency levels (1000, 2000, 3000, 4000, 5000, 6000 concurrent requests)
- Latency percentiles (P50, P90, P95, P99) during sustained load
- Request success rate and error handling
- Performance degradation across increasing concurrency levels

**Duration:** ~6 minutes total (60 seconds × 6 concurrency levels + recovery time)

## Running the Benchmark

### Option 1: Automated (Recommended)

The easiest way is to use the provided benchmark runner script:

```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
./run-metrics-benchmark.sh
```

This script will:
1. Build the Metrics Server in Release mode
2. Start a local Metrics Server instance
3. Run the benchmark test against it
4. Display formatted results
5. Shut down the server gracefully

### Option 2: Manual with External Server

If you have a Metrics Server running elsewhere:

```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
export METRICS_SERVER_URL=http://your-server:port
export METRICS_SERVER_BENCHMARK_REQUIRED=true
dotnet test SimpleL7Proxy.Test.csproj \
    --filter "Name=MetricsServer_BenchmarkConcurrentRequests" \
    --logger "console;verbosity=detailed"
```

### Option 3: Include in Full Test Suite

To run benchmarks as part of the regular test suite:

```bash
# Run all benchmarks (will skip if server unavailable)
cd ~/repos/SimpleL7Proxy/test/RegressionTests
./run-tests.sh all

# Run only performance/benchmark tests
./run-tests.sh --filter "TestCategory=Benchmark"
```

## Output Format

The benchmark produces detailed output showing:

### Per-Concurrency-Level Output
```
================================================== Concurrency Level: 1000 ==================================================

Concurrency:     1000 requests
Duration:          60.0 seconds
Total Requests:   45000 (750 req/sec)
Success Rate:    100.0%
Failed Requests:      0

Latency (ms):
  Min:        1.23
  P50:        5.45
  P90:       12.34
  P95:       18.76
  P99:       42.18
  Avg:        7.89
  Max:       156.34
```

### Summary Table
```
==================================================== BENCHMARK SUMMARY - Metrics Server Performance ====================================================
Concurrency  Req/Sec    Success %  P50 (ms)  P90 (ms)  P95 (ms)  P99 (ms)
────────────────────────────────────────────────────────────────────────────────────────────────────────────────
1000         750        100.0      5.45      12.34     18.76     42.18
2000         1500       100.0      5.67      13.12     19.45     48.92
3000         2250       100.0      6.23      15.67     22.34     56.78
...
```

## Success Criteria

The test passes if:
- **P99 Latency < 1000ms:** 99th percentile latency stays under 1 second
- **No excessive degradation:** P99 latency doesn't increase by more than 2x from the first level
- **High success rate:** > 99% of requests succeed

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `METRICS_SERVER_URL` | `http://127.0.0.1:5555` | URL of the Metrics Server instance |
| `METRICS_SERVER_BENCHMARK_REQUIRED` | (unset) | Set to `true` to fail if server unavailable; otherwise test is skipped |
| `ASPNETCORE_URLS` | (auto) | Set by run-metrics-benchmark.sh; don't override |

## Interpreting Results

### Healthy Performance
- P99 latency: < 100ms (excellent), < 200ms (good), < 500ms (acceptable)
- Throughput: Scales linearly or super-linearly with concurrency
- Success rate: > 99.9%

### Warning Signs
- P99 latency increases 2x or more from 1000 to 6000 concurrency
- Success rate drops below 99%
- Max latency spikes indicate occasional pauses/GC
- Error patterns (429, 503) suggest resource exhaustion

### Common Issues

**High P99 latency at high concurrency:**
- Metrics Server may need more CPU/memory
- Database/storage backend may be saturated
- Consider increasing connection pool size

**Increasing error rate:**
- Server resource limits (connections, memory) reached
- Database connection pool exhausted
- Backend storage slow

**Latency spikes:**
- Garbage collection pauses (see GC configuration)
- Context switching on overloaded CPU
- Network buffer exhaustion

## Test Categories

This test has the following MSTest categories:
- `Benchmark` - Performance/benchmark tests
- `Performance` - Performance-related tests  
- `Metrics` - Metrics server tests

## Running Specific Concurrency Levels

To test only a subset of concurrency levels, you can modify the test:

```csharp
// In MetricsServerBenchmarkTests.cs, change:
private static readonly int[] ConcurrencyLevels = [1000, 2000]; // Only test 1000 and 2000
```

Then rebuild and run.

## Performance Baseline

Expected results for a healthy Metrics Server (per concurrency level):

| Concurrency | Req/Sec | P90 (ms) | P95 (ms) | P99 (ms) | Success % |
|-------------|---------|----------|----------|----------|-----------|
| 1000        | 700-800 | <15      | <20      | <50      | >99.9     |
| 2000        | 1400-1600 | <20    | <25      | <60      | >99.9     |
| 3000        | 2000-2200 | <25    | <30      | <75      | >99.9     |
| 4000        | 2500-2700 | <30    | <40      | <90      | >99.9     |
| 5000        | 3000-3200 | <35    | <50      | <110     | >99.9     |
| 6000        | 3500-3700 | <40    | <60      | <150     | >99.9     |

*These baselines are for reference; actual numbers depend on:*
- Server hardware (CPU cores, RAM, disk speed)
- Load balancer configuration
- Backend storage performance
- Network conditions

## Troubleshooting

### Test times out
- Increase the `[Timeout]` attribute value in the test class
- Reduce `ConcurrencyLevels` array size
- Check if Metrics Server is running and responsive

### All requests fail
- Verify Metrics Server is running: `curl http://127.0.0.1:5555/health`
- Check `METRICS_SERVER_URL` environment variable
- Check Metrics Server logs for errors

### High error rate
- Check Metrics Server logs for resource exhaustion
- Verify backend storage is accessible
- Check network connectivity

### Inconsistent results
- Run multiple times to account for system variance
- Ensure no other heavy workloads running
- Check server resource utilization during test

## See Also

- [Metrics Server Documentation](../../src/MetricsServer/README.md)
- [Load Testing Guide](../../docs/reference/load-testing.md)
- [Performance Tuning](../../docs/how-to/performance-tuning.md)
