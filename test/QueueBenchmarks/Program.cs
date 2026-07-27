using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleL7Proxy;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Queue;

namespace QueueBenchmarks;

internal static class Program
{
    private const int LatencySampleCapacity = 1_000_000;
    private const int LatencySampleRate = 64;

    private static async Task<int> Main(string[] args)
    {
        BenchmarkOptions options;
        try
        {
            options = BenchmarkOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            BenchmarkOptions.PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            BenchmarkOptions.PrintUsage();
            return 0;
        }

        ThreadPool.GetMinThreads(out var minimumWorkerThreads, out var minimumIoThreads);
        ThreadPool.SetMinThreads(Math.Max(minimumWorkerThreads, options.Workers + 4), minimumIoThreads);

        Console.WriteLine($"label={options.Label}");
        Console.WriteLine($"runtime={Environment.Version} os={Environment.OSVersion} logical_cpus={Environment.ProcessorCount}");
        Console.WriteLine(
            $"workers={options.Workers} duration_seconds={options.Duration.TotalSeconds:F0} " +
            $"capacity={options.Capacity} probe_every={options.ProbeEvery}");
        Console.WriteLine($"worker_mix={DescribeWorkerMix(options.Workers)}");
        Console.WriteLine("request_mix=probe:configured,priority1-4:round-robin,priority2:alternating");

        var result = await RunForDurationAsync(options).ConfigureAwait(false);
        Console.WriteLine(result.ToOutput());
        return 0;
    }

    private static async Task<RunResult> RunForDurationAsync(BenchmarkOptions options)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var queue = new ConcurrentPriQueue<BenchmarkItem>(
            Options.Create(new ProxyConfig { MaxQueueLength = options.Capacity }),
            NullLogger<ConcurrentPriQueue<BenchmarkItem>>.Instance);
        using var producerCancellationSource = new CancellationTokenSource();
        using var signalerCancellationSource = new CancellationTokenSource();
        var signalerTask = Task.Run(() => queue.SignalWorker(signalerCancellationSource.Token));
        var state = new RunState(options.Workers);
        var workerStartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workerTasks = new Task[options.Workers];
        for (var workerIndex = 0; workerIndex < workerTasks.Length; workerIndex++)
        {
            var capturedIndex = workerIndex;
            var preferredPriority = GetWorkerPriority(workerIndex);
            workerTasks[workerIndex] = ConsumeAsync(
                queue,
                state,
                capturedIndex,
                preferredPriority,
                workerStartGate.Task,
                signalerCancellationSource.Token);
        }

