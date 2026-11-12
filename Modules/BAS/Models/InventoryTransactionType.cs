using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>庫存異動類別（IY）</summary>
    public class InventoryTransactionType
    {
        [Key, MaxLength(20)]
        public string TypeCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string TypeName { get; set; } = string.Empty;

        /// <summary>系統分類（決定單號前綴），來自 MovementCategory enum</summary>
        [MaxLength(20)]
        public string? TypeCategory { get; set; }

        /// <summary>庫存增減方向：+1 入庫 / -1 出庫</summary>
        public int Direction { get; set; } = 1;

        /// <summary>系統內建類型（true = 不可刪除）</summary>
        public bool IsSystemType { get; set; } = false;

        /// <summary>是否強制填批號</summary>
        public bool RequireLot { get; set; } = false;

        /// <summary>是否強制填來源單號</summary>
        public bool RequireDoc { get; set; } = true;

        /// <summary>來源異動代號（自我參照 FK）</summary>
        [MaxLength(20)]
        public string? SourceTypeCode { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Self-referencing navigation
        [ForeignKey(nameof(SourceTypeCode))]
        public InventoryTransactionType? SourceType { get; set; }
    }
}
