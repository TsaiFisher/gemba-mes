using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.ORD.Models
{
    /// <summary>
    /// 內部訂單領料的倉庫作業明細（每次揀貨一筆）：記錄實際出庫的儲位/批號/數量。
    /// 由「倉庫作業」(WarehouseApi pick) 寫入，與 SampleDocLocationDetail 同構。
    /// </summary>
    public class InternalOrderLocationDetail
    {
        public int Id { get; set; }

        [MaxLength(30)]
        public string InternalOrderId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [MaxLength(20)]
        public string LocationCode { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public InternalOrderLine? Line { get; set; }
    }
}
