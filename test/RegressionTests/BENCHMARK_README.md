# Metrics Server Benchmark Test Suite - Summary

## ✅ What Was Created

### 1. Benchmark Test Class
**File:** `test/RegressionTests/LoadTests/MetricsServerBenchmarkTests.cs` (233 lines)

**Test Method:** `MetricsServer_BenchmarkConcurrentRequests()`
- Category: `Benchmark`, `Performance`, `Metrics`
- Attributes: `[RegressionTestCase]`, `[TestMethod]`, `[Timeout(300_000ms)]`

**What it does:**
- Tests 6 concurrency levels: 1000, 2000, 3000, 4000, 5000, 6000 simultaneous requests
- Sustains load for 60 seconds per level
- Calculates latency percentiles: P50, P90, P95, P99, Min, Max, Avg
- Measures throughput (requests/sec) and success rate
- Alternates between 3 endpoints (/status, /models, /users) to simulate realistic workload
- Outputs formatted results per level + summary comparison table

**Success Criteria:**
- ✓ P99 latency ≤ 1000ms across all levels
- ✓ >99% request success rate
- ✓ No excessive latency degradation at higher concurrency

---

### 2. Automated Benchmark Runner
**File:** `test/RegressionTests/run-metrics-benchmark.sh` (executable)

**What it does:**
- Builds Metrics Server in Release mode
- Starts local Metrics Server on port 5555
- Runs the benchmark test against it
- Captures and displays formatted results
- Gracefully shuts down the server

**Usage:**
```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
./run-metrics-benchmark.sh
```

---

### 3. Documentation Files

#### `BENCHMARK_QUICKSTART.md` - Get Started in 30 Seconds
- Quick start command
- Sample output
- Performance interpretation guide
- Common troubleshooting

#### `METRICS_BENCHMARK.md` - Complete Reference
- Detailed running instructions (3 options)
- Output format explanation
- Success criteria and baselines
- Troubleshooting section
- Performance tuning guidance
- Expected baseline results table

---

## 🚀 Quick Start

### Simplest Way to Run
```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
chmod +x run-metrics-benchmark.sh    # Make executable (already done)
./run-metrics-benchmark.sh            # Auto-starts server, runs benchmark
```

### Manual Execution (with running Metrics Server)
```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests

# Set environment if server is on different host
export METRICS_SERVER_URL=http://your-host:5555
export METRICS_SERVER_BENCHMARK_REQUIRED=true

# Run the specific benchmark
./run-tests.sh test MetricsServer_BenchmarkConcurrentRequests
```

### As Part of Full Test Suite
```bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
./run-tests.sh all    # Includes benchmark if server available
```

---

## 📊 Expected Output

Each concurrency level produces:
```
Concurrency:     1000 requests
Duration:        60.0 seconds
Total Requests:  45000 (750 req/sec)
Success Rate:    100.0%

Latency (ms):
  Min:        1.23
  P50:        5.45
  P90:       12.34
  P95:       18.76
  P99:       42.18
  Avg:        7.89
  Max:      156.34
```

Final summary table compares all levels:
```
Concurrency  Req/Sec    Success %  P50 (ms)  P90 (ms)  P95 (ms)  P99 (ms)
─────────────────────────────────────────────────────────────────────────
1000         750        100.0      5.45      12.34     18.76     42.18
2000         1500       100.0      5.67      13.12     19.45     48.92
...
6000         4500       99.8       8.12      21.45     31.23     92.18
```

---

## 🔍 What Gets Tested

| Aspect | Details |
|--------|---------|
| **Concurrency** | 1000→6000 simultaneous connections |
| **Duration** | 60 seconds per level (~6 min total) |
| **Endpoints** | /status, /models, /users (rotated pattern) |
| **Metrics** | Throughput, latency percentiles, success rate |
| **Assertions** | P99 < 1s, success > 99%, no 2x degradation |

---

## ✨ Key Features

✅ **Regression Features Tracked:**
- `metrics-server-throughput` - Concurrent request handling at scale
- `metrics-server-latency-distribution` - Percentile latency tracking

