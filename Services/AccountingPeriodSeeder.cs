using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 會計期間 seed（每次啟動補齊當月起算一年份的缺口，**每個廠別各一份**）。
    ///
    /// 為什麼需要：AccountingPeriods 原本沒有任何 seeder，期間只能從 ACT「會計期間維護」
    /// 的「建立下一期間」一個月一個月產生。結果是全新部署的期間表是空的，ACT/FIN/CST
    /// 在有人手動產生期間之前完全不能用——成本過帳沒有期間可歸屬、部門實際費用等畫面的
    /// 期別下拉是空的（下拉沒選項的症狀離真因很遠，很難追）。
    ///
    /// <para><b>為什麼不是「只在期間表完全為空時建立」</b>：那是本 seeder 最初的條件，
    /// 實際上等於讓它永遠不執行。只要有任何一筆期間先落地——UITests 的
    /// <c>DepartmentActualCostSmokeTests</c> 那類「確保至少存在一筆會計期間」的前置、
    /// 或有人手動建過一期——seeder 就從此短路，期間永遠停在那一筆。
    /// 實地驗證（2026-08-21）：ProjectZ 庫在 2026-06-30 被寫進一筆 202606，
    /// 之後兩個月 seeder 一次都沒動作，當期（202608）根本不存在，過帳無期間可歸屬。
    /// <b>測試前置資料把正式 seeder 卡死了</b>，而且沒有任何跡象顯示這件事。</para>
    ///
    /// <para>改為<b>補齊缺口</b>：每次啟動檢查「當月起算 <see cref="MonthsToSeed"/> 期」
    /// 之中缺哪些，只新增缺的那幾期。順帶解決「每個月要記得手動建下一期」——
    /// 只要應用程式有啟動過，未來一年的期間就一直是滿的。</para>
    ///
    /// <para><b>只補未來、不回填過去</b>：當月之前若有缺口一律不動。過去期間的缺漏
    /// 代表那段時間本來就沒有營運資料，而補一個 Open 的過去期間會讓「早就該關帳的月份」
    /// 憑空出現一個可過帳的視窗，與 ACT 的期間治理（開關帳、雙重核准）直接衝突。</para>
    ///
    /// <para><b>既有期間一律不修改</b>：只 Add，不更新任何一筆的 Status 或起訖日。
    /// 已關帳的當期不會被這支 seeder 重新打開。</para>
    ///
    /// 建立的期間一律 Open（可過帳）；關帳仍走 ACT 既有流程。
    /// 預設所有環境執行，可用組態 "Security:SeedAccountingPeriods": false 關閉。
    /// </summary>
    public static class AccountingPeriodSeeder
    {
        /// <summary>維持在手的期間數（含當月）。</summary>
        private const int MonthsToSeed = 12;

        public static async Task SeedAsync(IServiceProvider sp)
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AccountingPeriodSeeder");
            var db = sp.GetRequiredService<ApplicationDbContext>();

            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1);

            var wanted = Enumerable.Range(0, MonthsToSeed)
                .Select(i => start.AddMonths(i))
                .ToList();
            var wantedIds = wanted.Select(PeriodIdOf).ToList();

            // 期間自 2026-08-30 起是 IPlantEntity（每廠一份），**必須** IgnoreQueryFilters：
            // seeder 在啟動時跑，沒有登入使用者，CurrentPlantId 是 null，套上全域廠別過濾
            // 會一筆都讀不到 → 判定「全部缺」→ 每次啟動都重插一次 → 撞 PK →
            // 落進下面那個 catch 變成一行警告，看起來像正常運作。
            var plantIds = await db.Plants.AsNoTracking()
                .Select(p => p.Id)
                .ToListAsync();
            if (plantIds.Count == 0) return;   // 廠別主檔還沒建，這時候談期間沒有意義

            var existing = await db.AccountingPeriods
                .AsNoTracking().IgnoreQueryFilters()
                .Where(p => wantedIds.Contains(p.PeriodId))
                .Select(p => new { p.PeriodId, p.PlantId })
                .ToListAsync();
            var existingKeys = existing
                .Select(x => x.PeriodId + "\u0000" + (x.PlantId ?? ""))
                .ToHashSet();

            // 缺口是「期 × 廠」的笛卡爾積：某一廠有 202609 不代表另一廠也有。
            var missing = (from month in wanted
                           from plantId in plantIds
                           where !existingKeys.Contains(PeriodIdOf(month) + "\u0000" + plantId)
                           select (Month: month, PlantId: plantId)).ToList();
            if (missing.Count == 0) return;

            foreach (var (month, plantId) in missing)
            {
                var startDate = new DateOnly(month.Year, month.Month, 1);

                db.AccountingPeriods.Add(new AccountingPeriod
                {
                    PeriodId  = PeriodIdOf(month),
                    PlantId   = plantId,
                    Year      = month.Year,
                    Month     = month.Month,
                    StartDate = startDate,
                    EndDate   = startDate.AddMonths(1).AddDays(-1),
                    Status    = PeriodStatus.Open,
                });
            }

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                // 多個實例同時啟動時，另一個可能已經先寫入同一批期間（PK 撞號）。
                // 期間已存在正是我們要的結果，記錄後放行，不讓啟動流程因此中斷。
                logger.LogWarning(ex,
                    "會計期間補齊時發生寫入衝突（可能是另一個實例已同時建立），略過：{Ids}",
                    string.Join(", ", missing.Select(m => $"{PeriodIdOf(m.Month)}/{m.PlantId}")));
                return;
            }

            logger.LogInformation(
                "會計期間補齊：新增 {Count} 筆期間×廠別（{Ids}）",
                missing.Count,
                string.Join(", ", missing.Select(m => $"{PeriodIdOf(m.Month)}/{m.PlantId}")));
        }

        private static string PeriodIdOf(DateTime month) => $"{month.Year:D4}{month.Month:D2}";
    }
}
