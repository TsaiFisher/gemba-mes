using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Models
{
    public class AuditLog : IPlantEntity
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public string? UserId { get; set; }

        public string? UserName { get; set; }

        /// <summary>異動別；合法值見 <see cref="AuditAction"/>（不要在此處手打字串）。</summary>
        [Required]
        public string Action { get; set; } = string.Empty;

        [Required]
        public string EntityType { get; set; } = string.Empty; // 資料表名稱

        public string? EntityId { get; set; }

        public string? PlantId { get; set; }

        public string? Changes { get; set; } // JSON 格式

        public string? IpAddress { get; set; }

        public string? UserAgent { get; set; }
    }
}
