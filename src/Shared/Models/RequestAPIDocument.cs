using System;

namespace Shared.RequestAPI.Models;

public class RequestAPIDocument
{
    public DateTime? createdAt { get; set; }
    public string? guid { get; set; }
    public string? id { get; set; }
    public string? mid { get; set; }
    public bool? isAsync { get; set; }
    public bool? isBackground { get; set; }
    public string? backgroundRequestId { get; set; }
    public int? priority1 { get; set; }
    public int? priority2 { get; set; }
    public RequestAPIStatusEnum? status { get; set; }
    public string? userID { get; set; }

    public RequestAPIDocument DeepCopy()
    {
        return new RequestAPIDocument()
        {
            createdAt = this.createdAt,
            guid = this.guid,
            id = this.id,
            mid = this.mid,
            isAsync = this.isAsync,
            isBackground = this.isBackground,
            backgroundRequestId = this.backgroundRequestId,
            priority1 = this.priority1,
            priority2 = this.priority2,
            status = this.status,
            userID = this.userID
        };
    }
}