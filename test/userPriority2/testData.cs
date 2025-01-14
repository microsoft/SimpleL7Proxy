public class testData {
    public Guid guid { get; set; }
    public required string userId { get; set; }
    public testData(Guid g)
    {
        this.guid = g;
    }

}