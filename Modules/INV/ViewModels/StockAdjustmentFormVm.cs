using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.ViewModels
{
    public class StockAdjustmentFormVm
    {
        public string? StockAdjustmentId { get; set; }

        [Required]
        public DateOnly AdjustDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "IA";

        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        [MaxLength(20)]
        public string? AdjustReasonCode { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public List<StockAdjustmentLineVm> Lines { get; set; } = new();
    }

    public class StockAdjustmentLineVm
    {
        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        public decimal Qty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        public decimal? UnitPrice { get; set; }

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        /// <summary>1 = 正調整（盤盈），-1 = 負調整（盤虧）</summary>
        public int Direction { get; set; } = 1;

        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }
    }
}
