namespace SmartFactory.MES.Modules.RPT.Models
{
    public class ProfitLossReport
    {
        public string FromPeriod { get; init; } = "";
        public string ToPeriod   { get; init; } = "";
        public decimal Revenue            { get; set; }
        public decimal Cogs               { get; set; }
        public decimal GrossProfit        => Revenue - Cogs;
        public decimal GrossMarginRate    => Revenue == 0 ? 0 : Math.Round(GrossProfit / Revenue * 100, 2);
        public decimal OperatingExpenses  { get; set; }
        public decimal OperatingProfit    => GrossProfit - OperatingExpenses;
        public decimal OtherIncome        { get; set; }
        public decimal OtherExpenses      { get; set; }
        public decimal NetProfit          => OperatingProfit + OtherIncome - OtherExpenses;
        public List<ProfitLossLine> Lines { get; init; } = new();
    }

    public class ProfitLossLine
    {
        public string AccountCode { get; init; } = "";
        public string AccountName { get; init; } = "";
        public string Category    { get; init; } = "";
        public decimal Amount     { get; init; }
    }
}