        using var producerStartGate = new ManualResetEventSlim(false);
        var producerTask = Task.Factory.StartNew(
            () => Produce(
                queue,
                state,
                options.ProbeEvery,
                producerStartGate,
                producerCancellationSource.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        var durationTask = Task.Delay(options.Duration);
        producerStartGate.Set();

        var prefillTarget = options.Capacity;
        await WaitUntilAsync(
            () => queue.thrdSafeCount >= prefillTarget,
            options.ShutdownTimeout,
            "producer to prefill the queue").ConfigureAwait(false);
        workerStartGate.TrySetResult();

        await durationTask.ConfigureAwait(false);
        producerCancellationSource.Cancel();
        await producerTask.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);

        stopwatch.Stop();
        var cpuTime = process.TotalProcessorTime - cpuBefore;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var accepted = Interlocked.Read(ref state.Accepted);
        var consumedAtDeadline = Interlocked.Read(ref state.Consumed);
        var backlogAtDeadline = queue.thrdSafeCount;

        await WaitUntilAsync(
            () => queue.thrdSafeCount == 0 && state.WaitingWorkerCount() == options.Workers,
            options.ShutdownTimeout,
            "accepted work to drain and workers to return to the queue").ConfigureAwait(false);

        signalerCancellationSource.Cancel();
        await signalerTask.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);
        await Task.WhenAll(workerTasks).WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);

        var finalConsumed = Interlocked.Read(ref state.Consumed);
        if (finalConsumed != accepted)
        {
            throw new InvalidOperationException(
                $"Queue lost work: accepted={accepted}, consumed={finalConsumed}.");
        }

        var queueDelayTicks = state.GetLatencySamples();
        Array.Sort(queueDelayTicks);
        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        var cpuUtilization = cpuTime.TotalMilliseconds / stopwatch.Elapsed.TotalMilliseconds /
            Environment.ProcessorCount * 100.0;

        return new RunResult(
            stopwatch.Elapsed.TotalMilliseconds,
            accepted,
            consumedAtDeadline,
            backlogAtDeadline,
            finalConsumed - consumedAtDeadline,
            Interlocked.Read(ref state.AdmissionRetries),
            accepted / elapsedSeconds,
            consumedAtDeadline / elapsedSeconds,
            queueDelayTicks.Length,
            ToMicroseconds(Percentile(queueDelayTicks, 0.50)),
            ToMicroseconds(Percentile(queueDelayTicks, 0.95)),
            ToMicroseconds(Percentile(queueDelayTicks, 0.99)),
            ToMicroseconds(queueDelayTicks[^1]),
            allocatedBytes / (double)accepted,
            cpuUtilization);
    }

    private static async Task ConsumeAsync(
        ConcurrentPriQueue<BenchmarkItem> queue,
        RunState state,
        int workerIndex,
        int preferredPriority,
        Task workerStartTask,
        CancellationToken signalerCancellationToken)
    {
        await workerStartTask.ConfigureAwait(false);

        while (true)
        {
            var dequeueTask = queue.DequeueAsync(preferredPriority);
            Volatile.Write(ref state.WaitingWorkers[workerIndex], 1);
            BenchmarkItem item;
            try
            {
                item = await dequeueTask.ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (signalerCancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref state.WaitingWorkers[workerIndex], 0);
                return;
            }
            Volatile.Write(ref state.WaitingWorkers[workerIndex], 0);

            if (item.Sequence % LatencySampleRate == 0)
            {
                state.RecordLatency(Stopwatch.GetTimestamp() - item.EnqueuedTimestamp);
            }

            Interlocked.Increment(ref state.Consumed);
        }
    }

    private static void Produce(
        ConcurrentPriQueue<BenchmarkItem> queue,
        RunState state,
        int probeEvery,
        ManualResetEventSlim startGate,
        CancellationToken cancellationToken)
    {
        startGate.Wait();
        var orderingBase = DateTime.UtcNow;
        long sequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var isProbe = probeEvery > 0 && (sequence + 1) % probeEvery == 0;
            var priority = isProbe ? 0 : (int)(sequence % 4) + 1;
            var priority2 = isProbe ? 0 : (int)(sequence & 1);
            var item = new BenchmarkItem(sequence, Stopwatch.GetTimestamp());
            var orderingTimestamp = orderingBase.AddTicks(sequence);

            if (queue.Enqueue(item, priority, priority2, orderingTimestamp))
            {
                Interlocked.Increment(ref state.Accepted);
                sequence++;
            }
            else
            {
                Interlocked.Increment(ref state.AdmissionRetries);
                Thread.Yield();
            }
        }
    }

    private static int GetWorkerPriority(int workerIndex)
    {
        if (workerIndex == 0)
        {
            return 0;
        }

        var prioritySlot = (workerIndex - 1) % 5;
        return prioritySlot switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            3 => 4,
            _ => Constants.AnyPriority
        };
    }

    private static string DescribeWorkerMix(int workerCount)
    {
        return string.Join(
            ',',
            Enumerable.Range(0, workerCount)
                .Select(GetWorkerPriority)
                .GroupBy(priority => priority)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Count()}"));
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for {operation}.");
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static long Percentile(long[] sortedValues, double percentile)
    {
        var index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private static double ToMicroseconds(long stopwatchTicks)
        => stopwatchTicks * 1_000_000.0 / Stopwatch.Frequency;

    private readonly record struct BenchmarkItem(long Sequence, long EnqueuedTimestamp);

    private sealed class RunState
    {
        private readonly long[] _latencySamples = new long[LatencySampleCapacity];
        private long _latencySampleCount;

        public RunState(int workerCount)
        {
            WaitingWorkers = new int[workerCount];
        }

        public int[] WaitingWorkers { get; }
        public long Accepted;
        public long AdmissionRetries;
        public long Consumed;

        public void RecordLatency(long elapsedTicks)
        {
            var sampleIndex = Interlocked.Increment(ref _latencySampleCount) - 1;
            _latencySamples[sampleIndex % _latencySamples.Length] = elapsedTicks;
        }

        public long[] GetLatencySamples()
        {
            var count = (int)Math.Min(Interlocked.Read(ref _latencySampleCount), _latencySamples.Length);
            var samples = new long[count];
            Array.Copy(_latencySamples, samples, count);
            return samples;
        }

        public int WaitingWorkerCount()
        {
            var count = 0;
            for (var index = 0; index < WaitingWorkers.Length; index++)
            {
                count += Volatile.Read(ref WaitingWorkers[index]);
            }

            return count;
        }
    }

    private sealed record RunResult(
        double ElapsedMilliseconds,
        long Accepted,
        long ConsumedAtDeadline,
        int BacklogAtDeadline,
        long DrainedAfterDeadline,
        long AdmissionRetries,
        double AcceptedRequestsPerSecond,
        double ConsumedRequestsPerSecond,
        int LatencySampleCount,
        double P50Microseconds,
        double P95Microseconds,
        double P99Microseconds,
        double MaxMicroseconds,
        double AllocatedBytesPerRequest,
        double CpuUtilizationPercent)
    {
        public string ToOutput()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"overall elapsed_ms={ElapsedMilliseconds:F2} accepted={Accepted} " +
                $"consumed_at_deadline={ConsumedAtDeadline} backlog_at_deadline={BacklogAtDeadline} " +
                $"drained_after_deadline={DrainedAfterDeadline} admission_retries={AdmissionRetries} " +
                $"accepted_rps={AcceptedRequestsPerSecond:F0} consumed_rps={ConsumedRequestsPerSecond:F0} " +
                $"latency_samples={LatencySampleCount} p50_us={P50Microseconds:F2} " +
                $"p95_us={P95Microseconds:F2} p99_us={P99Microseconds:F2} max_us={MaxMicroseconds:F2} " +
                $"allocated_bytes_per_request={AllocatedBytesPerRequest:F2} " +
                $"cpu_utilization_percent={CpuUtilizationPercent:F2}");
        }
    }

    private sealed class BenchmarkOptions
    {
        public int Workers { get; private set; } = 1000;
        public int Capacity { get; private set; } = 1000;
        public int ProbeEvery { get; private set; } = 10_000;
        public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);
        public TimeSpan ShutdownTimeout { get; private set; } = TimeSpan.FromSeconds(30);
        public string Label { get; private set; } = "working-tree";
        public bool ShowHelp { get; private set; }

        public static BenchmarkOptions Parse(string[] args)
        {
            var options = new BenchmarkOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string ReadValue()
                {
                    if (++index >= args.Length)
                    {
                        throw new ArgumentException($"Missing value for {argument}.");
                    }

                    return args[index];
                }

                switch (argument)
                {
                    case "--workers":
                        options.Workers = ParsePositiveInt(argument, ReadValue());
                        break;
                    case "--capacity":
                        options.Capacity = ParsePositiveInt(argument, ReadValue());
                        break;
                    case "--probe-every":
                        options.ProbeEvery = ParseNonNegativeInt(argument, ReadValue());
                        break;
                    case "--duration-seconds":
                        options.Duration = TimeSpan.FromSeconds(ParsePositiveInt(argument, ReadValue()));
                        break;
                    case "--label":
                        options.Label = ReadValue();
                        break;
                    case "--shutdown-timeout-seconds":
                        options.ShutdownTimeout = TimeSpan.FromSeconds(ParsePositiveInt(argument, ReadValue()));
                        break;
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {argument}");
                }
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine(
                "dotnet run -c Release --project test/QueueBenchmarks -- " +
                "[--workers 1000] [--duration-seconds 60] [--capacity 1000] " +
                "[--probe-every 10000] [--label NAME] [--shutdown-timeout-seconds 30]");
        }

        private static int ParsePositiveInt(string argument, string value)
        {
            var parsed = ParseNonNegativeInt(argument, value);
            if (parsed == 0)
            {
                throw new ArgumentException($"{argument} must be greater than zero.");
            }

            return parsed;
        }

        private static int ParseNonNegativeInt(string argument, string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                throw new ArgumentException($"{argument} must be a non-negative integer.");
            }

            return parsed;
        }
    }
}