using System.Net.Http;

public class PreparedTest
{
    public HttpRequestMessage request;
    public TimeSpan? timeout;

    public HttpMethod Method { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    public PreparedTest(HttpRequestMessage request, TimeSpan? timeout, string name, string method, string path)
    {
        this.request = request;
        this.timeout = timeout;
        Name = name;
        Method = new HttpMethod(method);
        Path = path;
    }
}
