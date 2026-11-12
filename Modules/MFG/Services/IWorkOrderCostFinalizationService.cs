namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工單成本月末結算（mfg-domain.md §16.3 / roadmap-fin-cst-sub.md §8）。
    /// 系統無「實際費率」資料來源，生產中已以 WorkCenter 標準費率即時累計
    /// ActualLabor/ActualOverhead；本服務於月結時將該期 Preliminary 成本鎖定為 Final，
    /// 防止關帳後再被報工/重工等動作竄改。不重算費率。
    /// </summary>
    public interface IWorkOrderCostFinalizationService
    {
        /// <summary>
        /// 將指定會計期間、指定廠別、CostDate 落在期間內的 Preliminary 工單成本轉為 Final（鎖定）。
        /// 期間已關帳（Closed）則拋 InvalidOperationException。回傳鎖定筆數。
        /// </summary>
        Task<int> FinalizeAsync(string periodId, string plantId, CancellationToken ct = default);
    }
}
