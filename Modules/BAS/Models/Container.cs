using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>容器（CONT）</summary>
    public class Container
    {
        /// <summary>容器種類代碼。30 字與報工 ProductionReport.ContainerCode 對齊（container-tracking.md §5 #11）</summary>
        [Key, MaxLength(30)]
        public string ContainerCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string ContainerName { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Spec { get; set; }

        /// <summary>最大容量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? MaxCapacity { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    }
}
