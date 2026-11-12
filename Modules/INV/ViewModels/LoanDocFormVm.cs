using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.ViewModels
{
    public class LoanDocFormVm
    {
        public string? LoanDocId { get; set; }

        [Required]
        public DateOnly LoanDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "IB";

        /// <summary>Customer / Supplier / Department</summary>
        [Required, MaxLength(20)]
        public string EntityType { get; set; } = "Customer";

        [Required, MaxLength(100)]
        public string EntityCode { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public List<LoanDocLineVm> Lines { get; set; } = new();
    }

    public class LoanDocLineVm
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

        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }
    }
}
