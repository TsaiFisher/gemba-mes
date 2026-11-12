using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §7.1 的 4-8：關閉期間之後，對該期間過帳必須被擋。
    ///
    /// 「被擋」只是一半。真正會出事的是**擋下來之後留了什麼**：過帳路徑是
    /// 「寫庫存異動 → 更新在手量 → 建傳票」的多步驟，中間任何一步丟例外而前面幾步
    /// 已經落地的話，帳上會多出一批沒有傳票的庫存異動，而且沒有任何人會知道——
    /// 使用者只看到一則錯誤訊息，回頭再試一次就變成扣兩次。
    /// 所以本測試對每一個入口都同時斷言「擋下來了」與「三張表一筆都沒多」。
    ///
    /// 期間鎖的實作是逐日期而非逐廠：<see cref="AccountingPeriod"/> 沒有 PlantId，
    /// 一關就是全公司一起關。因此本測試挑一個全庫沒有別人用的月份（2096-05），
    /// 並在收尾時刪掉，才不會把別條流程的日期一起鎖死。
    /// </summary>
    [Collection("Integration")]
    public class PeriodLockFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW4K";

        private const string Mat    = "FLOW-W4K-MAT";
        private const string Period = "209605";

        private static readonly DateOnly LockedDate = new(2096, 5, 15);
        private static readonly DateOnly OpenDate   = new(2096, 4, 15);

        private const decimal Qty = 50m, Cost = 30m;

        public PeriodLockFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "期間鎖測試料"));
            using var scope = NewScope(out var db);
            await db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == Period).ExecuteDeleteAsync();
            db.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId = Period, PlantId = Plant, Year = 2096, Month = 5,
                StartDate = new DateOnly(2096, 5, 1), EndDate = new DateOnly(2096, 5, 31),
                Status = PeriodStatus.Closed,
                ClosedAt = DateTime.UtcNow, ClosedBy = UserId,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 關帳期間：庫存過帳與總帳分錄兩個入口都要被擋，且一筆殘留都不能留。
        /// 同一批資料換成未設定期間的日期則必須過得去——守門擋的是關帳，不是全部。
        /// </summary>
        [Fact]
        public async Task ClosedPeriod_BlocksBothInventoryAndJournalEntries_WithoutResidue()
        {
            var before = await SnapshotAsync();

            using (var scope = NewScope(out var db))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostOpeningAsync(scope, db, "FLOW-W4K-LOCKED", LockedDate));
                Assert.Contains(Period, ex.Message);
                PzAssert.Message("Act_PeriodClosedNoPostFmt", ex.Message);
            }

            using (var scope = NewScope(out var db))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    Svc<IJournalEntryService>(scope).CreateAutoEntryAsync(
                        "INV", "FLOW-W4K-LOCKED-J", LockedDate, Plant,
                        new List<JournalLineRequest>
                        {
                            new(StandardAccounts.RawMaterialInventoryCode,
                                StandardAccounts.RawMaterialInventoryName, 100m, 0m),
                            new(StandardAccounts.GrIrAPClearingCode,
                                StandardAccounts.GrIrAPClearingName, 0m, 100m),
                        }));
                Assert.Contains(Period, ex.Message);
            }

            Assert.Equal(before, await SnapshotAsync());

            // 未設定期間的日期（寬鬆模式）仍必須過得去
            using (var scope = NewScope(out var db))
            {
                await PostOpeningAsync(scope, db, "FLOW-W4K-OPEN", OpenDate);
                Assert.Equal(Qty, await QtyOnHandAsync(db, Plant, Mat));
            }
        }

        /// <summary>
        /// 重開帳之後同一筆過帳必須成立。守門若做成單向死鎖，月結後發現的錯帳就永遠補不回去，
        /// 而現場的做法一定是繞過系統直接改庫——那比一開始不鎖還糟。
        /// </summary>
        [Fact]
        public async Task ReopenedPeriod_AcceptsThePostingThatWasBlockedBefore()
        {
            using (var scope = NewScope(out var db))
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostOpeningAsync(scope, db, "FLOW-W4K-REOPEN", LockedDate));

            using (var scope = NewScope(out var db))
                await Svc<IAccountingPeriodService>(scope).ReopenPeriodAsync(Period, Plant, UserId);

            using (var scope = NewScope(out var db))
            {
                await PostOpeningAsync(scope, db, "FLOW-W4K-REOPEN", LockedDate);

                Assert.Equal(Qty, await QtyOnHandAsync(db, Plant, Mat));
                await AssertEveryEntryBalancedAsync(db, Plant);

                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(Qty * Cost, Net(gl, StandardAccounts.RawMaterialInventoryCode));
            }
        }

        public new void Dispose()
        {
            using (var scope = NewScope(out var db))
                db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == Period).ExecuteDelete();
            base.Dispose();
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task PostOpeningAsync(
            IServiceScope scope, ApplicationDbContext db, string docNo, DateOnly date)
        {
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = Qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = date, UomCode = Uom, StockUomCode = Uom, UnitPrice = Cost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            // 過帳被期間鎖擋下時例外會在這一行之前拋出，故不會有半套落地。
            await db.SaveChangesAsync();
        }

        /// <summary>本廠的庫存異動／傳票／在手量三者的合計指紋。</summary>
        private async Task<(int Movements, int Journals, decimal OnHand)> SnapshotAsync()
        {
            using var scope = NewScope(out var db);
            return (
                await db.StockMovements.IgnoreQueryFilters().CountAsync(m => m.PlantId == Plant),
                await db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.PlantId == Plant),
                await QtyOnHandAsync(db, Plant, Mat));
        }
    }
}
