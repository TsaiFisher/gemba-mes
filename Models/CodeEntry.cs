using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Models
{
    /// <summary>
    /// 通用代碼檔：全公司共用，供使用者自行維護各種下拉選單的資料來源。
    /// 例：不良原因、停工原因、製程類別、計量單位等。
    /// 唯一索引：(Category, Code)
    /// </summary>
    public class CodeEntry
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>代碼類別，程式端用的分類鍵值，例："defect_reason"、"unit"</summary>
        [Required, MaxLength(100)]
        public string Category { get; set; } = string.Empty;

        /// <summary>代碼值，例："D001"、"KG"</summary>
        [Required, MaxLength(50)]
        public string Code { get; set; } = string.Empty;

        /// <summary>中文顯示名稱，例："外觀不良"、"公斤"</summary>
        [Required, MaxLength(200)]
        public string NameZh { get; set; } = string.Empty;

        /// <summary>英文顯示名稱（選填）</summary>
        [MaxLength(200)]
        public string? NameEn { get; set; }

        /// <summary>備註（選填）</summary>
        [MaxLength(500)]
        public string? Remark { get; set; }

        /// <summary>是否啟用；停用後不出現於下拉選單，但資料仍保留。核准後自動設為 true。</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>同一類別內的顯示排序，數字越小越前面</summary>
        public int SortOrder { get; set; } = 0;

        /// <summary>簽核流程狀態；無簽核設定時維持 Approved</summary>
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;

        /// <summary>對應的簽核申請單 ID（送審後填入）</summary>
        public string? ApprovalRequestId { get; set; }

    }
}
