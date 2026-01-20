using System;
using System.Collections.Generic;
using System.Net;

using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.DTO
{
    public class ProxyEventDto
    {
        // Base properties from ConcurrentDictionary
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

        // Standard fields that are always present
        public string Version { get; set; } = Constants.VERSION;
        public string Revision { get; set; } = "";
        public string ContainerApp { get; set; } = "";

        // Core properties
        public EventType Type { get; set; } = EventType.Console;
        public int Status { get; set; } = 0;
        public string Uri { get; set; } = "http://localhost";
        public string MID { get; set; } = "";
        public string ParentId { get; set; } = "";
        public string Method { get; set; } = "GET";
        public double DurationMilliseconds { get; set; } = 0;

        // Optional fields
        public string? ErrorMessage { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorStack { get; set; }

        // Conversion from ProxyEvent
        public static ProxyEventDto FromProxyEvent(ProxyEvent evt)
        {
            var dto = new ProxyEventDto
            {
                Type = evt.Type,
                Status = (int)evt.Status,
                Uri = evt.Uri.ToString(),
                MID = evt.MID ?? "",
                ParentId = evt.ParentId ?? "",
                Method = evt.Method ?? "GET",
                DurationMilliseconds = evt.Duration.TotalMilliseconds,
            };

            // Copy all dictionary entries
            foreach (var kvp in evt)
            {
                dto.Properties[kvp.Key] = kvp.Value;
            }

            // Handle exception if present
            if (evt.Exception != null)
            {
                dto.ErrorMessage = evt.Exception.Message;
                dto.ErrorType = evt.Exception.GetType().FullName;
                dto.ErrorStack = evt.Exception.StackTrace;
            }

            return dto;
        }

        // Conversion to ProxyEvent
        public ProxyEvent ToProxyEvent()
        {
            var evt = new ProxyEvent
            {
                Type = this.Type,
                Status = (HttpStatusCode)this.Status,
                Uri = new Uri(this.Uri),
                MID = this.MID,
                ParentId = this.ParentId,
                Method = this.Method,
                Duration = TimeSpan.FromMilliseconds(this.DurationMilliseconds)
            };

            // Copy all properties
            foreach (var kvp in Properties)
            {
                evt[kvp.Key] = kvp.Value;
            }

            // Recreate exception if error details exist
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                evt.Exception = new Exception(ErrorMessage);
            }

            return evt;
        }
    }
}
