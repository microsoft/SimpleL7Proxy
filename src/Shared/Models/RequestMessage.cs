namespace Shared.RequestAPI.Models;

public class RequestMessage : IRequestData
{
    public string? Id { get; set; }
    public string? UserID { get; set; }
    public string? Body { get; set; }
    public string? Path { get; set; }
    public string? Method { get; set; }
    public string? Priority { get; set; }

    // Interface implementation
    string? IRequestData.Guid { get; set; }
}