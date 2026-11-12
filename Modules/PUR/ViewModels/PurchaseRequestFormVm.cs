using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.PUR.ViewModels
{
    /// <summary>請購單 Side Panel 表單 ViewModel</summary>
    public class PurchaseRequestFormVm
    {
        public string? PurchaseRequestId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        [Required]
        public DateOnly RequestDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string RequestedBy { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>來源 MRP 執行單號（MRP 自動建立時填入；手動建立為 null）</summary>
        [MaxLength(30)]
        public string? SourceMrpRunId { get; set; }

        public List<PurchaseRequestLineVm> Lines { get; set; } = new();
    }

    public class PurchaseRequestLineVm
    {
        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        public DateOnly? DeliveryDate { get; set; }

        [Range(0.0001, double.MaxValue)]
        public decimal RequestQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsClosed { get; set; }
    }
}
