namespace SimpleL7Proxy.ServiceBus {
    public class ServiceBusStatusMessage
    {
        public Guid RequestGuid { get; set; }
        public string Status { get; set; }
        public DateTime Timestamp { get; set; }
        public string topicName { get; set; } = "status";

        public ServiceBusStatusMessage(Guid requestGuid, string topic, string status)
        {
            RequestGuid = requestGuid;
            Status = status;
            topicName = topic;
            Timestamp = DateTime.UtcNow;
        }
    }
}