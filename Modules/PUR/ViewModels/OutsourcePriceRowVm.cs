namespace SmartFactory.MES.Modules.PUR.ViewModels;

/// <summary>
/// 委外加工費比價的一列＝一筆委外收貨明細。
///
/// <para><b>比的是什麼</b>：實際收貨單價（<c>OutsourceReceiptLine.UnitCost</c>，也就是
/// 一路快照到加工費暫估與 AP 的那個值）對上當時**應該**適用的報價
/// （<c>OutsourceQuotation</c> 4-level 查價）。採購那一側的對應物是
/// <c>PurchasePriceRowVm</c>（進貨單價 vs <c>VendorPriceList</c>）。</para>
/// </summary>
public class OutsourcePriceRowVm
{
    public string ProductCode  { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public DateOnly ReceiptDate { get; set; }
    public string OutsourceReceiptId { get; set; } = string.Empty;
    public int LineNo { get; set; }

    /// <summary>實際委外加工費單價（交易幣別）。</summary>
    public decimal ActualUnitPrice { get; set; }

    /// <summary>交易幣別；留空代表建單時未指定（舊單）。</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>當時適用的報價單價（交易幣別）；查無適用報價時為 <c>null</c>。</summary>
    public decimal? QuotedUnitPrice { get; set; }

    /// <summary>報價的幣別。與 <see cref="CurrencyCode"/> 不同時差異不可比，見 <see cref="IsComparable"/>。</summary>
    public string? QuotedCurrencyCode { get; set; }

    /// <summary>被選中的報價單號；查無時為 <c>null</c>。用來回答「這個價是照哪一張報價來的」。</summary>
    public string? QuotationId { get; set; }

    /// <summary>
    /// 差異算不算得出來。**兩種算不出來的情形長得不一樣，不可混為一談**：
    /// 查無報價（廠商根本沒報過這個品號）與幣別不同（報價 USD、收貨 TWD）。
    /// 前者是報價維護的缺口，後者是換算問題——都不該靜默顯示成「差異 0」。
    /// </summary>
    public bool IsComparable =>
        QuotedUnitPrice is > 0 && string.Equals(CurrencyCode ?? "", QuotedCurrencyCode ?? "", StringComparison.Ordinal);

    /// <summary>實際 − 報價；正值＝買貴了。不可比時為 <c>null</c>。</summary>
    public decimal? PriceDiff => IsComparable ? ActualUnitPrice - QuotedUnitPrice!.Value : null;

    /// <summary>差異百分比；不可比時為 <c>null</c>。</summary>
    public decimal? PriceDiffPct => IsComparable && QuotedUnitPrice!.Value != 0
        ? Math.Round((ActualUnitPrice - QuotedUnitPrice.Value) / QuotedUnitPrice.Value * 100, 1)
        : null;
}

/// <summary>
/// 委外加工費的月趨勢列。
///
/// <para>⚠️ <b>單價一律換算成本幣才平均</b>（<c>UnitCost × ExchangeRate</c>，匯率方向見
/// <c>currency.md</c>：本幣＝外幣×Rate）。直接平均交易幣別的單價，等於把 USD 30 和
/// TWD 30 加起來除以二——那個數字沒有任何意義，而它看起來完全正常。
/// 採購金額分析修過同一個形狀的缺陷（多幣別混加）。</para>
/// </summary>
public class OutsourcePriceMonthRowVm
{
    /// <summary>年月（yyyy-MM）。</summary>
    public string YearMonth { get; set; } = string.Empty;

    /// <summary>該月的收貨明細筆數。</summary>
    public int ReceiptLines { get; set; }

    /// <summary>加權平均單價（本幣）。權數是收貨量——不加權的話，一筆 1 個的試作單會和一筆 10000 個的量產單等重。</summary>
    public decimal AvgUnitPriceLocal { get; set; }

    /// <summary>該月最低／最高單價（本幣），用來看離散程度。</summary>
    public decimal MinUnitPriceLocal { get; set; }
    public decimal MaxUnitPriceLocal { get; set; }
}

/// <summary>委外價格分析頁面模型：月趨勢（折線）＋比價明細兩個維度。</summary>
public class OutsourcePriceVm
{
    public List<OutsourcePriceMonthRowVm> MonthRows { get; set; } = new();
    public List<OutsourcePriceRowVm> Rows { get; set; } = new();
}
