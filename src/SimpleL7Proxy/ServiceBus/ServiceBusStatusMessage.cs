namespace SimpleL7Proxy.ServiceBus {
    public class ServiceBusStatusMessage
    {
        public string ClientId { get; set; }
        public Guid RequestGuid { get; set; }
        public string Status { get; set; }
        public DateTime Timestamp { get; set; }

        public ServiceBusStatusMessage(string clientId, Guid requestGuid, string status)
        {
            ClientId = clientId;
            RequestGuid = requestGuid;
            Status = status;
            Timestamp = DateTime.UtcNow;
        }
    }
}