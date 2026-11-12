using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §8 的 5-7：走完月結 Checklist → 關帳。
    /// 作業書把「關帳前會自動跑全稽核，有異常就關不了帳」寫成設計行為——那沒錯，
    /// 問題在於**擋下來之後怎麼辦**。
    ///
    /// 月結步驟 8「封鎖庫存異動」會把期間推進 <c>Closing</c>，而 Closing 會擋掉
    /// 所有一般過帳（只放行 <c>JournalEntryType.Adjustment</c> 的手工調整分錄）。
    /// 於是只要進了步驟 8 之後才發現稽核不過，就成了一個閉環：
    ///
    ///   關帳要稽核過 → 稽核異常多半得靠過帳去修（補開票、補認列、沖銷、調整庫存）
    ///   → 過帳被 Closing 擋 → 期間永遠停在 Closing。
    ///
    /// 而 <see cref="IAccountingPeriodService"/> 沒有任何回頭路：
    /// <c>StartClosingAsync</c> 只收 Open/Reopened，<c>ReopenPeriodAsync</c> 只收 Closed，
    /// Closing **不是任何一個轉換的來源狀態**。唯一的出路是直接改資料庫。
    ///
    /// 本測試不依賴「全公司稽核為零異常」——`RunPreClosingAuditAsync` 是
    /// `RunAllAsync(plantId: null)`，跨全庫，別條流程的殘留會讓它隨機紅。
    /// 這裡釘的是狀態機本身：進得去就要出得來。
    /// </summary>
    [Collection("Integration")]
    public class MonthEndFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW5M";

        private const string Mat    = "FLOW-W5M-MAT";
        private const string Period = "209507";

        private static readonly DateOnly InPeriod = new(2095, 7, 15);

        private const decimal Qty = 20m, Cost = 15m;

        private string ChecklistId => $"{Period}-{Plant}";

        public MonthEndFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "月結測試料"));
            using var scope = NewScope(out var db);
            await db.ChecklistItems.Where(i => i.ChecklistId == ChecklistId).ExecuteDeleteAsync();
            await db.MonthEndChecklists.Where(c => c.ChecklistId == ChecklistId).ExecuteDeleteAsync();
            await db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == Period).ExecuteDeleteAsync();
            db.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId = Period, PlantId = Plant, Year = 2095, Month = 7,
                StartDate = new DateOnly(2095, 7, 1), EndDate = new DateOnly(2095, 7, 31),
                Status = PeriodStatus.Open,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 進入月結封鎖之後必須出得來。
        ///
        /// 這是這條流程唯一會讓現場「只能找工程師改資料庫」的狀態，
        /// 而它發生的條件一點都不罕見：步驟 8 打勾之後才跑稽核發現有異常。
        /// </summary>
        [Fact]
        public async Task ClosingState_MustBeExitable_OtherwiseAFailedAuditStrandsThePeriodForever()
        {
            using (var scope = NewScope(out var db))
            {
                var monthEnd = Svc<IMonthEndService>(scope);
                await monthEnd.InitChecklistAsync(Period, Plant);
                // 月結步驟必須依序執行，先把 1–7 走完
                for (var n = 1; n <= 7; n++)
                    await monthEnd.CompleteStepAsync(ChecklistId, n, UserId);
                // 步驟 8「封鎖庫存異動」→ 自動觸發 StartClosing
                await monthEnd.CompleteStepAsync(ChecklistId, 8, UserId);
            }

            using (var scope = NewScope(out var db))
            {
                var period = await db.AccountingPeriods.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.PeriodId == Period);
                Assert.Equal(PeriodStatus.Closing, period.Status);

                // 前提：Closing 的確擋掉一般過帳——稽核異常多半就是要靠這個修
                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostAsync(scope, db, "FLOW-W5M-FIX"));
                PzAssert.Message("Act_PeriodClosingNoPostFmt", blocked.Message);
            }

            // 回頭路：取消月結，期間回到可過帳
            using (var scope = NewScope(out var db))
                await Svc<IMonthEndService>(scope).CancelClosingAsync(ChecklistId, UserId);

            using (var scope = NewScope(out var db))
            {
                var period = await db.AccountingPeriods.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.PeriodId == Period);
                Assert.Equal(PeriodStatus.Open, period.Status);

                // 現在才修得掉稽核異常
                await PostAsync(scope, db, "FLOW-W5M-FIX");
                Assert.Equal(Qty, await QtyOnHandAsync(db, Plant, Mat));

                // 清單上的步驟 8 必須跟著退回未完成，否則畫面顯示「已封鎖」而實際沒鎖
                var step8 = await db.ChecklistItems.AsNoTracking()
                    .SingleAsync(i => i.ChecklistId == ChecklistId && i.StepNo == 8);
                Assert.Equal(ChecklistItemStatus.Pending, step8.Status);
            }

            // 修完之後可以再走一次月結
            using (var scope = NewScope(out var db))
            {
                await Svc<IMonthEndService>(scope).CompleteStepAsync(ChecklistId, 8, UserId);
                var period = await db.AccountingPeriods.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.PeriodId == Period);
                Assert.Equal(PeriodStatus.Closing, period.Status);
            }
        }

        /// <summary>
        /// 取消月結只對「正在月結中」的期間有意義：
        /// 尚未開始的不能取消（沒有東西可取消），已關帳的要走重開帳（那是另一道需要核准的動作），
        /// 否則這個入口就變成繞過重開帳的後門。
        /// </summary>
        [Fact]
        public async Task CancelClosing_IsNotABackDoorAroundReopen()
        {
            using (var scope = NewScope(out var db))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Svc<IAccountingPeriodService>(scope).CancelClosingAsync(Period, Plant, UserId));
                Assert.Contains(Period, ex.Message);
            }

            using (var scope = NewScope(out var db))
            {
                var period = await db.AccountingPeriods.IgnoreQueryFilters().SingleAsync(p => p.PeriodId == Period);
                period.Status   = PeriodStatus.Closed;
                period.ClosedAt = DateTime.UtcNow;
                period.ClosedBy = UserId;
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Svc<IAccountingPeriodService>(scope).CancelClosingAsync(Period, Plant, UserId));

            // 已關帳過的期間重開後再進月結，取消時要回到 Reopened 而不是 Open——
            // 「這個期間曾經關過帳」是稽核軌跡，不能被一次取消月結抹掉。
            using (var scope = NewScope(out var db))
            {
                await Svc<IAccountingPeriodService>(scope).ReopenPeriodAsync(Period, Plant, UserId);
                await Svc<IAccountingPeriodService>(scope).StartClosingAsync(Period, Plant, UserId);
                await Svc<IAccountingPeriodService>(scope).CancelClosingAsync(Period, Plant, UserId);

                var period = await db.AccountingPeriods.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.PeriodId == Period);
                Assert.Equal(PeriodStatus.Reopened, period.Status);
            }
        }

        public new void Dispose()
        {
            using (var scope = NewScope(out var db))
            {
                db.ChecklistItems.Where(i => i.ChecklistId == ChecklistId).ExecuteDelete();
                db.MonthEndChecklists.Where(c => c.ChecklistId == ChecklistId).ExecuteDelete();
                db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PeriodId == Period).ExecuteDelete();
            }
            base.Dispose();
        }

        private async Task PostAsync(IServiceScope scope, ApplicationDbContext db, string docNo)
        {
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = 1,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = Qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = InPeriod, UomCode = Uom, StockUomCode = Uom, UnitPrice = Cost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }
    }
}
