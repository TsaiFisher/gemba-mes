namespace SmartFactory.MES.Modules.INV.ViewModels
{
    /// <summary>庫存異動帳本（單一品號 + 期間，含期初/逐筆結存）。唯讀報表。</summary>
    public class StockLedgerVm
    {
        public string ProductCode  { get; set; } = "";
        public string ProductName  { get; set; } = "";
        public string StockUomCode { get; set; } = "";

        public DateOnly? DateFrom   { get; set; }
        public DateOnly? DateTo     { get; set; }
        public string?   LocationCode { get; set; }
        public string?   LotNo        { get; set; }

        /// <summary>期初結存（起始日之前所有異動累計）</summary>
        public decimal OpeningBalance { get; set; }
        /// <summary>期末結存</summary>
        public decimal ClosingBalance { get; set; }
        /// <summary>期間入庫合計</summary>
        public decimal TotalIn  { get; set; }
        /// <summary>期間出庫合計</summary>
        public decimal TotalOut { get; set; }

        public List<StockLedgerRowVm> Rows { get; set; } = new();

        /// <summary>明細是否因超過上限被截斷（彙總數字仍為全期間 SQL 聚合，正確不受影響）。</summary>
        public bool Truncated { get; set; }
        /// <summary>明細列數上限。</summary>
        public int  RowCap    { get; set; }
    }

    public class StockLedgerRowVm
    {
        public DateOnly MovementDate { get; set; }
        public string   TypeCode     { get; set; } = "";
        /// <summary>摘要（異動類別名稱）</summary>
        public string   Summary      { get; set; } = "";
        public string   DocNo        { get; set; } = "";
        public string?  SourceDocNo  { get; set; }
        /// <summary>入庫數量（庫存單位；方向為 +1 時 = StockQty，否則 0）</summary>
        public decimal  InQty        { get; set; }
        /// <summary>出庫數量（庫存單位；方向為 -1 時 = StockQty，否則 0）</summary>
        public decimal  OutQty       { get; set; }
        /// <summary>逐筆結存</summary>
        public decimal  Balance      { get; set; }
        /// <summary>經辦人（顯示名稱）</summary>
        public string?  Handler      { get; set; }
        public string?  Remark       { get; set; }
    }
}
