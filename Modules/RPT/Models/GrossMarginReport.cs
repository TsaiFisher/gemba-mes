namespace SmartFactory.MES.Modules.RPT.Models
{
    public class GrossMarginReport
    {
        public List<GrossMarginRow> Rows { get; init; } = new();
        public decimal TotalRevenue     => Rows.Sum(r => r.Revenue);
        public decimal TotalCost        => Rows.Sum(r => r.Cost);
        public decimal TotalGrossProfit => Rows.Sum(r => r.GrossProfit);
        public decimal TotalMarginRate  => TotalRevenue == 0 ? 0
            : Math.Round(TotalGrossProfit / TotalRevenue * 100, 2);
    }

    public class GrossMarginRow
    {
        public string  ProductCode     { get; init; } = "";
        public decimal Revenue         { get; init; }
        public decimal Cost            { get; init; }
        public decimal GrossProfit     => Revenue - Cost;
        public decimal GrossMarginRate => Revenue == 0 ? 0
            : Math.Round(GrossProfit / Revenue * 100, 2);
        public int     ShipCount       { get; init; }
    }
}
