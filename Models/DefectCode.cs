using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Models
{
    /// <summary>
    /// 不良項目代碼（全域主檔，對應舊系統 QNI）。
    /// FgIQC/FgIPQC/FgFQC/FgRQC 旗標控制各檢驗類型可選用的不良原因。
    /// ProductionReport.NgReasonCode 以此為 lookup（目前保留字串，後續可加 FK）。
    /// </summary>
    public class DefectCode
    {
        [Key, MaxLength(20)]
        public string Code { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        /// <summary>適用於進料 IQC</summary>
        public bool FgIQC  { get; set; } = true;

        /// <summary>適用於製程 IPQC</summary>
        public bool FgIPQC { get; set; } = true;

        /// <summary>適用於完工 FQC</summary>
        public bool FgFQC  { get; set; } = true;

        /// <summary>適用於退貨 RQC</summary>
        public bool FgRQC  { get; set; } = true;

        public bool IsActive  { get; set; } = true;
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
