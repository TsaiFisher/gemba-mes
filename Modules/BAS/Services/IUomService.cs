namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 單位換算與小數位數服務。
    /// </summary>
    public interface IUomService
    {
        /// <summary>
        /// 執行單位換算。
        /// </summary>
        /// <param name="productCode">品號（有些換算可能與產品相關，目前先保留參數，實作暫以通用為主）</param>
        /// <param name="fromUom">來源單位</param>
        /// <param name="toUom">目標單位</param>
        /// <param name="qty">來源數量</param>
        /// <returns>換算後的目標數量</returns>
        Task<decimal> ConvertAsync(string productCode, string fromUom, string toUom, decimal qty, CancellationToken ct = default);

        /// <summary>
        /// 取得換算率（Target = Source * Rate）。
        /// </summary>
        Task<decimal> GetConversionRateAsync(string productCode, string fromUom, string toUom, CancellationToken ct = default);

        /// <summary>
        /// 取得指定單位的小數位數（找不到或空代碼回傳 0）。
        /// </summary>
        Task<int> GetDecimalPlacesAsync(string? uomCode, CancellationToken ct = default);

        /// <summary>
        /// 取得所有單位的小數位數對照表（含停用；供表單初始化 ViewBag，一次載入快取）。
        /// 停用只該擋新單據挑選，不該讓既有單據的記數方式跟著改變。
        /// </summary>
        Task<IReadOnlyDictionary<string, int>> GetAllDecimalPlacesAsync(CancellationToken ct = default);

        /// <summary>
        /// 依單位小數位數將數量四捨五入（MidpointRounding.AwayFromZero）。
        /// 空代碼或找不到時視同 dp=0，即四捨五入成整數——不是回傳原值。
        /// 未設定單位的品項若能保留原精度，等於繞過所有精度控制。
        /// </summary>
        Task<decimal> RoundQtyAsync(string? uomCode, decimal qty, CancellationToken ct = default);

        /// <summary>
        /// 品號可用的單位：庫存單位，加上每一個與庫存單位之間查得到換算（品號特定或全域、正向或逆向）的單位。
        /// 判定規則與 <see cref="GetConversionRateAsync"/> 完全一致——列出來的單位換算一定成功，沒列的一定失敗。
        /// 品號不存在回 null；品號沒有庫存單位時只回採購／銷售單位中有填的（無法換算，不擴充）。
        /// </summary>
        Task<ProductUomOptions?> GetProductUomsAsync(string productCode, CancellationToken ct = default);

        /// <summary>
        /// 把單據明細的數量換算成**品號主檔目前的庫存單位**並依其小數位數捨入。
        /// 庫存單位一律取主檔，不採信畫面送來的值（畫面只會送它當下看到的那一份，可能已過時或被改）。
        /// 單位與庫存單位相同（或沒填）時不換算；查不到換算時擲 <see cref="UomConversionNotFoundException"/>。
        /// 品號沒有庫存單位時以明細單位為準（相容舊資料）。
        /// </summary>
        Task<(string StockUom, decimal StockQty)> ToStockQtyAsync(string productCode, string? uomCode, decimal qty, CancellationToken ct = default);

        /// <summary>在 UOM 建立/修改/刪除後呼叫，清除小數位數快取。</summary>
        void InvalidateDecimalPlacesCache();
    }

    /// <summary>品號可用單位（<see cref="IUomService.GetProductUomsAsync"/>）。Uoms 依「庫存→採購→銷售→其餘代碼排序」排列。</summary>
    public sealed record ProductUomOptions(string? StockUom, string? PurchaseUom, string? SalesUom, IReadOnlyList<string> Uoms);
}
