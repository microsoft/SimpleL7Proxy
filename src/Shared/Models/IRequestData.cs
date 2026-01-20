namespace Shared.RequestAPI.Models;

public interface IRequestData
{
    string? Id { get; set; }
    string? UserID { get; set; }
    string? Guid { get; set; }
}