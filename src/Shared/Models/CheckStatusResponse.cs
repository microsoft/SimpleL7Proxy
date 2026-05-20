namespace Shared.RequestAPI.Models
{
    public class CheckStatusResponse
    {
        public string Status { get; set; } = string.Empty;
        public string Guid { get; set; } = string.Empty;
        public int CheckCount { get; set; } =0;
    }
}