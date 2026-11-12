using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>
    /// 廠商單價目錄（pur-domain.md §2）— 全域，非 IPlantEntity（廠商單價全公司共用）。
    /// 採購單建行時自動帶入最接近有效的議定單價。
    /// </summary>
    public class VendorPriceList : IValidatableObject
    {
        public int Id { get; set; }

        /// <summary>廠商代號 FK→Company (IsSupplier)</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>品號 FK→Product</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>幣別 FK→Currency；採購幣別</summary>
        [Required, MaxLength(20)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>議定單價（採購幣）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitPrice { get; set; }

        /// <summary>最小訂購量（null = 無限制）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? MinQty { get; set; }

        /// <summary>有效起日（含）</summary>
        public DateOnly ValidFrom { get; set; }

        /// <summary>有效迄日（含；null = 長期有效）</summary>
        public DateOnly? ValidTo { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;


        /// <summary>
        /// 生效區間不得倒置。
        ///
        /// 起訖日期只被「ValidFrom <= today && (ValidTo == null || ValidTo >= today)」這類區間查詢消費，倒置時存檔完全正常、列表也看得到，
        /// 只有查價（或查途程）永遠落空——症狀是「主檔明明建好了卻不生效」，
        /// 而畫面上沒有任何地方看得出問題。訖日留空＝無期限，是合法的。
        /// </summary>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (ValidTo != null && ValidTo < ValidFrom)
                yield return new ValidationResult(
                    PzText.Get("Pur_PriceListValidToBeforeFrom"), new[] { nameof(ValidTo) });
        }

        // Navigation
        public Company? Supplier { get; set; }
        public Product? Product { get; set; }
        public Currency? Currency { get; set; }
    }
}
