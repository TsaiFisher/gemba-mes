using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>
    /// 委外重工成本/索賠計算器（pur-domain.md §4.2，2026-07-09 責任歸屬＋雙路徑定案）。
    /// 純函式靜態類別，不依賴 DB 或注入服務。
    ///
    /// 責任歸屬決定成本流向：
    ///   Vendor（廠商責任）→ 向廠商扣款 VendorChargebackAmt＝重工成本全額，加工費 0
    ///   Us（我方責任）    → 應付加工費 ReworkProcessingFee＝加工費全額，扣款 0
    ///   Shared（共同）    → 扣款＝重工成本 × 廠商占比、加工費＝加工費 × 我方占比
    /// </summary>
    public static class OutsourceReworkCostCalculator
    {
        /// <summary>計算結果（占比與雙路徑金額）。</summary>
        /// <param name="VendorLiablePct">廠商責任占比（0–100；Vendor=100、Us=0）</param>
        /// <param name="VendorChargebackAmt">向廠商扣款/索賠金額</param>
        /// <param name="ReworkProcessingFee">我方應付重工加工費</param>
        public readonly record struct ReworkCostResult(
            decimal VendorLiablePct, decimal VendorChargebackAmt, decimal ReworkProcessingFee);

        /// <summary>
        /// 依責任歸屬計算雙路徑金額。
        /// </summary>
        /// <param name="party">責任歸屬</param>
        /// <param name="vendorLiablePct">Shared 時的廠商分攤比例（0–100）；Vendor/Us 時忽略、由歸屬決定</param>
        /// <param name="reworkCost">重工成本全額（扣款計算基礎，≥ 0）</param>
        /// <param name="processingFee">重工加工費全額（應付計算基礎，≥ 0）</param>
        /// <param name="amountDecimals">金額四捨五入位數（本位幣小數位，預設 4 對齊 decimal(18,4)）</param>
        /// <exception cref="ArgumentOutOfRangeException">金額為負、Shared 占比超出 0–100</exception>
        public static ReworkCostResult Compute(
            ReworkLiabilityParty party,
            decimal vendorLiablePct,
            decimal reworkCost,
            decimal processingFee,
            int amountDecimals = 4)
        {
            if (reworkCost < 0)
                throw new ArgumentOutOfRangeException(nameof(reworkCost),
                    PzText.Get("Pur_ReworkCostNegativeFmt", reworkCost));
            if (processingFee < 0)
                throw new ArgumentOutOfRangeException(nameof(processingFee),
                    PzText.Get("Pur_ReworkFeeNegativeFmt", processingFee));

            decimal pct = party switch
            {
                ReworkLiabilityParty.Vendor => 100m,
                ReworkLiabilityParty.Us     => 0m,
                _                           => vendorLiablePct,
            };
            if (pct is < 0m or > 100m)
                throw new ArgumentOutOfRangeException(nameof(vendorLiablePct),
                    PzText.Get("Pur_SupplierShareRangeFmt", vendorLiablePct));

            decimal chargeback = Math.Round(reworkCost * pct / 100m, amountDecimals, MidpointRounding.AwayFromZero);
            decimal fee        = Math.Round(processingFee * (100m - pct) / 100m, amountDecimals, MidpointRounding.AwayFromZero);
            return new ReworkCostResult(pct, chargeback, fee);
        }
    }
}
