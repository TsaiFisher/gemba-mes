using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.ViewModels
{
    public class MaterialStagingFormVm
    {
        public string? StagingId { get; set; }

        public string SourceType { get; set; } = "Manual";
        public string? SourceId { get; set; }

        public string? StagingWhCode { get; set; }
        public string? StagingLocationCode { get; set; }

        public string Status { get; set; } = "Draft";
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        public string? Remarks { get; set; }
        public string CreatedBy { get; set; } = string.Empty;

        public List<MaterialStagingLineVm> Lines { get; set; } = new();
    }

    public class MaterialStagingLineVm
    {
        public int LineNo { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string? UomCode { get; set; }
        public string? StockUomCode { get; set; }
        public decimal RequiredQty { get; set; }
        public decimal PickedQty { get; set; }
        public int? ReservationId { get; set; }

        /// <summary>本行為哪張工單備的（生產批來源時逐行不同，預留轉移／領料消耗都認這欄）。</summary>
        public string? SourceWorkOrderId { get; set; }

        /// <summary>本行對應的派工單（派工層級備料時填入）。</summary>
        public string? SourceDispatchId { get; set; }

        /// <summary>揀料來源倉庫／儲位（移入備料倉時的搬出點）。</summary>
        public string? FromWarehouseCode { get; set; }
        public string? FromLocationCode { get; set; }

        public string? Remarks { get; set; }
    }
}
