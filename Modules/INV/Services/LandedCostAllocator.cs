using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>分攤基準（規格 §3.4）。</summary>
    public enum LandedCostBasis
    {
        /// <summary>依採購金額比例</summary>
        Amount = 0,
        /// <summary>依採購數量比例</summary>
        Quantity = 1,
        /// <summary>依重量比例</summary>
        Weight = 2,
    }

    /// <summary>分攤輸入：一筆進貨明細。</summary>
    public sealed class LandedCostInputLine
    {
        /// <summary>採購金額（基準 = Amount 時的權重）</summary>
        public decimal PurchaseAmount { get; init; }
        /// <summary>採購數量（基準 = Quantity 時的權重，亦用於回算新單價）</summary>
        public decimal Quantity { get; init; }
        /// <summary>重量（基準 = Weight 時的權重）</summary>
        public decimal Weight { get; init; }
        /// <summary>原入庫單價（分攤前）</summary>
        public decimal OriginalUnitCost { get; init; }
    }

    /// <summary>分攤結果：對應單筆輸入明細。</summary>
    public sealed class LandedCostAllocationResult
    {
        /// <summary>分攤到本行的附帶費用金額</summary>
        public decimal AllocatedCost { get; init; }
        /// <summary>分攤後新入庫單價 = OriginalUnitCost + AllocatedCost / Quantity（Quantity ≤ 0 時維持原單價）</summary>
        public decimal NewUnitCost { get; init; }
    }

    /// <summary>
    /// 採購分攤費用（Landed Cost）分攤計算器（規格 §3.4）。
    /// 純函式靜態類別，不依賴 DB 或注入服務；將國際運費／關稅／保險等外包費用，
    /// 依「採購金額／數量／重量」比例分攤至各進貨明細，並回算新入庫單價（依 IAS 2 滾入存貨）。
    ///
    /// 設計重點：
    ///   * 各行四捨五入後與總額的尾差（residual）一律補進「基準權重最大」的那一行，
    ///     確保 Σ AllocatedCost ≡ totalLandedCost（不漏分、不溢分）。
    ///   * 四捨五入採 AwayFromZero，對齊 C# MidpointRounding 與專案金額慣例。
    /// </summary>
    public static class LandedCostAllocator
    {
        /// <summary>
        /// 將外包費用分攤至各進貨明細。
        /// </summary>
        /// <param name="totalLandedCost">待分攤外包費用總額（≥ 0，與明細同幣別）</param>
        /// <param name="lines">進貨明細（至少一筆，依輸入順序回傳對應結果）</param>
        /// <param name="basis">分攤基準</param>
        /// <param name="decimalPlaces">金額/單價四捨五入位數（預設 4）</param>
        /// <returns>與 <paramref name="lines"/> 同順序、同長度的分攤結果</returns>
        /// <exception cref="ArgumentOutOfRangeException">總額為負，或位數為負</exception>
        /// <exception cref="ArgumentException">明細為空</exception>
        /// <exception cref="InvalidOperationException">所選基準之權重合計 ≤ 0（無法分攤）</exception>
        public static IReadOnlyList<LandedCostAllocationResult> Allocate(
            decimal totalLandedCost,
            IReadOnlyList<LandedCostInputLine> lines,
            LandedCostBasis basis,
            int decimalPlaces = 4)
        {
            if (totalLandedCost < 0m)
                throw new ArgumentOutOfRangeException(nameof(totalLandedCost),
                    PzText.Get("Inv_LandedCostNegativeFmt", totalLandedCost));
            if (decimalPlaces < 0)
                throw new ArgumentOutOfRangeException(nameof(decimalPlaces));
            if (lines is null || lines.Count == 0)
                throw new ArgumentException(PzText.Get("Inv_ReceiptLinesEmpty"), nameof(lines));

            var weights = new decimal[lines.Count];
            for (int i = 0; i < lines.Count; i++)
                weights[i] = BasisWeight(lines[i], basis);

            decimal sumWeight = 0m;
            int maxIdx = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                sumWeight += weights[i];
                if (weights[i] > weights[maxIdx]) maxIdx = i;
            }

            if (sumWeight <= 0m)
                throw new InvalidOperationException(
                    PzText.Get("Inv_AllocationWeightZeroFmt", basis, sumWeight));

            var allocated = new decimal[lines.Count];
            decimal running = 0m;
            for (int i = 0; i < lines.Count; i++)
            {
                allocated[i] = Math.Round(totalLandedCost * weights[i] / sumWeight,
                                          decimalPlaces, MidpointRounding.AwayFromZero);
                running += allocated[i];
            }

            // 尾差補進權重最大的一行，確保 Σ ≡ totalLandedCost
            allocated[maxIdx] += totalLandedCost - running;

            var results = new LandedCostAllocationResult[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                var qty = lines[i].Quantity;
                var newUnit = qty > 0m
                    ? Math.Round(lines[i].OriginalUnitCost + allocated[i] / qty,
                                 decimalPlaces, MidpointRounding.AwayFromZero)
                    : lines[i].OriginalUnitCost;
                results[i] = new LandedCostAllocationResult
                {
                    AllocatedCost = allocated[i],
                    NewUnitCost   = newUnit,
                };
            }
            return results;
        }

        private static decimal BasisWeight(LandedCostInputLine line, LandedCostBasis basis) => basis switch
        {
            LandedCostBasis.Amount   => line.PurchaseAmount,
            LandedCostBasis.Quantity => line.Quantity,
            LandedCostBasis.Weight   => line.Weight,
            _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, PzText.Get("Inv_UnknownAllocationBasis")),
        };
    }
}
