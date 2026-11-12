namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>
    /// 合併財報畫面的檢視模型。
    ///
    /// <para><b>為什麼是獨立畫面而不是在資產負債表上加一個「全公司」選項</b>：
    /// 合併資料的可見範圍比使用者本廠大，那是**權限問題**不是顯示選項。
    /// 獨立 MenuKey 才能讓「看得到本廠財報」與「看得到全公司財報」分開授權。</para>
    /// </summary>
    public class ConsolidatedStatementVm
    {
        public string AsOfPeriod { get; set; } = "";
        public string FromPeriod { get; set; } = "";

        /// <summary>本次合併涵蓋的廠別（使用者勾選的，預設全部）。</summary>
        public List<PlantCloseStatusVm> Plants { get; set; } = new();

        public BalanceSheetReport? BalanceSheet { get; set; }
        public ProfitLossReport?   ProfitLoss   { get; set; }

        /// <summary>
        /// 本期損益。資產負債表刻意不做期末結轉（損益科目不併入權益），
        /// 所以「資產 = 負債 + 權益」在報表上本來就不成立，差額正是這個數
        /// （與單廠的資產負債表畫面同一個處理，見 <c>BalanceSheetController.ResolveNetProfitAsync</c>）。
        /// </summary>
        public decimal NetProfit { get; set; }

        /// <summary>
        /// 廠際往來（1450）加總後的殘值＝**在途存貨**（已撥出、未收貨）。
        ///
        /// <para>合併不需要另外寫沖銷分錄：跨廠調撥兩腿一借一貸，加總即歸零。
        /// 剩下的就是還在路上的那批貨的價值——這是個有意義的數字，
        /// 所以單獨列出並說明，而不是混在資產裡讓人以為有一個叫「廠際調撥往來」的資產。</para>
        /// </summary>
        public decimal InterPlantInTransit { get; set; }

        /// <summary>各廠的會計期間狀態不一致（有人已關帳、有人還沒）。</summary>
        public bool HasMixedCloseStatus =>
            Plants.Count > 1 && Plants.Select(p => p.Status).Distinct().Count() > 1;
    }

    /// <summary>單一廠別在該期間的關帳狀態。</summary>
    public class PlantCloseStatusVm
    {
        public string PlantId   { get; set; } = "";
        public string PlantName { get; set; } = "";

        /// <summary>該廠在此期間的狀態；查無期間列時為 null（那一廠沒有建期間）。</summary>
        public Modules.ACT.Models.PeriodStatus? Status { get; set; }
    }
}
