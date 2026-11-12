using SmartFactory.MES.Modules._Shared;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 委外報價主檔（DocType: OQ）— 全域主檔，不隸屬廠別。
    /// 供建立委外採購單時自動帶入單價，支援 4-level 特殊度優先查詢：
    ///   1. SupplierCode + ProductCode + RoutingId + OperationSeq（最精確）
    ///   2. SupplierCode + ProductCode + RoutingId
    ///   3. SupplierCode + ProductCode
    ///   4. SupplierCode（僅廠商，最低優先）
    /// </summary>
    public class OutsourceQuotation : IValidatableObject
    {
        [Key, MaxLength(40)]
        public string QuotationId { get; set; } = string.Empty;

        /// <summary>委外廠商代號 → FK to Company（IsSupplier = true）</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>適用品號（null = 廠商層級，適用所有品號）</summary>
        [MaxLength(30)]
        public string? ProductCode { get; set; }

        /// <summary>適用工藝路線（null = 不限工藝）</summary>
        [MaxLength(40)]
        public string? RoutingId { get; set; }

        /// <summary>適用工序序號（null = 不限工序）</summary>
        public int? OperationSeq { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitPrice { get; set; }

        /// <summary>
        /// 合約允收損耗率（0–1）。委外加工必然有損耗，這一格是「賠不賠」的分界：
        /// 超出這個比率的部分才向廠商求償（<c>ExcessScrapPenalty</c>，
        /// roadmap-fin-cst-sub.md §1.5）。
        ///
        /// <para><b>刻意可為 null，且 null ≠ 0</b>：</para>
        /// <list type="bullet">
        ///   <item><c>null</c> ＝ 合約沒有約定損耗條款 → **不扣款**（沒談過的錢不能自己扣）</item>
        ///   <item><c>0</c> ＝ 明確約定零容忍 → 只要短交就全額求償</item>
        /// </list>
        /// <para>用 <c>decimal</c> 預設 0 的話這兩件事就分不出來了——而它們的財務結果
        /// 完全相反。原本欄位只存在於收貨明細上、上游沒有任何來源，於是恆為 0；
        /// 而扣款計算被 <c>allowedRate &gt; 0</c> 擋著，超耗扣款在正式環境**結構性不可達**
        /// （人工驗收 W2-F14）。</para>
        ///
        /// <para>放在報價主檔而不是採購單／品號／廠商主檔：它是**合約條款**，
        /// 與單價同一個層級、同一組 4-level 特殊度、同一段生效期間——
        /// 談價的時候一起談，換一份報價就換一組條件。</para>
        /// </summary>
        [Range(0, 1)]
        [Column(TypeName = "decimal(8,6)")]
        public decimal? AllowedScrapRate { get; set; }

        /// <summary>報價幣別 → FK to Currency</summary>
        [Required, MaxLength(20)]
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary>報價計量單位 → FK to Uom</summary>
        [Required, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        /// <summary>最小訂購量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal? MinQty { get; set; }

        /// <summary>最大訂購量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal? MaxQty { get; set; }

        /// <summary>委外交期天數</summary>
        public int LeadDays { get; set; }

        public DateOnly EffectiveFrom { get; set; }

        /// <summary>null = 永久有效</summary>
        public DateOnly? EffectiveTo { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsActive { get; set; } = true;

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

    }
}
