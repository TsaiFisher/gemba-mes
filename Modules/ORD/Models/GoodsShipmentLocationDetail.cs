using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.ORD.Models
{
    /// <summary>出貨單儲位明細（第3層：倉庫作業下架時建立）</summary>
    public class GoodsShipmentLocationDetail
    {
        public int Id { get; set; }

        [Required, MaxLength(30)]
        public string GoodsShipmentId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        /// <summary>儲位代號（出庫來源儲位）</summary>
        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = string.Empty;

        /// <summary>倉庫代號（由 LocationCode 自動回寫）</summary>
        [MaxLength(20)]
        public string? WhCode { get; set; }

        /// <summary>批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>數量（庫存單位）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public GoodsShipmentLine? Line { get; set; }
    }
}
