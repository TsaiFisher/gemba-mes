using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 投產＋送審要是一筆交易（2026-11-04 nightly Y5 紅燈追出來的）。
    ///
    /// <para>投產時 <c>ApprovalService.SubmitForApproval</c> 會在同一個 DbContext 上先 SaveChanges，
    /// 把已經設好的「工單＝已投產」一起寫進去，簽核狀態卻要等投產服務最後那次存檔才改成待審核。
    /// 原本沒有交易包住：</para>
    /// <list type="bullet">
    /// <item>之後任一步失敗 → 工單停在「已投產＋草稿／已退回」，外加一張孤兒簽核單；
    ///   開工閘只擋待審核／已退回，草稿那張<b>沒核准也開得了工</b>。</item>
    /// <item>就算沒失敗，兩次存檔之間也讀得到半套（Y5 就是讀到「已投產＋已退回」）。</item>
    /// </list>
    /// <para>只有真的 PostgreSQL 才看得出回滾，InMemory 會忽略交易，所以放整合測試。</para>
    /// </summary>
    [Collection("Integration")]
    public class MfgReleaseAtomicityFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGRTX";

        private const string Fg    = "FLOW-MFGRTX-FG";
        private const string Batch = "FLOW-MFGRTX-PB";
        private const string Wo    = "FLOW-MFGRTX-WO";
        private const string Wc    = "FLOW-MFGRTX-WC";
        /// <summary>同批第二張：只有整批投產的測試用到。</summary>
        private const string Wo2   = "FLOW-MFGRTX-WO2";
        private static readonly string[] AllWos = [Wo, Wo2];
        private const string DocType = "WorkOrder";

        public MfgReleaseAtomicityFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "投產交易成品"));

            using var scope = NewScope(out var db);
            await db.ApprovalLogs.IgnoreQueryFilters().Where(l => l.PlantId == Plant).ExecuteDeleteAsync();
            await db.ApprovalRequests.IgnoreQueryFilters().Where(r => r.PlantId == Plant).ExecuteDeleteAsync();
            await db.DocumentApprovalConfigs.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            var templateIds = await db.ApprovalTemplates.IgnoreQueryFilters()
                .Where(t => t.PlantId == Plant).Select(t => t.Id).ToListAsync();
            await db.ApprovalSteps.Where(s => templateIds.Contains(s.TemplateId)).ExecuteDeleteAsync();
            await db.ApprovalTemplates.IgnoreQueryFilters().Where(t => t.PlantId == Plant).ExecuteDeleteAsync();

            await db.Dispatches.IgnoreQueryFilters().Where(d => AllWos.Contains(d.WorkOrderId)).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => AllWos.Contains(o.WorkOrderId)).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => AllWos.Contains(w.WorkOrderId)).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();

            // 工作中心費率非 0：簡化模式只有標準成本超過門檻才送審，成本是 0 就測不到送審那條路
            await db.Set<WorkCenter>().IgnoreQueryFilters().Where(w => w.WorkCenterCode == Wc).ExecuteDeleteAsync();
            db.Set<WorkCenter>().Add(new WorkCenter
            {
                WorkCenterCode = Wc, WorkCenterName = "投產交易工作中心",
                StandardLaborCost = 300m, MachineHourlyRate = 600m,
                IsProductionLine = false, PlantId = Plant,
            });

            // 工單要送審：有範本、設定為必簽，且不設金額門檻（＝每張都簽）
            var template = new ApprovalTemplate { Name = "ZZMFGRTX 工單簽核", TargetDocumentType = DocType, PlantId = Plant };
            template.Steps.Add(new ApprovalStep
            {
                TemplateId = template.Id, Sequence = 1,
                ApproverType = ApproverType.User, TargetUserId = "zzmfgrtx-approver",
            });
            db.ApprovalTemplates.Add(template);
            db.DocumentApprovalConfigs.Add(new DocumentApprovalConfig
            {
                DocumentType = DocType, DocumentTypeName = "工單",
                IsApprovalRequired = true, ApprovalTemplateId = template.Id, PlantId = Plant,
            });

            // 沒有毛需求（不重算量）、沒有用料（不過料閘）：投產只剩送審這件事
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = Batch, MrpRunId = "FLOW-MFGRTX-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = 10m, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            foreach (var id in AllWos)
            {
                var wo = new WorkOrder
                {
                    WorkOrderId = id, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGRTX-RT", Quantity = 10m,
                    Status = WorkOrderStatus.Planned, PlantId = Plant, CreatedByRoleId = RoleId,
                    PlannedStartDate = new DateTime(2098, 5, 6, 0, 0, 0, DateTimeKind.Utc),
                    PlannedEndDate   = new DateTime(2098, 5, 8, 0, 0, 0, DateTimeKind.Utc),
                };
                wo.Operations.Add(new WorkOrderOperation
                {
                    WorkOrderId = id, OperationSeq = 10, OperationName = "OP10",
                    WorkCenterId = Wc, UnitTime = 1m,
                });
                db.WorkOrders.Add(wo);
            }
            await db.SaveChangesAsync();
        }

        /// <summary>對照組：正常投產要送審成功，證明下面那支的「沒投產」是回滾造成的，不是根本送不出去。</summary>
        [Fact]
        public async Task Release_WithApprovalConfig_ReleasedAndPendingWithOneRequest()
        {
            using (var scope = NewScope(out var db))
            {
                var r = await NewReleaseService(scope, db, approval: null).ApplyAsync(Wo, confirmed: true);
                Assert.Equal(WorkOrderReleaseOutcome.Submitted, r.Outcome);
            }

            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(WorkOrderStatus.Released, wo.Status);
                Assert.Equal(DocumentApprovalStatus.PendingApproval, wo.ApprovalStatus);
                var req = await db.ApprovalRequests.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(x => x.DocumentType == DocType && x.DocumentId == Wo);
                Assert.Equal(req.Id, wo.ApprovalRequestId);
            }
        }

        /// <summary>
        /// 送審已經存檔之後才失敗：工單必須整個退回已規劃／草稿，也不能留下簽核單。
        /// 修正前工單是「已投產＋草稿」——開工閘不擋草稿。
        /// </summary>
        [Fact]
        public async Task Release_FailsAfterApprovalSubmitted_RollsBackWorkOrderAndRequest()
        {
            using (var scope = NewScope(out var db))
            {
                var real = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId)
                    .GetRequiredService<IApprovalService>();
                var failing = ThrowAfterSubmitApproval.Wrap(real);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => NewReleaseService(scope, db, failing).ApplyAsync(Wo, confirmed: true));
                Assert.Equal(ThrowAfterSubmitApproval.Message, ex.Message);
            }

            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.True(wo.Status == WorkOrderStatus.Planned && wo.ApprovalStatus == DocumentApprovalStatus.Draft,
                    $"投產失敗後工單應維持已規劃／草稿，實得 {wo.Status}／{wo.ApprovalStatus}" +
                    "——已投產＋草稿＝沒核准也開得了工");
                Assert.Null(wo.ApprovalRequestId);
                Assert.False(await db.ApprovalRequests.IgnoreQueryFilters().AnyAsync(x => x.DocumentType == DocType && x.DocumentId == Wo),
                    "投產失敗不應留下孤兒簽核單（簽核人會在待我簽核看到一張不存在的投產）");
            }
        }

        /// <summary>
        /// 簡化模式自動投產（MRP 展開後逐張呼叫）是另一段提交程式，同樣要整筆回滾。
        /// 簡化模式預設直接核准，故設門檻讓它走送審（A4）。
        /// </summary>
        [Fact]
        public async Task AutoRelease_FailsAfterApprovalSubmitted_RollsBackWorkOrderAndRequest()
        {
            using (var scope = NewScope(out _))
                await Svc<IPlantParamService>(scope).SetAsync(ProjectZSystemParamKeys.MfgWoApprovalThreshold, Plant, "1");
            try
            {
                // 先證明門檻真的讓它走送審——否則下面的「沒投產」可能只是根本沒送審
                using (var scope = NewScope(out var db))
                {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    var r = await NewReleaseService(scope, db, approval: null).AutoReleaseAsync(Wo);
                    Assert.Equal(WorkOrderReleaseOutcome.Submitted, r.Outcome);
                    await tx.RollbackAsync();
                }

                using (var scope = NewScope(out var db))
                {
                    var real = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId)
                        .GetRequiredService<IApprovalService>();
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => NewReleaseService(scope, db, ThrowAfterSubmitApproval.Wrap(real)).AutoReleaseAsync(Wo));
                }

                await AssertNotReleasedAsync("自動投產失敗後");
            }
            finally
            {
                using var scope = NewScope(out _);
                await Svc<IPlantParamService>(scope).ClearAsync(ProjectZSystemParamKeys.MfgWoApprovalThreshold, Plant);
            }
        }

        /// <summary>
        /// 呼叫端已開交易（MRP 執行即如此）時，投產要併入、不可自己提交：
        /// 否則 MRP 展開失敗回滾時，已投產的工單與簽核單會漏網留下來，「失敗整批回滾」就破了。
        /// </summary>
        [Fact]
        public async Task Release_InsideCallerTransaction_DoesNotCommitOnItsOwn()
        {
            using (var scope = NewScope(out var db))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var r = await NewReleaseService(scope, db, approval: null).ApplyAsync(Wo, confirmed: true);
                Assert.Equal(WorkOrderReleaseOutcome.Submitted, r.Outcome);

                // 外層還沒提交：從另一條連線看，必須還是已規劃（自己提交了這裡就會是已投產）
                await AssertNotReleasedAsync("外層交易提交前");

                await tx.RollbackAsync();
            }

            await AssertNotReleasedAsync("外層交易回滾後");
        }

        /// <summary>
        /// 整批投產：第一張送審成功、第二張送審後才出錯——整批都不能投（與缺料的 all-or-nothing 同一規則）。
        /// 修正前第一張已投產並送審，畫面卻只顯示失敗。
        /// </summary>
        [Fact]
        public async Task ReleaseBatch_SecondWorkOrderFails_NoWorkOrderInBatchReleased()
        {
            // 對照：兩張都送得出去，整批才有「第一張成功、第二張失敗」可言
            using (var scope = NewScope(out var db))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var ok = await NewReleaseService(scope, db, approval: null).ReleaseBatchAsync(Batch, confirmed: true);
                Assert.Equal((BatchReleaseOutcome.Released, 2), (ok.Outcome, ok.Submitted));
                await tx.RollbackAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var real = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId)
                    .GetRequiredService<IApprovalService>();
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => NewReleaseService(scope, db, ThrowAfterSubmitApproval.Wrap(real, failOnCall: 2))
                        .ReleaseBatchAsync(Batch, confirmed: true));
            }

            await AssertNotReleasedAsync("整批投產第二張失敗後", AllWos);
        }

        private Task AssertNotReleasedAsync(string when) => AssertNotReleasedAsync(when, [Wo]);

        private async Task AssertNotReleasedAsync(string when, string[] woIds)
        {
            using var scope = NewScope(out var db);
            foreach (var id in woIds)
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkOrderId == id);
                Assert.True(wo.Status == WorkOrderStatus.Planned && wo.ApprovalStatus == DocumentApprovalStatus.Draft,
                    $"{when}工單 {id} 應維持已規劃／草稿，實得 {wo.Status}／{wo.ApprovalStatus}");
                Assert.False(await db.ApprovalRequests.IgnoreQueryFilters().AnyAsync(x => x.DocumentType == DocType && x.DocumentId == id),
                    $"{when}工單 {id} 不應留下簽核單");
            }
        }

        /// <summary>
        /// 申請人要是真的使用者（ApprovalRequests.RequesterId 有 FK 到 AspNetUsers），
        /// 故 IUserContext 換成帶種子 admin 的版本；其餘照 <see cref="TestControllerServices"/>。
        /// </summary>
        private WorkOrderReleaseService NewReleaseService(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db,
            IApprovalService? approval)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            var user = new RequesterUser(Plant, RoleId,
                db.Users.AsNoTracking().Where(u => u.UserName == "admin").Select(u => u.Id).Single());
            object[] args = approval == null ? [user] : [user, approval];
            return ActivatorUtilities.CreateInstance<WorkOrderReleaseService>(sp, args);
        }

        private sealed class RequesterUser(string plant, string role, string userId) : IUserContext
        {
            public string? PlantId => plant;
            public string? TimeZoneId => "Asia/Taipei";
            public string? Language => "zh-TW";
            public string? UserId => userId;
            public string? ActiveRoleId => role;
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => [plant];
        }

        /// <summary>
        /// 送審照真的做（含它自己的 SaveChanges），做完才丟例外——等同送審之後的 ⑥／⑦ 或最後存檔失敗。
        /// 用 DispatchProxy 是因為 IApprovalService 成員很多，手寫裝飾器只為改一個方法不值得。
        /// </summary>
        public class ThrowAfterSubmitApproval : DispatchProxy
        {
            public const string Message = "模擬送審之後的步驟失敗";
            private IApprovalService _inner = null!;

            private int _failOnCall, _calls;

            /// <param name="failOnCall">第幾次送審之後才丟（前面幾次照常成功）——整批投產用來讓第二張才出錯。</param>
            public static IApprovalService Wrap(IApprovalService inner, int failOnCall = 1)
            {
                var proxy = Create<IApprovalService, ThrowAfterSubmitApproval>();
                var self = (ThrowAfterSubmitApproval)(object)proxy;
                self._inner = inner;
                self._failOnCall = failOnCall;
                return proxy;
            }

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                var result = method!.Invoke(_inner, args);
                return method.Name == nameof(IApprovalService.SubmitForApproval) && ++_calls == _failOnCall
                    ? ThrowAfter((Task<ApprovalRequest>)result!)
                    : result;
            }

            private static async Task<ApprovalRequest> ThrowAfter(Task<ApprovalRequest> submit)
            {
                await submit;
                throw new InvalidOperationException(Message);
            }
        }
    }
}
