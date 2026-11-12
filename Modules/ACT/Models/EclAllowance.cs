using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Models
{
    /// <summary>IFRS 9 預期信用損失（ECL）呆帳準備（Phase C，§4.5）。
    /// 按帳齡區間乘以損失率計算應計提之呆帳準備金。</summary>
    public class EclAllowance : IPlantEntity
    {
        public int Id { get; set; }

        [Required, MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        /// <summary>帳齡區間（Current / 1-30 / 31-60 / 61-90 / 90+）</summary>
        [Required, MaxLength(20)]
        public string AgingBucket { get; set; } = string.Empty;

        /// <summary>該區間應收帳款餘額</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ArBalance { get; set; }

        /// <summary>設定之損失率（0–1）</summary>
        [Column(TypeName = "decimal(8,6)")]
        public decimal LossRate { get; set; }

        /// <summary>ECL 準備金（ArBalance × LossRate）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal EclAmount { get; set; }

        public DateTime AssessedAt { get; set; } = DateTime.UtcNow;
        public string? PlantId { get; set; }
    }

    /// <summary>帳齡損失率設定（IFRS 9 簡化矩陣）</summary>
    public class EclLossRate : IPlantEntity
    {
        public int Id { get; set; }

        [Required, MaxLength(20)]
        public string AgingBucket { get; set; } = string.Empty;

        public int AgingDaysFrom { get; set; }
        public int AgingDaysTo   { get; set; }  // -1 表示無上限

        [Column(TypeName = "decimal(8,6)")]
        public decimal LossRate { get; set; }

        public string? PlantId { get; set; }
    }
}
