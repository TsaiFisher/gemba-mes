using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.ORD.Models
{
    /// <summary>
    /// 內部訂單。TypeCode=NO（出料，庫存-）或 NR（退料，庫存+）。
    /// 確認時依 TypeCode 產生對應 StockMovement。
    /// </summary>
    public class InternalOrder : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string InternalOrderId { get; set; } = string.Empty;

        public DateOnly OrderDate { get; set; }

        /// <summary>NO = 內部訂單出（庫存-）；NR = 內部訂單退（庫存+）</summary>
        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "NO";

        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
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

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken；見 ord-module.md §13）</summary>
        public uint xmin { get; set; }

        public ICollection<InternalOrderLine> Lines { get; set; } = new List<InternalOrderLine>();
    }
}
