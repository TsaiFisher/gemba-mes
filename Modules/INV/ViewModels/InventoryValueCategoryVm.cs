namespace SmartFactory.MES.Modules.INV.ViewModels;

/// <summary>
/// 存貨價值分析的「分類彙總」一列。
///
/// <para><b>刻意沒有數量欄位</b>：同一分類下的品號單位不同（KG／PCS／M），
/// 把 <c>QtyOnHand</c> 加起來得到的數字沒有意義，卻會看起來像個可用的總量。
/// 分類層只彙總金額——金額已由各品號換算為本幣，是唯一可加總的量。</para>
/// </summary>
public class InventoryValueCategoryVm
{
    public string? CategoryCode { get; set; }

    /// <summary>該分類下有庫存（QtyOnHand &gt; 0）的品號數。</summary>
    public int ItemCount { get; set; }

    public decimal TotalValue { get; set; }
    public decimal ValuePct { get; set; }
}
