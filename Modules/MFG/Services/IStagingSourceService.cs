using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 備料單的來源帶料服務：由派工單／工單／生產批帶出「還要備多少料」，
    /// 以及該來源的預設備料倉（工作中心設定 &gt; 系統參數）。
    /// 備料單本身屬 INV，但用料需求來自 MFG，故帶料邏輯留在 MFG 側。
    /// </summary>
    public interface IStagingSourceService
    {
        /// <summary>
        /// 帶出備料明細。<paramref name="sourceType"/>：Dispatch | WorkOrder | ProductionBatch。
        /// 需求量一律扣掉「已發料」與「既有未作廢備料單已備」的量，重複開單不會把同一批料重覆鎖兩次。
        /// </summary>
        Task<IReadOnlyList<StagingSourceLine>> GetLinesAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct = default);

        /// <summary>
        /// 解析預設備料倉／儲位：派工／工單 → 工作中心設定 → 系統參數；查無則回傳 (null, null)。
        /// </summary>
        Task<(string? WhCode, string? LocationCode)> ResolveStagingTargetAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct = default);

        /// <summary>
        /// 由來源直接建立草稿備料單（現場備料畫面的一鍵開單），回傳單號。
        /// 無待備料料件時拋 <see cref="InvalidOperationException"/>。
        /// 後續的確認鎖料、揀料移入備料倉仍走備料單畫面，此處只負責把單開出來。
        /// </summary>
        Task<string> CreateDraftAsync(
            string sourceType, string sourceId, string plantId, string userId, CancellationToken ct = default);

        /// <summary>
        /// 該來源目前已存在的備料單（未作廢），供備料畫面顯示狀態、避免重複開單。
        /// </summary>
        Task<IReadOnlyList<StagingOrderBrief>> GetExistingOrdersAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct = default);
    }

    /// <summary>備料單摘要（畫面顯示用）。</summary>
    public class StagingOrderBrief
    {
        public string StagingId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? StagingWhCode { get; set; }
        public string? StagingLocationCode { get; set; }

        /// <summary>明細筆數（[NotMapped]：非表單欄位，不需 R5b 驗證訊息在地化鍵）。</summary>
        [NotMapped]
        public int LineCount { get; set; }
    }

    /// <summary>備料單帶料明細（非 Entity；[NotMapped] 供一致性規則 R5b 判定為非表單欄位）。</summary>
    public class StagingSourceLine
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public string? UomCode { get; set; }
        public string? StockUomCode { get; set; }

        /// <summary>尚須備料的數量（已扣既有發料與既有備料單鎖量）。</summary>
        [NotMapped]
        public decimal RequiredQty { get; set; }

        public string? SourceWorkOrderId { get; set; }
        public string? SourceDispatchId { get; set; }
    }
}
