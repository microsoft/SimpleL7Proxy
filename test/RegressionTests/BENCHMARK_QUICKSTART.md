# Metrics Server Benchmark - Quick Start

## Run the Benchmark in 30 Seconds

```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
chmod +x run-metrics-benchmark.sh
./run-metrics-benchmark.sh
```

That's it! The script will:
1. ✅ Build Metrics Server (Release mode)
2. ✅ Start it locally
3. ✅ Run benchmark tests (1000→6000 concurrent requests, 60 sec each)
4. ✅ Display formatted results with latency percentiles
5. ✅ Shut down cleanly

## What You'll See

### During Test
```
Concurrency Level: 1000 ================================================

Concurrency:     1000 requests
Duration:        60.0 seconds
Total Requests:  45000 (750 req/sec)
Success Rate:    100.0%

Latency (ms):
  P50:    5.45
  P90:   12.34
  P95:   18.76
  P99:   42.18
  Avg:    7.89
  Max:  156.34
```

### Final Summary Table
```
BENCHMARK SUMMARY - Metrics Server Performance
Concurrency  Req/Sec    Success %  P50 (ms)  P90 (ms)  P95 (ms)  P99 (ms)
─────────────────────────────────────────────────────────────────────────
1000         750        100.0      5.45      12.34     18.76     42.18
2000         1500       100.0      5.67      13.12     19.45     48.92
3000         2250       100.0      6.23      15.67     22.34     56.78
4000         3000       100.0      6.89      17.23     24.67     65.34
5000         3750       99.9       7.45      19.12     27.89     78.56
6000         4500       99.8       8.12      21.45     31.23     92.18

✓ Performance is acceptable across all concurrency levels.
```

## Benchmark Details

| Metric | Details |
|--------|---------|
| **Concurrency Levels** | 1000, 2000, 3000, 4000, 5000, 6000 simultaneous requests |
| **Duration Per Level** | 60 seconds |
| **Total Duration** | ~6 minutes |
| **Endpoints Tested** | /status, /models, /users (rotated) |
| **Latency Percentiles** | P50, P90, P95, P99, Min, Max, Avg |
| **Success Criteria** | P99 < 1 second, >99% success rate |

## Interpreting Results

### Good Performance
✅ P99 latency < 100ms  
✅ Throughput scales linearly  
✅ Success rate > 99.9%  
✅ No significant latency increase from level 1000→6000  

### Warning Signs
⚠️ P99 latency > 500ms at any level  
⚠️ P99 increases 2x+ from 1000→6000  
⚠️ Success rate < 99%  
⚠️ Frequent max latency spikes (GC pauses)  

### Troubleshooting

| Problem | Solution |
|---------|----------|
| Test times out | Increase `-Timeout` or reduce concurrency levels |
| All requests fail | Verify server running: `curl http://127.0.0.1:5555/health` |
| High error rate | Check Metrics Server logs, verify backend storage |
| Inconsistent results | Run multiple times, ensure no other workloads |

## Advanced Usage

### Run with External Server
```bash
export METRICS_SERVER_URL=http://your-server:5555
export METRICS_SERVER_BENCHMARK_REQUIRED=true
./run-tests.sh test MetricsServer_BenchmarkConcurrentRequests
```

### Run Only Benchmarks in Test Suite
```bash
./run-tests.sh --filter "TestCategory=Benchmark"
```

### Modify Concurrency Levels
Edit `MetricsServerBenchmarkTests.cs`:
```csharp
private static readonly int[] ConcurrencyLevels = [1000, 2000, 3000]; // Custom levels
```

### Adjust Sustained Load Duration
Edit `MetricsServerBenchmarkTests.cs`:
```csharp
private static readonly TimeSpan SustainedLoadDuration = TimeSpan.FromSeconds(30); // 30 sec instead of 60
```

## Performance Baselines

Expected P99 latencies for healthy Metrics Server:

| Concurrency | P99 Latency |
|-------------|-------------|
| 1000        | < 50ms      |
| 2000        | < 60ms      |
| 3000        | < 75ms      |
| 4000        | < 90ms      |
| 5000        | < 110ms     |
| 6000        | < 150ms     |

## Full Documentation

See [METRICS_BENCHMARK.md](./METRICS_BENCHMARK.md) for:
- Detailed output interpretation
- Environment variables
- Common issues & solutions
- How to run as part of CI/CD
- Performance tuning guide

## Next Steps

1. **Run the benchmark** to establish baseline performance
2. **Save the output** as a reference point
3. **Monitor over time** to detect regressions
4. **Investigate warnings** - P99 latency degradation indicates bottlenecks
5. **Optimize** based on results (see performance tuning guide)

---

**Need help?** Check METRICS_BENCHMARK.md or review test output logs.
