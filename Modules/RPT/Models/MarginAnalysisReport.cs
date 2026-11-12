namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>
    /// 多維度銷售毛利分析：可依「客戶 / 品號 / 月份」維度彙總營收、銷貨成本與毛利率。
    /// 營收來自已確認出貨明細（GoodsShipmentLine），成本來自出貨庫存異動（StockMovement GS）。
    /// </summary>
    public class MarginAnalysisReport
    {
        /// <summary>彙總維度：Customer / Product / Month。</summary>
        public string Dimension { get; set; } = "Product";

        public List<MarginRow> Rows { get; set; } = new();

        public decimal TotalRevenue     => Rows.Sum(r => r.Revenue);
        public decimal TotalCost        => Rows.Sum(r => r.Cost);
        public decimal TotalGrossProfit => Rows.Sum(r => r.GrossProfit);
        public decimal TotalMarginRate  => TotalRevenue == 0 ? 0m
            : Math.Round(TotalGrossProfit / TotalRevenue * 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>單一維度值的毛利列。</summary>
    public class MarginRow
    {
        /// <summary>維度鍵（客戶代碼 / 品號 / yyyy-MM）。</summary>
        public string DimensionKey  { get; init; } = "";

        /// <summary>維度顯示名（客戶名稱 / 品名 / 月份）。</summary>
        public string DimensionName { get; init; } = "";

        public decimal Revenue   { get; init; }
        public decimal Cost      { get; init; }
        public int     ShipCount { get; init; }

        public decimal GrossProfit     => Revenue - Cost;
        public decimal GrossMarginRate => Revenue == 0 ? 0m
            : Math.Round(GrossProfit / Revenue * 100m, 2, MidpointRounding.AwayFromZero);
    }
}