✅ **Environment Variables:**
- `METRICS_SERVER_URL` - Where benchmark connects (default: http://127.0.0.1:5555)
- `METRICS_SERVER_BENCHMARK_REQUIRED` - Fail if unavailable (default: false = skip)
- `ASPNETCORE_URLS` - Set by run-metrics-benchmark.sh for server startup

✅ **Flexible Execution:**
- Run standalone via script
- Run in test suite (included in "all" category)
- Run with external server
- Filter by category: `--filter "TestCategory=Benchmark"`

---

## 📝 How to Customize

### Change Concurrency Levels
Edit `MetricsServerBenchmarkTests.cs`:
```csharp
private static readonly int[] ConcurrencyLevels = [500, 1000, 2000];
```

### Change Load Duration
Edit `MetricsServerBenchmarkTests.cs`:
```csharp
private static readonly TimeSpan SustainedLoadDuration = TimeSpan.FromSeconds(30);
```

### Change Metrics Server Port
When running manually:
```bash
export METRICS_SERVER_URL=http://127.0.0.1:9999
export METRICS_SERVER_BENCHMARK_REQUIRED=true
./run-tests.sh test MetricsServer_BenchmarkConcurrentRequests
```

When running via script:
```bash
METRICS_SERVER_PORT=9999 ./run-metrics-benchmark.sh
```

---

## 📚 Documentation Map

| Document | Purpose |
|----------|---------|
| [BENCHMARK_QUICKSTART.md](./BENCHMARK_QUICKSTART.md) | 30-sec quick start, basic troubleshooting |
| [METRICS_BENCHMARK.md](./METRICS_BENCHMARK.md) | Complete reference, all options, baselines |
| This file | Overview, what was created, how to use |

---

## 🔧 Technical Details

### Benchmark Implementation
- Uses `SemaphoreSlim(concurrency)` to control concurrent requests
- Collects per-request latencies in `ConcurrentBag<TimeSpan>`
- Calculates percentiles via sorted list indexing
- Rotates between 3 endpoints via modulo operator
- Handles failures gracefully with error logging
- Timeout: 300 seconds per test method

### Test Infrastructure
- Framework: MSTest with `[RegressionTestCase]` attributes
- Category: Benchmark (for filtering)
- Supports environment variable configuration
- Graceful skip if server unavailable (unless required)

### Integration Points
- Integrated with `run-tests.sh` (use `--filter "TestCategory=Benchmark"`)
- Standalone via `run-metrics-benchmark.sh`
- Results go to standard MSTest/TRX output
- Works with existing test output formatter

---

## ⏱️ Time Expectations

| Task | Estimated Time |
|------|-----------------|
| First run with script | 6-8 min (includes build) |
| Subsequent runs | 6-7 min (no rebuild) |
| Full test suite with benchmark | 15-20 min |
| Analysis & interpretation | 5-10 min |

---

## 🎯 Next Steps

1. **Run the benchmark** using the Quick Start command
2. **Save baseline results** as reference
3. **Monitor over time** for regressions
4. **Investigate warnings** if P99 latency exceeds 1 second
5. **Optimize** based on bottlenecks identified

---

## 📞 Support

**Issues with the benchmark?**

1. Check [BENCHMARK_QUICKSTART.md](./BENCHMARK_QUICKSTART.md) for common fixes
2. Review [METRICS_BENCHMARK.md](./METRICS_BENCHMARK.md) troubleshooting section
3. Verify Metrics Server is running: `curl http://127.0.0.1:5555/health`
4. Check test output in TRX report at: `results/history/{TIMESTAMP}/`

**Questions about implementation?**

- Review `MetricsServerBenchmarkTests.cs` for detailed comments
- Check existing load tests in `LoadTests/` directory
- See main test documentation in `docs/reference/`

---

## 📦 Files Added

```
test/RegressionTests/
├── LoadTests/
│   └── MetricsServerBenchmarkTests.cs    (233 lines - benchmark implementation)
├── run-metrics-benchmark.sh               (executable - automated runner)
├── BENCHMARK_QUICKSTART.md                (quick reference)
└── METRICS_BENCHMARK.md                   (complete documentation)
```

All files compile without errors ✓
All files are ready for immediate use ✓

---

**Status: ✅ READY TO USE**
