namespace SmartFactory.MES.Modules.BAS.Models
{
    public enum WarehouseType
    {
        Raw,         // 原料倉
        Wip,         // 在製倉
        Finished,    // 成品倉
        Quarantine,  // 待驗倉（品檢放行前強制）
        Outsource,   // 委外倉（OUT 出料目的地）
        Scrap,       // 報廢倉（QJ / 損耗）
        // 備料倉（備料單移入的待領用區）。⚠️ 與待驗倉不同，備料倉庫存**仍計入**廠級可用量：
        // 那批料已被備料單的 InventoryReservation 鎖住，若比照 Quarantine 在
        // GetPlantAvailableQtyAsync 再排除一次，等於同一批料扣兩次，全廠可用量會憑空短少。
        Staging,

        /// <summary>
        /// 客供倉（`CI` 客供進貨／`CR` 客供退回的唯一合法去處）。
        ///
        /// <para><b>它存在的理由是成本而不是動線</b>：客供料是客戶提供自己的料做該客戶訂單之用，
        /// 所有權屬客戶，**不是我們的資產也不是我們的成本**。而移動平均的成本池是
        /// (PlantId, ProductCode)——分母取該品號全廠在手量，新均價寫回該品號所有 Inventory 列
        /// （見 `InventoryService.UpdateMovingAverageCostAsync`）。所以沒有這個維度時，
        /// 客供數量會進到均價分母、把自有料單價壓低，並擴散到之後每一張出貨的 COGS。</para>
        ///
        /// <para><b>數量要管、成本不算</b>——這是它與 `Quarantine` 的差別：
        /// 待驗倉排除的是**可用量**（IQC 沒放行不准動），客供倉排除的是**計價**
        /// （料可以正常領用去做該客戶的訂單，只是它的價值不進我們的存貨）。
        /// 因此 `GetAvailableQtyAsync`（儲位級）**不**排除客供倉。</para>
        ///
        /// <para>本倉庫存的 `Inventory.UnitCost` 恆為 0，所以六處存貨金額報表
        /// （評價／週轉／帳齡／呆滯／儀表板）不必逐一加排除條件就自動正確。</para>
        ///
        /// <para>⚠️ `WarehouseType` 以 int 序數持久化，新值只能加在**最後**——
        /// 插在中間會讓既有倉庫的類型整批位移，而位移後每一筆都還是合法值，沒有任何訊號。</para>
        /// </summary>
        CustomerSupplied
    }
}
