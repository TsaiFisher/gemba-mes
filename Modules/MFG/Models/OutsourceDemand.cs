using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    public enum OutsourceDemandType
    {
        WholeProduct = 0,   // 整品委外（RunJ 展開）
        Operation    = 1    // 工序委外（RoutingOperation.IsOutsource）
    }

    /// <summary>
    /// 委外建議單（DocType: OD）— MRP 執行後產生，統一整品委外與工序委外兩條路徑。
    /// 轉委外採購單後以 TransferredQty 追蹤已轉數量，支援分批拆單至不同廠商/交期。
    /// </summary>
    public class OutsourceDemand : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string DemandId { get; set; } = string.Empty;

        /// <summary>來源 MRP 執行單（MRP 展開時填入；手動建立或 OPO 轉換自動建立時可為 null）</summary>
        [MaxLength(40)]
        public string? MrpRunId { get; set; }

        /// <summary>來源工單</summary>
        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        /// <summary>工序委外時對應派工單 ID</summary>
        [MaxLength(40)]
        public string? DispatchId { get; set; }

        public OutsourceDemandType DemandType { get; set; } = OutsourceDemandType.WholeProduct;

        /// <summary>委外品號（整品委外為成品，工序委外為半製品）</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,6)")]
        public decimal DemandQty { get; set; }

        public DateOnly RequiredDate { get; set; }

        /// <summary>工序委外時填入對應工序代碼（供 OutsourceQuotation 4-level 查詢）</summary>
        [MaxLength(30)]
        public string? RoutingOperationCode { get; set; }

        /// <summary>已轉委外採購單數量（每次建立 OutsourcePurchaseOrderLine 時累加）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal TransferredQty { get; set; } = 0M;

        [NotMapped]
        public bool IsFullyTransferred => TransferredQty >= DemandQty;

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public WorkOrder? WorkOrder { get; set; }
    }
}
