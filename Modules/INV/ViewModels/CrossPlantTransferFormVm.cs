using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.ViewModels
{
    public class CrossPlantTransferFormVm
    {
        public string? CrossPlantTransferId { get; set; }

        [Required]
        public DateOnly TransferDate { get; set; }

        [MaxLength(20)]
        public string? FromWhCode { get; set; }

        [MaxLength(20)]
        public string? ToWhCode { get; set; }

        [MaxLength(30)]
        public string? ToPlantId { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        public string? SourcePlantId { get; set; }

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public List<CrossPlantTransferLineVm> Lines { get; set; } = new();
    }

    public class CrossPlantTransferLineVm
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

        [MaxLength(20)]
        public string? FromWhCode { get; set; }

        [MaxLength(20)]
        public string? FromLocationCode { get; set; }

        [MaxLength(20)]
        public string? ToWhCode { get; set; }

        [MaxLength(20)]
        public string? ToLocationCode { get; set; }

        /// <summary>批號（兩廠相同）；空白＝不追蹤批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        public decimal QtyShipped { get; set; }
        public decimal QtyReceived { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }
    }
}
