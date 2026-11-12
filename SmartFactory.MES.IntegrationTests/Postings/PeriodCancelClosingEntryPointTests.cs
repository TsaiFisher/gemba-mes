using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.ACT.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 「會計期間」畫面上的月結回頭路（W4-F8）。
    ///
    /// 2026-09-06 人工驗收 4-8 實測：在該畫面按下〈開始月結〉之後，期間進入 Closing，
    /// 一般過帳全部被擋——而畫面上唯一剩下的鈕是〈關帳〉。關帳要關帳前稽核全過，
    /// 稽核異常又多半得靠過帳去修，過帳正被 Closing 擋著。
    ///
    /// 伺服端其實有出口（<c>AccountingPeriodService.CancelClosingAsync</c>），但當時只能
    /// 從「月結作業」畫面呼叫，而且條件是清單步驟 8 為「已完成」。從會計期間頁進 Closing 時
    /// 根本沒有清單；補建清單再按步驟 8 也會因為「狀態為 Closing，無法開始月結」而拋錯。
    /// 於是那顆鈕永遠不會出現——P2 廠 202609 實測確認只剩改資料庫一條路。
    ///
    /// 這一組守的是「按不按得到」，所以渲染真的 HTML：服務層測試對這種缺陷全綠。
    /// 兩個方向都要斷言——只斷言「Closing 時有鈕」的話，把按鈕改成無條件渲染也會過。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class PeriodCancelClosingEntryPointTests : PostingResidueTestBase
    {
        // 刻意用遠期期間：與種子期間（202606–202708）不重疊，且列表依 PeriodId 遞減排序，
        // 一定落在第一頁，不會被既有資料擠掉。
        private const string ClosingPeriod = "209701";
        private const string OpenPeriod    = "209702";

        public PeriodCancelClosingEntryPointTests(AuthedScheduleFactory factory) : base(factory) { }

        public override void Dispose()
        {
            using (var scope = NewScope(out var db))
                db.Database.ExecuteSqlRaw(
                    @"DELETE FROM ""AccountingPeriods"" WHERE ""PeriodId"" IN ('209701','209702');");
            base.Dispose();
        }

        private async Task SeedAsync()
        {
            using var scope = NewScope(out var db);
            db.Database.ExecuteSqlRaw(
                @"DELETE FROM ""AccountingPeriods"" WHERE ""PeriodId"" IN ('209701','209702');");

            db.AccountingPeriods.Add(Period(ClosingPeriod, 1, PeriodStatus.Closing));
            db.AccountingPeriods.Add(Period(OpenPeriod,    2, PeriodStatus.Open));
            await db.SaveChangesAsync();
        }

        private static AccountingPeriod Period(string periodId, int month, PeriodStatus status)
            => new()
            {
                PeriodId  = periodId,
                PlantId   = Plant,
                Year      = 2097,
                Month     = month,
                StartDate = new DateOnly(2097, month, 1),
                EndDate   = new DateOnly(2097, month, 1).AddMonths(1).AddDays(-1),
                Status    = status,
            };

        [Fact]
        public async Task ClosingPeriod_ListRow_ExposesCancelClosingAction()
        {
            await SeedAsync();
            var (client, _) = await NewAuthedClientAsync();

            var html = await client.GetStringAsync("/AccountingPeriod?searched=true&pageSize=50");

            Assert.Contains(ClosingPeriod, html);
            Assert.Contains($"data-action=\"cancelClosing\" data-id=\"{ClosingPeriod}\"", html);
        }

        /// <summary>
        /// 反向：開帳中的期間不得出現〈取消月結〉。少了這一條，把按鈕改成無條件渲染
        /// 也會讓上一條變綠，等於什麼都沒守到。
        /// </summary>
        [Fact]
        public async Task OpenPeriod_ListRow_DoesNotExposeCancelClosingAction()
        {
            await SeedAsync();
            var (client, _) = await NewAuthedClientAsync();

            var html = await client.GetStringAsync("/AccountingPeriod?searched=true&pageSize=50");

            Assert.Contains(OpenPeriod, html);
            Assert.DoesNotContain($"data-action=\"cancelClosing\" data-id=\"{OpenPeriod}\"", html);
            // 開帳中該有的是〈開始月結〉——確認這一列真的被渲染出來了，
            // 否則「找不到取消鈕」可能只是因為整列根本不在頁面上。
            Assert.Contains($"data-action=\"startClosing\" data-id=\"{OpenPeriod}\"", html);
        }
    }
}
