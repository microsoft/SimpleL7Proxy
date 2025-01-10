namespace Tests;

[TestClass]
public class ProxyWorkerTestFixture
{
    [TestMethod]
    public async Task TestMethod()
    {
        var taskSignaler = new TaskSignaler<int>();
        var task1 = taskSignaler.WaitForSignalAsync("task1");
        var task2 = taskSignaler.WaitForSignalAsync("task2");

        var signalTask1 = taskSignaler.SignalRandomTask(42);
        var signalTask2 = taskSignaler.SignalRandomTask(43);

        Assert.IsTrue(signalTask1);
        Assert.IsTrue(signalTask2);

        var result1 = await task1;
        var result2 = await task2;

        Assert.AreEqual(42, result1);
        Assert.AreEqual(43, result2);
    }
}