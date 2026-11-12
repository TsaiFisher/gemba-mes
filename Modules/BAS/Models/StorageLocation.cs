using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 儲位 / 庫位（WS）— 複合主鍵：WhCode + LocationCode
    /// </summary>
    public class StorageLocation
    {
        [Required, MaxLength(20)]
        public string WhCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string LocationCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string LocationName { get; set; } = string.Empty;

        /// <summary>儲位類型 → CodeEntry: location_type</summary>
        [MaxLength(20)]
        public string? LocationType { get; set; }

        [MaxLength(200)]
        public string? Remarks { get; set; }

        public bool IsActive { get; set; } = true;

        // Navigation
        [ForeignKey(nameof(WhCode))]
        public Warehouse? Warehouse { get; set; }
    }
}
