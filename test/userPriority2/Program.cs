using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;


public class Program
{
    private static ConcurrentPriQueue<testData> _requestsQueue = new ConcurrentPriQueue<testData>();
    private static testData? _lastEnqueuedItem; // Made nullable to address the warning


    static CancellationTokenSource cts = new CancellationTokenSource();
    static CancellationToken token = cts.Token;

    static UserPriority up = new UserPriority() { threshold = 0.5f };


    public static async Task Main(string[] args)
    {
        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionTrapper;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        _requestsQueue.MaxQueueLength = 100;
        _requestsQueue.StartSignaler(CancellationToken.None);
        //        _requestsQueue.startCoordination(CancellationToken.None);
        await MainAsync();
    }

    private static void UnhandledExceptionTrapper(object? sender, UnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"Unhandled exception: {e.ExceptionObject}");
        // Optionally, log the exception or take other actions
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Console.WriteLine($"Unobserved task exception: {e.Exception}");
        e.SetObserved(); // Prevents the exception from terminating the process
        // Optionally, log the exception or take other actions
    }

    private static async Task MainAsync()
    {
        bool perfMode = false;
        _requestsQueue.MaxQueueLength = 1000;
        //        _requestsQueue.startSignaler(CancellationToken.None);
        // _requestsQueue.startCoordination(CancellationToken.None);
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        if (perfMode)
        {
            await perfTest();
            return;
        }
        else
        {
            bool smoketest = true;

            if (smoketest)
            {
                // Smoke test

                Random rand = new Random();
                for (int i = 0; i < 20; i++)
                {
                    string userId = "user" + i;
                    var guid = up.addRequest(userId);
                    var item = new testData(guid) { userId = userId };
                    bool isBoosted = up.boostIndicator(userId, out float boostValue);
                    int priority2 = (isBoosted) ? 1 : 0;

                    // randomly pick a priority : 1,2,3
                    int pri1 = rand.Next(1, 4);

                    //var status= _requestsQueue.Enqueue(rd, priority, userPriorityBoost, rd.EnqueueTime, true);

                    var status = _requestsQueue.Enqueue(item, priority: pri1, priority2: priority2, DateTime.Now);
                    Console.WriteLine($" Enqueued item: status = {status} boostValue = {boostValue}");
                }

                while ( _requestsQueue.Count > 0)
                {
                    DisplayQueue(false);

                    // Dequeue a random item

                    int dequeuePriority = rand.Next(0, 4);
                    if (dequeuePriority == 0 ) dequeuePriority = Constants.AnyPriority;
                    var priStr = (dequeuePriority == Constants.AnyPriority) ? "Any" : dequeuePriority.ToString();
                    
                    Console.WriteLine($"Dequeue priority: {priStr}");
                    // time the dequeue operation
                    Stopwatch sw = Stopwatch.StartNew();
                    var itm = await _requestsQueue.DequeueAsync(dequeuePriority);
                    sw.Stop();
                    if (itm != null)
                    {
                        long dequeueTimeMicroseconds = sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                        up.removeRequest(itm.userId, itm.guid);
                        Console.WriteLine($"<< User: {itm.userId},  GUID: {itm.guid} >>  Dequeue time: {dequeueTimeMicroseconds} µs");
                    }
                    Console.WriteLine ("\n\n");
                }

            }
            else
            {
                // Start the input task

                Task inputTask = Task.Run(() => HandleInput());
                while (true)
                {
                    Thread.Sleep(1000);
                    var itm = await _requestsQueue.DequeueAsync(Constants.AnyPriority);
                    if (itm != null)
                    {
                        up.removeRequest(itm.userId, itm.guid);
                        Console.WriteLine($"<< User: {itm.userId},  Request ID: {itm.guid} >>");
                    }
                    DisplayQueue();
                }
            }
        }
    }

    public static async Task perfTest()
    {
        int consumerCount = 5000;
        int totalOperations = 1000000;
        int completedConsumers = 0;

        Stopwatch stopwatch = Stopwatch.StartNew();

        // Start Consumers
        Task[] consumers = new Task[consumerCount];
        Random _random = new Random();

        string[] status = new string[consumerCount];
        for (int i = 0; i < consumerCount; i++)
        {
            // Task specific static variable
            int taskId = i;
            string tid = $"Consumer-{taskId}";

            consumers[i] = Task.Run(async () =>
            {
                try
                {
                    while (Interlocked.CompareExchange(ref completedConsumers, 0, 0) < totalOperations)
                    {
                        try
                        {
                            //                            status[taskId] = "dequeue";
                            var item = await _requestsQueue.DequeueAsync(Constants.AnyPriority).ConfigureAwait(false);
                            if (item != null)
                            {
                                // Process the item
                                //Console.WriteLine($"Consumer {tid} got item {item.userId}");
                                //                                status[taskId] = "dequeue delay";
                                await Task.Delay(_random.Next(500)).ConfigureAwait(false); // Simulate processing time
                                                                                           //                                status[taskId] = "dequeue delay increment";
                                Interlocked.Increment(ref completedConsumers);
                                //Console.WriteLine($"Consumer {tid} got item {item.userId} - removing request");
                                //                                status[taskId] = "dequeue delay increment remove";
                                up.removeRequest(item.userId, item.guid);
                                //                                status[taskId] = "dequeue delay increment remove yield";
                                await Task.Yield();
                            }
                            else
                            {
                                Console.WriteLine($"Consumer {tid} got null item");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error in consumer {tid}: {ex.Message}");
                        }
                    }
                    status[taskId] = "dequeue delay increment remove exit";

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in consumer {tid}: {ex.Message}");
                }

                //Console.WriteLine($"Consumer {tid} finished");

            });
        }

        // Start Reporter Task
        Task reporter = Task.Run(async () =>
        {
            DateTime lastReportTime = DateTime.Now;
            while (Interlocked.CompareExchange(ref completedConsumers, 0, 0) < totalOperations)
            {
                Console.WriteLine($"{DateTime.Now} Completed Consumers: {completedConsumers} / {totalOperations} - {100.0 * completedConsumers / totalOperations:F2}% : {completedConsumers / stopwatch.Elapsed.TotalSeconds:F2} ops/sec ");
                for (int i = 0; i < consumerCount; i++)
                {
                    // Console.WriteLine($"Consumer[{i}]: {status[i]}");
                }
                //Console.WriteLine("Enqueue: " + _requestsQueue.EnqueueStatus); 
                //Console.WriteLine("SignalWorker: " + _requestsQueue.SignalWorkerStatus);
                await Task.Delay(10000).ConfigureAwait(false);
            }
        });

        // Start Producer
        Task producer = Task.Run(async () =>
        {

            Random _random = new Random();

            for (int i = 0; i <= totalOperations + consumerCount; i++)
            {
                int usernum = _random.Next(100);
                string _userId = "user" + usernum;
                var guid = up.addRequest(_userId);
                var item = new testData(guid) { userId = _userId };
                bool isBoosted = up.boostIndicator(_userId, out float boostValue);
                int priority2 = (isBoosted) ? 1 : 0;

                while (!_requestsQueue.Enqueue(item, priority: 1, priority2: priority2, DateTime.Now))
                {
                    await Task.Delay(10).ConfigureAwait(false); // Wait for 10 ms for the queue to have space
                }
                //Console.WriteLine($"Producer >>> Enqueued item #{i}");
            }

            Console.WriteLine("Producer finished =================================================================");
        });

        await Task.WhenAll(producer);
        stopwatch.Stop();

        // Wait for consumers to finish
        await Task.WhenAll(consumers);

        double opsPerSecond = totalOperations / stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine($"Total Operations: {totalOperations}");
        Console.WriteLine($"Elapsed Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        Console.WriteLine($"Operations per Second: {opsPerSecond:F2} ops/sec");

    }

    private static async Task HandleInput()
    {

        Console.WriteLine("Press a number key to add a request to the queue");
        while (true)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).KeyChar;

                if (char.IsDigit(key) && key != '0')
                {
                    string userId = "user" + key;
                    var guid = up.addRequest(userId);

                    bool isBoosted = up.boostIndicator(userId, out float boostValue);
                    int priority = 2;
                    int priority2 = isBoosted ? 1 : 0;

                    var item = new testData(guid) { userId = userId /*, maybe store boostValue here if needed */ };
                    var status = _requestsQueue.Enqueue(item, priority, priority2, DateTime.Now);

                    _lastEnqueuedItem = item; // Track recently enqueued item

                    //Console.WriteLine($">> User: {userId},  Request ID: {guid} <<");
                    DisplayQueue();
                    Console.WriteLine($" Enqueued item: status = {status} boostValue = {boostValue}");
                }

                if (key == 'd')
                {
                    var itm = await _requestsQueue.DequeueAsync(Constants.AnyPriority);

                    DisplayQueue();
                }
            }
        }
    }

    private static void DisplayQueue(bool clear=true)
    {
        if (clear)
            Console.Clear();
        var items = _requestsQueue.getItems();
        Console.WriteLine("=== Queue Contents ===");
        foreach (var i in items)
        {
            // Highlight the last enqueued item
            string highlight = (i.Item == _lastEnqueuedItem) ? " <---" : "";
            Console.WriteLine($"Item: {i.Item.userId}, guid: {i.Item.guid}  Priority: {i.Priority}, Priority2: {i.Priority2} {highlight}");
        }
        Console.WriteLine("======================");

    }
}