public class Program
{
    private static SortedSet<(int priority, int counter, string userId, Guid requestId, DateTime timestamp)> queue = new SortedSet<(int priority, int counter, string userId, Guid requestId, DateTime timestamp)>();
    private static Dictionary<Guid, (int priority, int counter, string userId, DateTime timestamp)> entryFinder = new Dictionary<Guid, (int priority, int counter, string userId, DateTime timestamp)>();
    private static int counter = 0;
    private static UserPriority userPriority = new UserPriority(threshold: 5, expirationTimeInSeconds: 10);

    public static void Main()
    {
        Thread inputThread = new Thread(HandleInput);
        inputThread.Start();

        while (true)
        {
            Thread.Sleep(1000);
            RemoveNextEntry();
            DisplayQueue();
        }
    }

    private static void HandleInput()
    {
        while (true)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).KeyChar;
                if (char.IsDigit(key) && key != '0')
                {
                    string userId = "user" + key;
                    AddToQueue(userId);
                    DisplayQueue();
                }
            }
        }
    }

    private static void AddToQueue(string userId)
    {
        var (priority, requestId, timestamp) = userPriority.AddRequest(userId);
        var entry = (priority, counter, userId, requestId, timestamp);
        counter++;
        entryFinder[requestId] = (priority, counter, userId, timestamp);
        queue.Add(entry);
    }

    private static void RemoveNextEntry()
    {
        if (queue.Count > 0)
        {
            var entry = queue.Min;
            queue.Remove(entry);
            if (entryFinder.ContainsKey(entry.requestId))
            {
                entryFinder.Remove(entry.requestId);
                userPriority.RemoveRequest(entry.userId, entry.requestId);
            }
        }
    }

    private static void DisplayQueue()
    {
        Console.Clear();
        Console.WriteLine("Current Queue:");
        foreach (var entry in queue)
        {
            Console.WriteLine($"User: {entry.userId}, Priority: {entry.priority}, Counter: {entry.counter}, Request ID: {entry.requestId}, Timestamp: {entry.timestamp}");
        }
    }
}