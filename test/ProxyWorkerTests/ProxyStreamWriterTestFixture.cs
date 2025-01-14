using System.Net;
using System.Text;
using SimpleL7Proxy.Proxy;

namespace Tests;

[TestClass]
public class ProxyStreamWriterTestFixture
{
    [TestMethod]
    public async Task StreamingYieldsPerChunkTest()
    {
        // Arrange
        ProxyStreamWriter concern = new();
        FakeHttpListenerResponse listenerResponse = new();
        const string proxyBody = "Hello, World!";
        ProxyData proxyData = new()
        {
            Body = Encoding.UTF8.GetBytes(proxyBody),
            ResponseMessage = new(HttpStatusCode.OK)
            {
                Content = new StringContent(proxyBody)
            }
        };
        var token = CancellationToken.None;

        // Act
        await concern.WriteResponseDataAsync(listenerResponse, proxyData, token);

        // Assert
        var stream = listenerResponse.OutputStream;
        Assert.IsTrue(stream.CanRead);

        stream.Seek(0, SeekOrigin.Begin);
        using StreamReader streamReader = new(stream);
        var results = await streamReader.ReadToEndAsync();
        Assert.AreEqual(proxyBody, results);
    }
}
