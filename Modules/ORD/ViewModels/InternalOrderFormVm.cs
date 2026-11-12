using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.ORD.ViewModels
{
    public class InternalOrderFormVm
    {
        public string? InternalOrderId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public DocStatus Status { get; set; } = DocStatus.Draft;

        [Required]
        public DateOnly OrderDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>NO = 出料；NR = 退料</summary>
        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "NO";

        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<InternalOrderLineVm> Lines { get; set; } = new();
    }

    public class InternalOrderLineVm
    {
        public int LineNo { get; set; }

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        public DateOnly? DeliveryDate { get; set; }

        [Range(0.0001, double.MaxValue)]
        public decimal OrderQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        public bool IsClosed { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public decimal QtyPutAway { get; set; }
    }
}
