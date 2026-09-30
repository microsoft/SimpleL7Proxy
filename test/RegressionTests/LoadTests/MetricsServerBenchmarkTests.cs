using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Tokenomics;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Benchmark tests for the Metrics Server throughput and latency under sustained concurrent load.
/// Tests the server's ability to handle metric ingestion and queries at various concurrency levels.
/// </summary>
[TestClass]
public sealed class MetricsServerBenchmarkTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["metrics-server-throughput"] = new(
                "Performance",
                "Metrics server concurrent request handling",
                "Validates that the metrics server maintains acceptable latencies (p99 < 1 second) under high concurrent load."),
            ["metrics-server-latency-distribution"] = new(
                "Performance",
                "Metrics server latency percentiles",
                "Ensures 90th, 95th, and 99th percentile latencies scale acceptably with concurrent load.")
        };

    public TestContext? TestContext { get; set; }

    private static readonly int[] ConcurrencyLevels = [1000, 2000, 3000, 4000, 5000, 6000];
    private static readonly TimeSpan SustainedLoadDuration = TimeSpan.FromSeconds(60);

    [TestMethod]
    [RegressionTestCase("metrics-server-throughput", "MetricsServer sustains concurrent requests at scale", "Benchmark various concurrency levels for 60 seconds each.")]
    [TestCategory("Benchmark")]
    [TestCategory("Performance")]
    [TestCategory("Metrics")]
    [Timeout(480_000)] // 8 minutes: 6 levels × 60 sec + overhead
    public async Task MetricsServer_BenchmarkConcurrentRequests()
    {
        var metricsServerUrl = Environment.GetEnvironmentVariable("METRICS_SERVER_URL") ?? "http://127.0.0.1:5555";
        var skipIfUnavailable = !bool.TryParse(Environment.GetEnvironmentVariable("METRICS_SERVER_BENCHMARK_REQUIRED"), out var required) || !required;

        using var httpClient = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Verify server is running
        try
        {
            var healthResponse = await httpClient.GetAsync($"{metricsServerUrl}/health");
            Assert.IsTrue(healthResponse.IsSuccessStatusCode, $"Metrics server at {metricsServerUrl} is not healthy");
        }
        catch (Exception ex) when (skipIfUnavailable)
        {
            Assert.Inconclusive($"Metrics server not available at {metricsServerUrl}: {ex.Message}. Set METRICS_SERVER_URL and METRICS_SERVER_BENCHMARK_REQUIRED=true to run this benchmark.");
            return;
        }

        var results = new List<BenchmarkResult>();

        // Run concurrent transmitter + query benchmark with parallel tasks
        TestContext?.WriteLine("\n========== Concurrent Transmitter Benchmark ==========");
        var uploadResult = await UploadSampleMetricsAsync(httpClient, metricsServerUrl);
        TestContext?.WriteLine($"Upload Status: {uploadResult.StatusCode}");
        TestContext?.WriteLine($"Upload Success: {uploadResult.IsSuccess}");
        if (uploadResult.IsSuccess)
        {
            TestContext?.WriteLine($"Uploaded {uploadResult.RecordCount} sample metrics records");
            // Wait for background processor to ingest
            await Task.Delay(2000);
        }
        else
        {
            TestContext?.WriteLine($"Warning: Upload failed - {uploadResult.Error}");
            TestContext?.WriteLine($"Continuing benchmark anyway...");
        }

        foreach (var concurrency in ConcurrencyLevels)
        {
            TestContext?.WriteLine($"\n========== Concurrency Level: {concurrency} ==========");
            
            var result = await RunBenchmarkAsync(httpClient, metricsServerUrl, concurrency, SustainedLoadDuration);
            results.Add(result);

            PrintBenchmarkResult(result);

            // Small delay between runs to let server recover
            await Task.Delay(1000);
        }

        PrintSummaryTable(results);

        // Verify that p99 latency doesn't exceed acceptable threshold
        var maxAcceptableP99Latency = TimeSpan.FromSeconds(1.0);
        foreach (var result in results)
        {
            Assert.IsTrue(
                result.P99Latency <= maxAcceptableP99Latency,
                $"Concurrency {result.ConcurrentRequests}: P99 latency {result.P99Latency.TotalMilliseconds:F1}ms exceeded threshold {maxAcceptableP99Latency.TotalMilliseconds:F1}ms");
        }
    }

    private async Task<BenchmarkResult> RunBenchmarkAsync(
        HttpClient httpClient,
        string metricsServerUrl,
        int concurrentRequests,
        TimeSpan duration)
    {
        var latencies = new ConcurrentBag<TimeSpan>();
        var errors = new ConcurrentBag<string>();
        var requestCount = 0L;
        var failureCount = 0L;

        var stopwatch = Stopwatch.StartNew();
        var endTime = stopwatch.Elapsed + duration;

        // Semaphore to control concurrency
        using var semaphore = new SemaphoreSlim(concurrentRequests, concurrentRequests);
        var tasks = new List<Task>();

        while (stopwatch.Elapsed < endTime)
        {
            // Queue up to N concurrent requests
            for (int i = 0; i < concurrentRequests && stopwatch.Elapsed < endTime; i++)
            {
                await semaphore.WaitAsync();
                
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var requestStopwatch = Stopwatch.StartNew();
                        
                        // Query metrics lookup endpoint with user and model parameters
                        var reqNum = Interlocked.Increment(ref requestCount);
                        var userId = $"user-{reqNum % 10}";
                        var model = new[] { "gpt-4", "gpt-3.5-turbo", "claude-3", "mistral-large" }[reqNum % 4];
                        var lookupUri = new Uri($"{metricsServerUrl}/tokenomics/metrics/lookup?u={Uri.EscapeDataString(userId)}&m={Uri.EscapeDataString(model)}");

                        var response = await httpClient.GetAsync(lookupUri);
                        requestStopwatch.Stop();

                        latencies.Add(requestStopwatch.Elapsed);

                        if (!response.IsSuccessStatusCode)
                        {
                            Interlocked.Increment(ref failureCount);
                            errors.Add($"lookup: {response.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failureCount);
                        errors.Add($"Exception: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            // Wait a bit before spawning next batch to maintain steady concurrency
            await Task.Delay(100);
        }

        // Wait for all pending requests to complete (with a timeout)
        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();

        return new BenchmarkResult
        {
            ConcurrentRequests = concurrentRequests,
            Duration = stopwatch.Elapsed,
            TotalRequests = requestCount,
            FailedRequests = failureCount,
            Latencies = latencies.ToList(),
            Errors = errors.ToList()
        };
    }

    private void PrintBenchmarkResult(BenchmarkResult result)
    {
        var sortedLatencies = result.Latencies.OrderBy(l => l.TotalMilliseconds).ToList();
        var p50 = GetPercentile(sortedLatencies, 50);
        var p90 = GetPercentile(sortedLatencies, 90);
        var p95 = GetPercentile(sortedLatencies, 95);
        var p99 = GetPercentile(sortedLatencies, 99);
        var avgLatency = TimeSpan.FromMilliseconds(result.Latencies.Average(l => l.TotalMilliseconds));
        var maxLatency = result.Latencies.Max();
        var minLatency = result.Latencies.Min();
        var throughputRps = result.TotalRequests / result.Duration.TotalSeconds;
        var successRate = result.TotalRequests > 0 ? (100.0 * (result.TotalRequests - result.FailedRequests)) / result.TotalRequests : 0;

        TestContext?.WriteLine($"Concurrency:     {result.ConcurrentRequests,5} requests");
        TestContext?.WriteLine($"Duration:        {result.Duration.TotalSeconds,5:F1} seconds");
        TestContext?.WriteLine($"Total Requests:  {result.TotalRequests,5} ({throughputRps:F0} req/sec)");
        TestContext?.WriteLine($"Success Rate:    {successRate,5:F1}%");
        TestContext?.WriteLine($"Failed Requests: {result.FailedRequests,5}");
        TestContext?.WriteLine("");
        TestContext?.WriteLine($"Latency (ms):");
        TestContext?.WriteLine($"  Min:  {minLatency.TotalMilliseconds,8:F2}");
        TestContext?.WriteLine($"  P50:  {p50.TotalMilliseconds,8:F2}");
        TestContext?.WriteLine($"  P90:  {p90.TotalMilliseconds,8:F2}");
        TestContext?.WriteLine($"  P95:  {p95.TotalMilliseconds,8:F2}");
        TestContext?.WriteLine($"  P99:  {p99.TotalMilliseconds,8:F2}");
        TestContext?.WriteLine($"  Avg:  {avgLatency.TotalMilliseconds,8:F2}");
        TestContext?.WriteLine($"  Max:  {maxLatency.TotalMilliseconds,8:F2}");

        result.P50Latency = p50;
        result.P90Latency = p90;
        result.P95Latency = p95;
        result.P99Latency = p99;
        result.AvgLatency = avgLatency;
        result.ThroughputRps = throughputRps;
        result.SuccessRate = successRate;
    }

    private void PrintSummaryTable(List<BenchmarkResult> results)
    {
        TestContext?.WriteLine("\n" + new string('=', 110));
        TestContext?.WriteLine("BENCHMARK SUMMARY - Metrics Server Performance");
        TestContext?.WriteLine(new string('=', 110));
        TestContext?.WriteLine(
            string.Format("{0,-12} {1,-10} {2,-11} {3,-10} {4,-10} {5,-10} {6,-10}", 
                "Concurrency", "Req/Sec", "Success %", "P50 (ms)", "P90 (ms)", "P95 (ms)", "P99 (ms)"));
        TestContext?.WriteLine(new string('-', 110));

        foreach (var result in results)
        {
            TestContext?.WriteLine(
                string.Format("{0,-12} {1,-10:F0} {2,-11:F1} {3,-10:F2} {4,-10:F2} {5,-10:F2} {6,-10:F2}",
                    result.ConcurrentRequests, result.ThroughputRps, result.SuccessRate, 
                    result.P50Latency.TotalMilliseconds, result.P90Latency.TotalMilliseconds, 
                    result.P95Latency.TotalMilliseconds, result.P99Latency.TotalMilliseconds));
        }

        TestContext?.WriteLine(new string('=', 110));

        // Identify trends
        var p99Latencies = results.Select(r => r.P99Latency.TotalMilliseconds).ToList();
        var hasRegression = p99Latencies.Count > 1 && p99Latencies[p99Latencies.Count - 1] > p99Latencies[0] * 2;

        if (hasRegression)
        {
            TestContext?.WriteLine("⚠️  WARNING: P99 latency shows degradation at higher concurrency levels.");
        }
        else
        {
            TestContext?.WriteLine("✓ Performance is acceptable across all concurrency levels.");
        }

        TestContext?.WriteLine("");
    }

    private TimeSpan GetPercentile(List<TimeSpan> sortedLatencies, int percentile)
    {
        if (sortedLatencies.Count == 0)
            return TimeSpan.Zero;

        var index = (int)Math.Ceiling(percentile / 100.0 * sortedLatencies.Count) - 1;
        index = Math.Clamp(index, 0, sortedLatencies.Count - 1);
        return sortedLatencies[index];
    }

    private sealed class BenchmarkResult
    {
        public int ConcurrentRequests { get; set; }
        public TimeSpan Duration { get; set; }
        public long TotalRequests { get; set; }
        public long FailedRequests { get; set; }
        public List<TimeSpan> Latencies { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        
        public TimeSpan P50Latency { get; set; }
        public TimeSpan P90Latency { get; set; }
        public TimeSpan P95Latency { get; set; }
        public TimeSpan P99Latency { get; set; }
        public TimeSpan AvgLatency { get; set; }
        public double ThroughputRps { get; set; }
        public double SuccessRate { get; set; }
    }

    private async Task<UploadResult> UploadSampleMetricsAsync(HttpClient queryHttpClient, string metricsServerUrl)
    {
        try
        {
            var metricsServerUri = new Uri($"{metricsServerUrl}/tokenomics/metrics/upload");
            
            // Generate initial batch pool
            var initialBatchCount = 5;
            var recordsPerBatch = 50;
            var batchQueue = new ConcurrentQueue<(string BatchId, string CsvData)>();
            
            TestContext?.WriteLine($"\nGenerating initial {initialBatchCount} batches ({recordsPerBatch} records each)");
            for (int i = 0; i < initialBatchCount; i++)
            {
                var batchId = Guid.NewGuid().ToString("N");
                var csvData = GenerateSampleTokenomicsData(recordCount: recordsPerBatch);
                batchQueue.Enqueue((batchId, csvData));
            }

            // Track aggregate stats across all transmitters
            var aggregateStats = new AggregateTransmissionStats();
            var transmitterStatsList = new ConcurrentBag<TransmitterStats>();
            var allTasksCts = new CancellationTokenSource();
            var transmissionStartTime = DateTime.UtcNow;
            var maxDurationSeconds = 300; // 5 minutes total run
            var maxTasksToCreate = 5;

            TestContext?.WriteLine($"Creating transmitter tasks progressively, one every 60 seconds");
            TestContext?.WriteLine($"Max tasks: {maxTasksToCreate}, Total duration: {maxDurationSeconds} seconds");
            TestContext?.WriteLine($"Outputting stats every 10 seconds");

            var transmitterTasks = new List<Task<TransmitterStats>>();
            var taskSpawnerCts = new CancellationTokenSource();
            
            // Task spawner that creates a new transmitter every 60 seconds
            var spawnerTask = Task.Run(async () =>
            {
                var taskCount = 0;
                while (taskCount < maxTasksToCreate && !taskSpawnerCts.Token.IsCancellationRequested)
                {
                    var currentTaskId = taskCount;
                    taskCount++;

                    // Instantiate and start a new transmitter task
                    var task = Task.Run(async () =>
                    {
                        var replicaId = $"replica-{currentTaskId:00}";
                        using var transmitterHttpClient = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
                        {
                            Timeout = TimeSpan.FromSeconds(10)
                        };

                        var transmitter = new TokenomicsTransmitter(metricsServerUri, replicaId, transmitterHttpClient);
                        var stats = new TransmitterStats { ReplicaId = replicaId };
                        transmitterStatsList.Add(stats);

                        transmitter.Start();

                        var startTime = DateTime.UtcNow;
                        var queryTasks = new List<Task>();
                        var batchIdx = 0;

                        // Use PeriodicTimer for 1 batch per second feeding
                        using var feedTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                        
                        try
                        {
                            while (await feedTimer.WaitForNextTickAsync(taskSpawnerCts.Token).ConfigureAwait(false))
                            {
                                if (DateTime.UtcNow >= transmissionStartTime.AddSeconds(maxDurationSeconds))
                                {
                                    break;
                                }

                                // Try to get a batch from the queue
                                if (batchQueue.TryDequeue(out var batch))
                                {
                                    var (batchId, csvData) = batch;
                                    transmitter.SubmitBatch(batchId, csvData);
                                    stats.TotalBatchesSubmitted++;
                                    stats.TotalRecords += csvData.Split('\n').Length - 2;
                                    Interlocked.Increment(ref aggregateStats.TotalBatchesSubmitted);
                                    
                                    // Generate a new batch and enqueue it
                                    var newBatchId = Guid.NewGuid().ToString("N");
                                    var newCsvData = GenerateSampleTokenomicsData(recordCount: recordsPerBatch);
                                    batchQueue.Enqueue((newBatchId, newCsvData));
                                    Interlocked.Increment(ref aggregateStats.TotalBatchesGenerated);
                                    
                                    // Fire off queries interspersed (every other batch)
                                    if (batchIdx % 2 == 0)
                                    {
                                        var currentBatchIdx = batchIdx;
                                        var queryTask = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                var userId = $"user-{currentBatchIdx % 10}";
                                                var model = new[] { "gpt-4", "gpt-3.5-turbo", "claude-3", "mistral-large" }[currentBatchIdx % 4];
                                                var lookupUri = new Uri($"{metricsServerUrl}/tokenomics/metrics/lookup?u={Uri.EscapeDataString(userId)}&m={Uri.EscapeDataString(model)}");
                                                var response = await queryHttpClient.GetAsync(lookupUri);
                                                if (response.IsSuccessStatusCode)
                                                {
                                                    Interlocked.Increment(ref aggregateStats.TotalQuerySuccesses);
                                                }
                                                Interlocked.Increment(ref aggregateStats.TotalQueryRequests);
                                            }
                                            catch
                                            {
                                                Interlocked.Increment(ref aggregateStats.TotalQueryRequests);
                                            }
                                        });
                                        queryTasks.Add(queryTask);
                                    }
                                    
                                    batchIdx++;
                                }
                            }
                        }
                        finally
                        {
                            feedTimer.Dispose();
                        }

                        // Wait for all queries to complete for this task
                        await Task.WhenAll(queryTasks);

                        // Wait for all batches to be acknowledged (up to 30 seconds)
                        var timeout = DateTime.UtcNow.AddSeconds(30);
                        var acknowledgedCount = 0;
                        while (DateTime.UtcNow < timeout)
                        {
                            var pendingCount = transmitter.GetPendingBatchCount();
                            if (pendingCount == 0)
                            {
                                acknowledgedCount = stats.TotalBatchesSubmitted;
                                break;
                            }
                            await Task.Delay(500);
                        }

                        var failedCount = stats.TotalBatchesSubmitted - acknowledgedCount;
                        stats.AcknowledgedBatches = acknowledgedCount;
                        stats.FailedBatches = failedCount;
                        stats.ElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;

                        // Stop transmitter gracefully
                        var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await transmitter.StopAsync(stopCts.Token);

                        return stats;
                    });

                    transmitterTasks.Add(task);

                    if (taskCount < maxTasksToCreate)
                    {
                        // Wait 60 seconds before spawning next task
                        await Task.Delay(TimeSpan.FromSeconds(60), taskSpawnerCts.Token).ConfigureAwait(false);
                    }
                }
            }, taskSpawnerCts.Token);

            // Stats reporter that outputs every 10 seconds (both Console and file for real-time monitoring)
            var statsLogFile = "/tmp/benchmark-stats.log";
            var statsReporterTask = Task.Run(async () =>
            {
                // Clear log file at start
                try { File.WriteAllText(statsLogFile, $"[BENCHMARK START] {DateTime.UtcNow:O}\n"); } catch { }

                using var statsTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                var lastReportedSubmitted = 0;
                var lastReportedAcked = 0;

                try
                {
                    while (await statsTimer.WaitForNextTickAsync(allTasksCts.Token).ConfigureAwait(false))
                    {
                        if (DateTime.UtcNow >= transmissionStartTime.AddSeconds(maxDurationSeconds))
                        {
                            break;
                        }

                        var elapsed = (DateTime.UtcNow - transmissionStartTime).TotalSeconds;
                        var activeTransmitters = transmitterStatsList.Count;
                        var totalPending = 0;

                        foreach (var stat in transmitterStatsList)
                        {
                            totalPending += stat.TotalBatchesSubmitted - stat.AcknowledgedBatches;
                        }

                        var throughputBps = aggregateStats.TotalBatchesSubmitted > 0 
                            ? aggregateStats.TotalBatchesSubmitted / elapsed 
                            : 0;
                        
                        var querySuccessRate = aggregateStats.TotalQueryRequests > 0 
                            ? (100.0 * aggregateStats.TotalQuerySuccesses) / aggregateStats.TotalQueryRequests 
                            : 0;

                        var lines = new List<string>
                        {
                            $"\n[{elapsed:F1}s] TRANSMISSION STATS - Active Transmitters: {activeTransmitters}",
                            $"  Generated: {aggregateStats.TotalBatchesGenerated} | Submitted: {aggregateStats.TotalBatchesSubmitted} | Acked: {aggregateStats.TotalBatchesAcknowledged} | Pending: {totalPending}",
                            $"  Throughput: {throughputBps:F2} batches/sec | Queries: {aggregateStats.TotalQueryRequests} requests, {aggregateStats.TotalQuerySuccesses} successful ({querySuccessRate:F1}%)"
                        };

                        // Per-transmitter details
                        foreach (var stat in transmitterStatsList.OrderBy(s => s.ReplicaId))
                        {
                            var pending = stat.TotalBatchesSubmitted - stat.AcknowledgedBatches;
                            lines.Add($"    {stat.ReplicaId}: submitted={stat.TotalBatchesSubmitted}, acked={stat.AcknowledgedBatches}, pending={pending}");
                        }

                        // Write to stderr (unbuffered) for real-time visibility during async test execution
                        foreach (var line in lines)
                        {
                            Console.Error.WriteLine(line);
                            Console.Error.Flush();
                            // Also log to file as backup
                            try { File.AppendAllText(statsLogFile, line + "\n"); } catch { }
                        }

                        lastReportedSubmitted = aggregateStats.TotalBatchesSubmitted;
                        lastReportedAcked = aggregateStats.TotalBatchesAcknowledged;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown
                }
                finally
                {
                    statsTimer.Dispose();
                    try { File.AppendAllText(statsLogFile, $"\n[BENCHMARK END] {DateTime.UtcNow:O}\n"); } catch { }
                }
            }, allTasksCts.Token);

            // Wait for all transmitter tasks to complete (or timeout)
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(transmitterTasks),
                    Task.Delay(TimeSpan.FromSeconds(maxDurationSeconds + 60))
                ).ConfigureAwait(false);
            }
            finally
            {
                // Signal completion
                taskSpawnerCts.Cancel();
                allTasksCts.Cancel();
            }

            // Wait for spawner and reporter to finish
            await Task.WhenAll(spawnerTask, statsReporterTask).ConfigureAwait(false);

            // Collect final stats
            var allStats = await Task.WhenAll(transmitterTasks);
            
            // Update aggregate with final acknowledgements
            foreach (var stat in allStats)
            {
                aggregateStats.TotalBatchesAcknowledged += stat.AcknowledgedBatches;
            }

            // Output final stats
            PrintTransmitterStats(allStats, aggregateStats);

            // Determine overall success
            var totalSubmitted = allStats.Sum(s => s.TotalBatchesSubmitted);
            var totalAcknowledged = allStats.Sum(s => s.AcknowledgedBatches);
            var totalRecords = allStats.Sum(s => s.TotalRecords);
            var overallSuccess = totalSubmitted > 0 && totalAcknowledged == totalSubmitted;

            return new UploadResult
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                IsSuccess = overallSuccess,
                RecordCount = totalRecords,
                Error = overallSuccess ? null : $"Acknowledged {totalAcknowledged}/{totalSubmitted} batches"
            };
        }
        catch (Exception ex)
        {
            TestContext?.WriteLine($"Upload exception: {ex}");
            return new UploadResult
            {
                StatusCode = System.Net.HttpStatusCode.InternalServerError,
                IsSuccess = false,
                RecordCount = 0,
                Error = ex.Message
            };
        }
    }

    private void PrintTransmitterStats(TransmitterStats[] stats, AggregateTransmissionStats aggregateStats)
    {
        TestContext?.WriteLine("\n" + new string('=', 130));
        TestContext?.WriteLine("FINAL TRANSMITTER STATS - Stepped Concurrent Batch Submission with Interspersed Queries");
        TestContext?.WriteLine(new string('=', 130));
        TestContext?.WriteLine(
            string.Format("{0,-15} {1,-10} {2,-12} {3,-12} {4,-10} {5,-10}",
                "Replica ID", "Batches", "Records", "Acked", "Failed", "Duration (s)"));
        TestContext?.WriteLine(new string('-', 130));

        foreach (var stat in stats.OrderBy(s => s.ReplicaId))
        {
            TestContext?.WriteLine(
                string.Format("{0,-15} {1,-10} {2,-12} {3,-12} {4,-10} {5,-10:F2}",
                    stat.ReplicaId, stat.TotalBatchesSubmitted, stat.TotalRecords, 
                    stat.AcknowledgedBatches, stat.FailedBatches, stat.ElapsedSeconds));
        }

        TestContext?.WriteLine(new string('-', 130));
        
        var totalBatches = stats.Sum(s => s.TotalBatchesSubmitted);
        var totalRecords = stats.Sum(s => s.TotalRecords);
        var totalAcknowledged = stats.Sum(s => s.AcknowledgedBatches);
        var totalFailed = stats.Sum(s => s.FailedBatches);
        var avgDuration = stats.Length > 0 ? stats.Average(s => s.ElapsedSeconds) : 0;

        TestContext?.WriteLine(
            string.Format("{0,-15} {1,-10} {2,-12} {3,-12} {4,-10} {5,-10:F2}",
                "TOTAL", totalBatches, totalRecords, totalAcknowledged, totalFailed, avgDuration));
        
        TestContext?.WriteLine(new string('-', 130));
        
        var querySuccessRate = aggregateStats.TotalQueryRequests > 0 
            ? (100.0 * aggregateStats.TotalQuerySuccesses) / aggregateStats.TotalQueryRequests 
            : 0;
        TestContext?.WriteLine($"Batches Generated: {aggregateStats.TotalBatchesGenerated} | Queries: {aggregateStats.TotalQueryRequests} requests, {aggregateStats.TotalQuerySuccesses} successful ({querySuccessRate:F1}%)");
        
        TestContext?.WriteLine(new string('=', 130));
        
        if (totalFailed == 0)
        {
            TestContext?.WriteLine("✓ All batches acknowledged successfully - Metrics server keeping up!");
        }
        else
        {
            TestContext?.WriteLine($"⚠️  {totalFailed} batches failed to be acknowledged - Metrics server falling behind!");
        }

        TestContext?.WriteLine("");
    }

    private sealed class TransmitterStats
    {
        public string ReplicaId { get; set; } = "";
        public int TotalBatchesSubmitted { get; set; }
        public int TotalRecords { get; set; }
        public int AcknowledgedBatches { get; set; }
        public int FailedBatches { get; set; }
        public double ElapsedSeconds { get; set; }
    }

    private sealed class AggregateTransmissionStats
    {
        public int TotalBatchesGenerated;
        public int TotalBatchesSubmitted;
        public int TotalBatchesAcknowledged;
        public int TotalQueryRequests;
        public int TotalQuerySuccesses;
    }

    private string GenerateSampleTokenomicsData(int recordCount)
    {
        // Follow the exact same pattern as TokenRollupCollector.CollapseCurrentMetrics():
        // Header + \n + metric1.ToCSV() + metric2.ToCSV() + ...
        var sb = new StringBuilder();
        sb.Append(PendingMetric.CsvHeader).Append('\n');

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var random = new Random(42); // Deterministic seed for reproducibility

        for (int i = 0; i < recordCount; i++)
        {
            var userId = $"user-{i % 10}";
            var model = new[] { "gpt-4", "gpt-3.5-turbo", "claude-3", "mistral-large" }[i % 4];
            var inputTokens = random.Next(100, 2000);
            var outputTokens = random.Next(50, 1000);
            var cachedTokens = random.Next(0, 500);
            var isJailbreak = random.NextDouble() < 0.01;
            var isFiltered = random.NextDouble() < 0.02;
            var statusCode = new[] { 200, 200, 200, 429, 500 }[random.Next(5)];
            var latencyMs = random.NextDouble() * 500; // 0-500ms
            var timestampUtc = DateTime.UtcNow.AddSeconds(-random.Next(0, 60));

            // Create PendingMetric using the full constructor
            var metric = new PendingMetric(
                userId: userId,
                model: model,
                inputTokens: inputTokens,
                outputTokens: outputTokens,
                cachedTokens: cachedTokens,
                isJailbreakDetected: isJailbreak,
                isContentFiltered: isFiltered,
                day: today,
                statusCode: statusCode,
                latencyMs: latencyMs,
                timestampUtc: timestampUtc);

            // Append using ToCSV() like the production code does
            sb.Append(metric.ToCSV());
        }

        return sb.ToString();
    }

    private sealed class UploadResult
    {
        public System.Net.HttpStatusCode StatusCode { get; set; }
        public bool IsSuccess { get; set; }
        public int RecordCount { get; set; }
        public string? Error { get; set; }
    }
}
