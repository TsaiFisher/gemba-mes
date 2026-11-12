using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>
    /// 委外超耗扣款計算器（§5.2）。
    /// 純函式靜態類別，不依賴 DB 或注入服務，可直接在 Controller / Service 呼叫。
    ///
    /// 計算公式：
    ///   實際損耗率 = 1 − (receivedQty / issuedQty)
    ///   超耗比率   = max(0, 實際損耗率 − allowedScrapRate)
    ///   超耗數量   = 超耗比率 × issuedQty
    ///   罰款金額   = Round(超耗數量 × materialUnitCost, 4, AwayFromZero)
    /// </summary>
    public static class OutsourceScrapCalculator
    {
        /// <summary>
        /// 計算委外超耗扣款金額。
        /// </summary>
        /// <param name="issuedQty">發給委外商的原料數量（必須 &gt; 0）</param>
        /// <param name="receivedQty">收回的成品數量（≥ 0；以 1:1 BOM 比計算，多品 BOM 請先換算等效數量）</param>
        /// <param name="allowedScrapRate">合約允收損耗率（0–1，例：0.05 表示 5%）</param>
        /// <param name="materialUnitCost">原料單位成本（用於折算罰款金額）</param>
        /// <returns>應扣款金額（≥ 0）；呼叫端若扣款超過加工費，自行決定截斷或轉 AR</returns>
        /// <exception cref="ArgumentOutOfRangeException">issuedQty ≤ 0 或 receivedQty &lt; 0</exception>
        /// <exception cref="InvalidOperationException">實際損耗率 &lt; 0（成品量超出發料量，超產或資料錯誤）</exception>
        public static decimal Calculate(
            decimal issuedQty,
            decimal receivedQty,
            decimal allowedScrapRate,
            decimal materialUnitCost)
        {
            if (issuedQty <= 0)
                throw new ArgumentOutOfRangeException(nameof(issuedQty),
                    PzText.Get("Pur_IssuedQtyPositiveFmt", issuedQty));

            if (receivedQty < 0)
                throw new ArgumentOutOfRangeException(nameof(receivedQty),
                    PzText.Get("Pur_ReturnedFgQtyNegativeFmt", receivedQty));

            decimal actualScrapRate = 1m - (receivedQty / issuedQty);

            if (actualScrapRate < 0m)
                throw new InvalidOperationException(
                    PzText.Get("Pur_NegativeScrapRateFmt", actualScrapRate, receivedQty, issuedQty));

            decimal excessRate = actualScrapRate - allowedScrapRate;
            if (excessRate <= 0m) return 0m;

            decimal excessQty = excessRate * issuedQty;
            return Math.Round(excessQty * materialUnitCost, 4, MidpointRounding.AwayFromZero);
        }

        /// <summary>計算實際損耗率（輔助方法，供 UI 顯示）</summary>
        public static decimal GetActualScrapRate(decimal issuedQty, decimal receivedQty)
        {
            if (issuedQty <= 0)
                throw new ArgumentOutOfRangeException(nameof(issuedQty));
            return 1m - (receivedQty / issuedQty);
        }

        /// <summary>單一原料的超耗計算參數（§1.5 逐料計算用）。</summary>
        /// <param name="IssuedQty">該原料實際發料量（原料庫存單位，來自 OO 發料 StockMovement）</param>
        /// <param name="AvcoUnitCost">該原料發料時點 AVCO 單價（StockMovement.UnitCost 快照，數量加權）</param>
        /// <param name="BomUsagePerUnit">每一成品單位的 BOM 標準用量（RoutingBom.Quantity）</param>
        public readonly record struct MaterialUsage(
            decimal IssuedQty, decimal AvcoUnitCost, decimal BomUsagePerUnit);

        /// <summary>
        /// 多料 BOM 版超耗扣款（§1.5）。逐原料以「發料時點 AVCO」計價後加總。
        ///
        /// 逐料公式：
        ///   預期耗用 = 實收成品量 × BOM 標準用量
        ///   損耗量   = 實際發料量 − 預期耗用
        ///   罰款     = max(0, 損耗量 − 實際發料量 × 允收損耗率) × 該料 AVCO
        ///
        /// 與單料 <see cref="Calculate"/> 對齊：把「預期耗用」當作等效 receivedQty 傳入即等價。
        /// 預期耗用超過發料量（BOM/資料異常導致損耗率為負）時，該料罰款以 0 計，不中斷整批。
        /// </summary>
        /// <param name="receivedFinishedQty">實收成品量（成品單位，≥ 0）</param>
        /// <param name="allowedScrapRate">合約允收損耗率（0–1）</param>
        /// <param name="materials">該工單各發料原料的用量/成本參數</param>
        /// <returns>合計應扣款金額（≥ 0）</returns>
        public static decimal CalculateMultiMaterial(
            decimal receivedFinishedQty,
            decimal allowedScrapRate,
            IEnumerable<MaterialUsage> materials)
        {
            if (receivedFinishedQty < 0)
                throw new ArgumentOutOfRangeException(nameof(receivedFinishedQty),
                    PzText.Get("Pur_ReceivedFgQtyNegativeFmt", receivedFinishedQty));
            if (materials is null) return 0m;

            decimal total = 0m;
            foreach (var m in materials)
            {
                if (m.IssuedQty <= 0) continue;   // 無實際發料的料不計超耗

                decimal expectedConsumed = receivedFinishedQty * m.BomUsagePerUnit;
                if (expectedConsumed >= m.IssuedQty) continue;   // 損耗 ≤ 0，無超耗

                total += Calculate(m.IssuedQty, expectedConsumed, allowedScrapRate, m.AvcoUnitCost);
            }
            return total;
        }
    }
}
