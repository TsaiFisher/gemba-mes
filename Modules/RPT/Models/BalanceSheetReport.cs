namespace SmartFactory.MES.Modules.RPT.Models
{
    public class BalanceSheetReport
    {
        public string AsOfPeriod        { get; init; } = "";
        public decimal TotalAssets      { get; set; }
        public decimal TotalLiabilities { get; set; }
        public decimal TotalEquity      { get; set; }
        public List<BalanceSheetSection> Sections { get; init; } = new();
    }

    public class BalanceSheetSection
    {
        public string Category { get; init; } = "";  // Assets / Liabilities / Equity
        public decimal Total   { get; set; }
        public List<BalanceSheetItem> Items { get; init; } = new();
    }

    public class BalanceSheetItem
    {
        public string  AccountCode { get; init; } = "";
        public string  AccountName { get; init; } = "";
        public decimal Balance     { get; init; }
    }
}
