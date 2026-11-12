using SmartFactory.MES.Modules._Shared;
namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工單在製品（WIP）平衡公式計算器（§1.3.C.1 / §5.8）。純函式，不接 DB。
    /// 期末 WIP = 期初 WIP + 淨發料 + 人工製費 − 完工入庫成本 − 報廢成本
    /// </summary>
    public static class WipBalanceCalculator
    {
        /// <summary>
        /// 計算期末在製品金額。
        /// </summary>
        /// <param name="beginningWip">期初在製品金額</param>
        /// <param name="netMaterialIssued">淨發料金額（發料 − 退料），不可為負</param>
        /// <param name="laborAndOverhead">本期人工與製費合計，不可為負</param>
        /// <param name="completionCost">完工入庫成本，不可為負</param>
        /// <param name="scrapCost">報廢金額（預設 0），不可為負</param>
        public static decimal ComputeEndingWip(
            decimal beginningWip,
            decimal netMaterialIssued,
            decimal laborAndOverhead,
            decimal completionCost,
            decimal scrapCost = 0m)
        {
            if (netMaterialIssued < 0)
                throw new ArgumentOutOfRangeException(nameof(netMaterialIssued),
                    PzText.Get("Mfg_WipNetIssueNegative"));
            if (laborAndOverhead < 0)
                throw new ArgumentOutOfRangeException(nameof(laborAndOverhead),
                    PzText.Get("Mfg_WipLaborOverheadNegative"));
            if (completionCost < 0)
                throw new ArgumentOutOfRangeException(nameof(completionCost),
                    PzText.Get("Mfg_WipCompletionCostNegative"));
            if (scrapCost < 0)
                throw new ArgumentOutOfRangeException(nameof(scrapCost),
                    PzText.Get("Mfg_WipScrapNegative"));

            return beginningWip + netMaterialIssued + laborAndOverhead - completionCost - scrapCost;
        }

        /// <summary>
        /// 計算三項成本差異總和。正數表示超支，負數表示節省。
        /// </summary>
        public static decimal ComputeTotalVariance(
            decimal actualMaterial,  decimal standardMaterial,
            decimal actualLabor,     decimal standardLabor,
            decimal actualOverhead,  decimal standardOverhead)
        {
            return (actualMaterial  - standardMaterial)
                 + (actualLabor     - standardLabor)
                 + (actualOverhead  - standardOverhead);
        }
    }
}
