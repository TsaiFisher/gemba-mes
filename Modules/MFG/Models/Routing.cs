using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>途程主檔（WC_R）— IPlantEntity，廠別隔離</summary>
    public class Routing : IPlantEntity, IValidatableObject
    {
        [Key, MaxLength(30)]
        public string RoutingId { get; set; } = string.Empty;

        // 非必填：有值＝該途程歸屬某品號（候選篩選/驗證用），空＝通用途程（mfg-domain.md §3.1(1)）
        [MaxLength(30)]
        public string? ProductCode { get; set; }

        [MaxLength(200)]
        public string? RoutingName { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal BatchSize { get; set; } = 1;

        public int BufferDays { get; set; } = 0;

        [Column(TypeName = "decimal(18,4)")]
        public decimal AvgLeadTime { get; set; } = 0;

        /// <summary>版本（工程變更控管，mfg-domain.md §3.1(8)）。</summary>
        [MaxLength(20)]
        public string? Version { get; set; }

        /// <summary>生效起日（含）。null = 不限。</summary>
        public DateOnly? EffectiveFrom { get; set; }

        /// <summary>生效迄日（含）。null = 不限。</summary>
        public DateOnly? EffectiveTo { get; set; }

        public bool IsActive { get; set; } = true;
        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;


        /// <summary>
        /// 生效區間不得倒置。
        ///
        /// 起訖日期只被「EffectiveFrom <= today && (EffectiveTo == null || EffectiveTo >= today)」這類區間查詢消費，倒置時存檔完全正常、列表也看得到，
        /// 只有查價（或查途程）永遠落空——症狀是「主檔明明建好了卻不生效」，
        /// 而畫面上沒有任何地方看得出問題。訖日留空＝無期限，是合法的。
        /// </summary>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (EffectiveTo != null && EffectiveTo < EffectiveFrom)
                yield return new ValidationResult(
                    PzText.Get("Pur_PriceListValidToBeforeFrom"), new[] { nameof(EffectiveTo) });
        }

        public ICollection<RoutingOperation> Operations { get; set; } = new List<RoutingOperation>();
    }
}
