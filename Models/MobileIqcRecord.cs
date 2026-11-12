using System.ComponentModel.DataAnnotations.Schema;
namespace SmartFactory.MES.Models
{
    /// <summary>手機 IQC 進料檢驗紀錄（mobile-pwa.md §7.2 aiq）。</summary>
    public class MobileIqcRecord
    {
        public string Id             { get; set; } = Guid.NewGuid().ToString("N");
        public string GoodsReceiptId { get; set; } = string.Empty;
        public int    LineNo         { get; set; }
        public string ProductCode    { get; set; } = string.Empty;
        public string? PlantId       { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal SampleQty     { get; set; }
        [Column(TypeName = "decimal(18,4)")]
        public decimal PassQty       { get; set; }
        [Column(TypeName = "decimal(18,4)")]
        public decimal FailQty       { get; set; }

        /// <summary>Pass / Fail / Conditional</summary>
        public string Result         { get; set; } = "Pass";

        public string? Remarks       { get; set; }
        public string InspectorId    { get; set; } = string.Empty;
        public DateTime InspectedAt  { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    }
}
