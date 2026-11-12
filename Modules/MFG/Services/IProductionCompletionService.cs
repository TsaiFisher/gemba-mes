namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 完工入庫服務（mfg-domain.md §5.4 / §17.2 / §16.3 Phase F）。
    /// WorkOrder.Status = Completed → 入庫 + Backflush 後沖 + 成本回寫 + Reservation 釋放 → Closed。
    /// </summary>
    public interface IProductionCompletionService
    {
        /// <summary>
        /// 執行完工入庫：
        ///   1. 計算 QtyDone＝最後一道工序的完工量（WorkOrderOutput）
        ///   2. 建立 IT（工單繳庫）StockMovement，並更新 Inventory
        ///   3. Backflush 後沖 IO（AtCompletion 的 WorkOrderMaterial）
        ///   4. 回寫 WorkOrder.ActualMaterialCost
        ///   5. 釋放 InventoryReservation
        ///   6. WorkOrder.Status = Closed
        /// </summary>
        Task CompleteAsync(
            string workOrderId,
            string plantId,
            string whCode,
            string locationCode,
            string? lotNo = null,
            CancellationToken ct = default);

        /// <summary>
        /// 分箱完工入庫（container-tracking.md §8 L1+）：完工量依每箱數量拆成多行入庫，一箱一個批號
        /// （<see cref="CompletionBoxing.BoxLots"/>）。其餘步驟與上一個多載完全相同。
        /// <paramref name="boxing"/> 為 null 等同上一個多載。
        /// </summary>
        Task CompleteAsync(
            string workOrderId,
            string plantId,
            string whCode,
            string locationCode,
            string? lotNo,
            CompletionBoxing? boxing,
            CancellationToken ct = default);

        /// <summary>
        /// 純函式輔助：計算 Backflush 後沖用料量。
        /// ActualIssued = RequiredQty × (qtyDone / woQuantity)。
        /// </summary>
        static decimal CalcBackflushQty(decimal requiredQty, decimal qtyDone, decimal woQuantity)
            => woQuantity <= 0 ? 0 : requiredQty * (qtyDone / woQuantity);
    }

    /// <summary>
    /// 分箱入庫參數。<see cref="ContainerCode"/> 只印在箱標籤上供辨識，不進庫存帳（容器個體追蹤屬 L3）。
    /// </summary>
    /// <param name="UnitIds">指定裝貨的箱號（container-tracking.md §9，箱子重複使用）：數量須等於箱數，依序綁到第 1、2…箱的批號；null／空＝不綁箱子</param>
    /// <param name="UserId">箱子歷程的操作者</param>
    public sealed record CompletionBoxing(decimal BoxQty, string? ContainerCode = null,
        IReadOnlyList<string>? UnitIds = null, string? UserId = null)
    {
        /// <summary>單次分箱上限。防止每箱數量打錯（例如少一個 0）時一口氣產生上萬行異動。</summary>
        public const int MaxBoxes = 999;

        /// <summary>
        /// 純函式：完工量 → 每箱的（批號, 數量）。批號＝前綴（使用者填的批號，空白則用工單號）＋「-」＋序號，
        /// 序號至少兩位、依箱數補零（12 箱→-01…-12；120 箱→-001…-120），讓批號照字串排序也是箱序。
        /// 最後一箱裝餘數。
        /// </summary>
        public static IReadOnlyList<(string LotNo, decimal Qty)> BoxLots(decimal qtyDone, decimal boxQty, string prefix)
        {
            if (boxQty <= 0) throw new ArgumentOutOfRangeException(nameof(boxQty));
            var count = (int)Math.Ceiling(qtyDone / boxQty);
            var width = Math.Max(2, count.ToString().Length);
            var result = new List<(string, decimal)>(count);
            for (var i = 1; i <= count; i++)
            {
                var qty = i < count ? boxQty : qtyDone - boxQty * (count - 1);
                result.Add(($"{prefix}-{i.ToString().PadLeft(width, '0')}", qty));
            }
            return result;
        }
    }
}
