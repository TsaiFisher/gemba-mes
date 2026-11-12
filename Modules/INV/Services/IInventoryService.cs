using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 庫存餘額查詢、更新、成本計算服務。
    /// 由 IInventoryPostingService 或 Controller 呼叫。
    /// </summary>
    public interface IInventoryService
    {
        /// <summary>取得指定品號+儲位+批號的庫存量</summary>
        Task<decimal> GetQtyOnHandAsync(string plantId, string productCode, string locationCode, string lotNo = "", CancellationToken ct = default);

        /// <summary>取得指定品號+儲位+批號的可用量（= QtyOnHand - QtyReserved）</summary>
        Task<decimal> GetAvailableQtyAsync(string plantId, string productCode, string locationCode, string lotNo = "", CancellationToken ct = default);

        /// <summary>
        /// 更新庫存數量（正數=入庫，負數=出庫）。
        /// 若 Inventory 記錄不存在則自動建立。
        /// 使用 PostgreSQL xmin 樂觀鎖防併發。
        /// </summary>
        Task UpdateQtyAsync(string plantId, string productCode, string locationCode, string lotNo, decimal deltaQty, CancellationToken ct = default);

        /// <summary>
        /// 更新移動加權平均成本（入庫時呼叫）。
        /// 新均價 = (舊量 × 舊均價 + 入庫量 × 入庫單價) / (舊量 + 入庫量)
        /// </summary>
        Task UpdateMovingAverageCostAsync(string plantId, string productCode, string locationCode, string lotNo, decimal addQty, decimal inUnitCost, CancellationToken ct = default);

        /// <summary>
        /// 反向更新移動加權平均成本（作廢一筆**入庫**時呼叫）。
        /// 新均價 = (舊量 × 舊均價 − 沖銷量 × 該筆入庫單價) / (舊量 − 沖銷量)
        ///
        /// 這是 <see cref="UpdateMovingAverageCostAsync"/> 的逐項反函式：帳一致時，
        /// 沖掉哪一筆就精確還原成那筆入庫之前的均價。
        /// **必須在扣回數量之前呼叫**——公式裡的「舊量」是尚未扣掉沖銷量的在手量，
        /// 順序顛倒會把沖銷量重複扣兩次。
        /// </summary>
        Task ReverseMovingAverageCostAsync(string plantId, string productCode, string locationCode, string lotNo, decimal removeQty, decimal inUnitCost, CancellationToken ct = default);

        /// <summary>
        /// 該儲位是否屬客供倉（<c>WarehouseType.CustomerSupplied</c>）。
        ///
        /// <para>客供料屬客戶財產，**不參與我們的存貨計價**：不進移動平均的分母、
        /// 不被均價寫回、不建 FIFO 成本層，<c>Inventory.UnitCost</c> 恆為 0。
        /// 判斷收斂在這一支，是為了讓「什麼算客供」只有一個答案——
        /// 計價、過帳與單據驗證三處若各判一次，遲早會分岔，
        /// 而分岔的症狀是客供料在某一條路徑上「有了成本」。</para>
        /// </summary>
        Task<bool> IsCustomerSuppliedLocationAsync(string locationCode, CancellationToken ct = default);

        /// <summary>取得儲位資訊</summary>
        Task<StorageLocation?> GetLocationAsync(string locationCode, CancellationToken ct = default);

        /// <summary>取得或建立 Inventory 記錄</summary>
        Task<Inventory> GetOrCreateInventoryAsync(string plantId, string productCode, string locationCode, string lotNo, CancellationToken ct = default);
    }
}
