using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>委外採購單狀態（mfg-domain.md §19）</summary>
    public enum OutsourcePoStatus
    {
        Draft = 0,      // 草稿（建立後可調整、刪除）
        Confirmed = 1,  // 已確認（送交廠商）
        Void = 2        // 作廢
    }

    /// <summary>
    /// 委外採購單（mfg-domain.md §19）：將「待轉委外」的委外派工（整品委外 CD_MRP=S 或工序委外
    /// RoutingOperation.IsOutsource）指定協力廠商後產生。與一般採購單不同——明細為收回品，
    /// 並附送外料（由來源派工用料衍生顯示，作為領料依據）。收貨/IQC/完工入庫屬 Phase F。
    /// </summary>
    public class OutsourcePurchaseOrder : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(30)]
        public string OutsourcePurchaseOrderId { get; set; } = string.Empty;

        /// <summary>委外廠商（FK → Company，IsSupplier）</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        public DateOnly OrderDate { get; set; }

        /// <summary>廠商應交日期</summary>
        public DateOnly? ExpectedDeliveryDate { get; set; }

        public OutsourcePoStatus Status { get; set; } = OutsourcePoStatus.Draft;

        [MaxLength(200)]
        public string? Remarks { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ICollection<OutsourcePurchaseOrderLine> Lines { get; set; } = new List<OutsourcePurchaseOrderLine>();
    }
}
