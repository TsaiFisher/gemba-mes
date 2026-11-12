using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.QCR.Models
{
    /// <summary>
    /// 檢驗儀器主檔（全域，對應舊系統 QIN）。
    /// 定義儀器類型與校正週期；實體序號管理見 InstrumentSerial。
    /// </summary>
    public class Instrument
    {
        [Key, MaxLength(20)]
        public string InstrumentCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string InstrumentName { get; set; } = string.Empty;

        /// <summary>校正週期（天）；0 = 不需校正</summary>
        public int CalibrationFrequencyDays { get; set; } = 365;

        [MaxLength(50)]
        public string? CalibrationMethod { get; set; }

        public bool IsActive  { get; set; } = true;

        /// <summary>備註。長度上限對齊全庫慣例（varchar(500)）；原為無上限的 PG text。</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<InstrumentSerial> Serials { get; set; } = new List<InstrumentSerial>();
    }

    /// <summary>
    /// 儀器序號（廠別主檔，對應舊系統 QIN_D）。
    /// 每個物理儀器一筆；記錄部門/保管人/校正狀態。
    /// </summary>
    public class InstrumentSerial : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string SerialId { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string InstrumentCode { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string SerialNo { get; set; } = string.Empty;

        /// <summary>保管部門 → OrganizationUnit.Id（下拉；與其他單據的部門欄同長度，GUID 是 36 字）。</summary>
        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        /// <summary>保管人 → 使用者 Id（下拉）。</summary>
        [MaxLength(50)]
        public string? CustodianId { get; set; }

        public DateOnly? EffectiveDate { get; set; }
        public DateOnly? ObsoleteDate  { get; set; }

        /// <summary>Active / Retired / Loaned / UnderCalibration</summary>
        [MaxLength(20)]
        public string Status { get; set; } = "Active";

        [MaxLength(100)] public string? Brand     { get; set; }
        [MaxLength(100)] public string? Model     { get; set; }
        [MaxLength(200)] public string? Spec      { get; set; }

        /// <summary>最小刻度 / 量測公差</summary>
        [MaxLength(50)]  public string? Tolerance { get; set; }

        /// <summary>精度等級</summary>
        [MaxLength(50)]  public string? Accuracy  { get; set; }

        /// <summary>覆寫主檔校正週期（天）；null = 沿用主檔</summary>
        public int? CalibrationFrequencyDays { get; set; }

        [MaxLength(50)]
        public string? CalibrationMethod { get; set; }

        public DateOnly? LastCalibrationDate { get; set; }
        public DateOnly? NextCalibrationDate { get; set; }

        /// <summary>備註。長度上限對齊全庫慣例（varchar(500)）；原為無上限的 PG text。</summary>
        [MaxLength(500)]
        public string? Remarks  { get; set; }
        public string? PlantId  { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Instrument? Instrument { get; set; }
    }
}
