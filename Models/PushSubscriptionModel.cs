namespace SmartFactory.MES.Models
{
    /// <summary>Web Push 訂閱記錄（每個裝置/瀏覽器各一筆）。</summary>
    public class PushSubscriptionRecord
    {
        public string Id           { get; set; } = Guid.NewGuid().ToString("N");
        public string UserId       { get; set; } = string.Empty;
        public string Endpoint     { get; set; } = string.Empty;
        public string P256dhKey    { get; set; } = string.Empty;
        public string AuthKey      { get; set; } = string.Empty;
        public string? UserAgent   { get; set; }
        public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;
    }
}
