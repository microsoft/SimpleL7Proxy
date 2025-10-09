using System;

namespace Shared.RequestAPI.Models;

public class RequestAPIDocument
{
    public string? backgroundRequestId { get; set; }
    public DateTime? createdAt { get; set; }
    public string? guid { get; set; }
    public string? id { get; set; }
    public bool? isAsync { get; set; }
    public bool? isBackground { get; set; }
    public string? mid { get; set; }
    public int? priority1 { get; set; }
    public int? priority2 { get; set; }
    public RequestAPIStatusEnum? status { get; set; }
    public string? URL { get; set; }
    public string? userID { get; set; }

    public RequestAPIDocument DeepCopy()
    {
        return new RequestAPIDocument()
        {
            backgroundRequestId = this.backgroundRequestId,
            createdAt = this.createdAt,
            guid = this.guid,
            id = this.id,
            isAsync = this.isAsync,
            isBackground = this.isBackground,
            mid = this.mid,
            priority1 = this.priority1,
            priority2 = this.priority2,
            status = this.status,
            URL = this.URL,
            userID = this.userID
        };
    }
}