using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.ORD.Models
{
    /// <summary>出貨單主檔。確認時產生 GS（訂單出貨，庫存-）StockMovement。</summary>
    public class GoodsShipment : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string GoodsShipmentId { get; set; } = string.Empty;

        public DateOnly ShipDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "GS";

        [Required, MaxLength(20)]
        public string CompanyCode { get; set; } = string.Empty;

        [MaxLength(30)]
        public string? SalesOrderId { get; set; }

        [MaxLength(300)]
        public string? ShipAddress { get; set; }

        /// <summary>本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

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

        public ICollection<GoodsShipmentLine> Lines { get; set; } = new List<GoodsShipmentLine>();
    }
}
