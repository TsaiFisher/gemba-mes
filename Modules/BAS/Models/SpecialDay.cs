using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>特殊日（SD）— 加班日 / 補假日 / 國定假日</summary>
    public class SpecialDay : IPlantEntity
    {
        /// <summary>日期（PK）</summary>
        [Key]
        public DateOnly Date { get; set; }

        /// <summary>類型 → CodeEntry: special_day_type（例：Holiday / Overtime / Compensation）</summary>
        [Required, MaxLength(20)]
        public string DayType { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Description { get; set; }

        /// <summary>補班對應的星期幾（僅補班日有值，1=週一 … 7=週日）</summary>
        public int? CompensatoryDayOfWeek { get; set; }

        /// <summary>所屬年度（方便查詢）</summary>
        public int Year { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }
    }
}
