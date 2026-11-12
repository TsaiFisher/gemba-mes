using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 品號特定單位換算 — 複合主鍵：ProductCode + FromUomCode + ToUomCode。
    ///
    /// <para>換算公式與 <see cref="UomConversion"/> 相同：目標數量 = 來源數量 × (Numerator / Denominator)。
    /// 差別只在**適用範圍**：本表綁定單一品號，<c>UomConversion</c> 是全品號共用。</para>
    ///
    /// <para><b>為什麼需要這張表：</b>「1 箱 = 幾個」是品號的性質，不是單位的性質。
    /// A 品號一箱 12 入、B 品號一箱 24 入，兩者都用 BOX→PC，全域換算表只能記其中一個。
    /// 在本表出現之前，<c>UomService.GetConversionRateAsync</c> 收了 <c>productCode</c> 參數
    /// 卻完全沒有使用它（原始碼留著一行 <c>// 3. TODO: 產品特定換算</c>）。</para>
    ///
    /// <para><b>查找優先序：品號特定 → 全域 → 例外。</b>最specific的先贏，
    /// 這樣全域表可以只放真正與品號無關的換算（KG↔G、M↔CM），品號差異放本表。</para>
    ///
    /// <para>與 <c>Uom</c>／<c>Product</c> 同為**全域主檔**（不實作 <c>IPlantEntity</c>）：
    /// 換算比例是物料本身的性質，不會 A 廠一箱 12 入、B 廠一箱 24 入。</para>
    /// </summary>
    public class ProductUomConversion
    {
        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string FromUomCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string ToUomCode { get; set; } = string.Empty;

        /// <summary>分子</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal Numerator { get; set; } = 1;

        /// <summary>分母</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal Denominator { get; set; } = 1;
    }
}
