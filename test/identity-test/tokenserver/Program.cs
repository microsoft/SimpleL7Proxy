using System.Net;
using System.Text;

class Program {
    private static readonly string filePath = "token.txt"; // Path to the text file

    static async Task Main(string[] args) {
        var Listener = new HttpListener();
        Listener.Prefixes.Add("http://localhost:8080/");
        Listener.Start();
        Console.WriteLine("Server started. Listening on http://localhost:8080/");

        while (true) {
            var Context = await Listener.GetContextAsync();
            _ = Task.Run(() => HandleRequestAsync(Context));
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext Context) {
        try {
            var Response = Context.Response;

            if (!File.Exists(filePath)) {
                Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteResponseAsync(Response, "File not found.");
                return;
            }

            var fileContent = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            Response.StatusCode = (int)HttpStatusCode.OK;
            Response.ContentType = "text/plain";
            await WriteResponseAsync(Response, fileContent);
        } catch (Exception hadesException) {
            Console.WriteLine($"[{DateTime.UtcNow}] Error: {hadesException.Message}");
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse Response, string content) {
        var responseBytes = Encoding.UTF8.GetBytes(content);
        Response.ContentLength64 = responseBytes.Length;
        await Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
        Response.Close();
    }
}