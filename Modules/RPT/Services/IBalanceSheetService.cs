using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public interface IBalanceSheetService
    {
        /// <summary>單一廠別。等同於 <see cref="GetConsolidatedReportAsync"/> 傳一個廠。</summary>
        Task<BalanceSheetReport> GetReportAsync(
            string asOfPeriod, string plantId, CancellationToken ct = default);

        /// <summary>
        /// 多廠合併。
        ///
        /// <para><b>廠際往來不需要另外寫沖銷分錄</b>：跨廠調撥的兩腿是
        /// 「撥出廠 借 1450／貸 1401」與「撥入廠 借 1401／貸 1450」，
        /// 把各廠的分錄直接加總，1450 自己就歸零了。加總之後**還剩下的餘額就是在途存貨**
        /// （已撥出、未收貨），那是個有意義的數字，該標示出來而不是藏掉。</para>
        /// </summary>
        Task<BalanceSheetReport> GetConsolidatedReportAsync(
            string asOfPeriod, IReadOnlyCollection<string> plantIds, CancellationToken ct = default);
    }
}
