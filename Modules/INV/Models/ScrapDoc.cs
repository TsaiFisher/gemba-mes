using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 報廢單主檔。
    /// TypeCode：IJ（庫存報廢）
    /// </summary>
    public class ScrapDoc : IPlantEntity, IDataAuthEntity, IInventoryDocument, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string ScrapDocId { get; set; } = string.Empty;

        public DateOnly ScrapDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "IJ";

        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Scrap";

        /// <summary>責任部門代號（從組織圖選取）</summary>
        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        /// <summary>報廢原因代號（建議對應 CodeEntry category = PZ.ScrapReason）</summary>
        [MaxLength(20)]
        public string? ScrapReasonCode { get; set; }

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        [MaxLength(100)]
        public string? ApprovalRequestId { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>
        /// 樂觀並發控制（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken）。
        /// 防 Confirm 過帳並發雙重扣帳：表頭自身 xmin 確定性擋下同單並發過帳（不依賴
        /// Inventory xmin 的讀寫交錯時機）；收尾 UPDATE 帶 WHERE xmin → 後到者拋
        /// DbUpdateConcurrencyException → 整段過帳交易回滾（Confirm 已 catch 轉友善訊息）。
        /// 見 reference-concurrency-catch-audit：INV 單據表頭補 xmin 試點。
        /// </summary>
        public uint xmin { get; set; }

        public ICollection<ScrapDocLine> Lines { get; set; } = new List<ScrapDocLine>();

        string IInventoryDocument.DocNo     => ScrapDocId;
        DateOnly IInventoryDocument.DocDate => ScrapDate;
        IReadOnlyList<IInventoryDocumentLine> IInventoryDocument.GetLines()
            => Lines.Cast<IInventoryDocumentLine>().ToList();
    }
}
