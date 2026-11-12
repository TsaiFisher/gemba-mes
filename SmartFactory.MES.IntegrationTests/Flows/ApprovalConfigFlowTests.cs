using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §8 的 5-1：簽核。
    ///
    /// 作業書的括號寫著「缺流程設定會靜默停在 PendingApproval」，那半已經有 R15 靜態守門
    /// （送簽的型別必須有已註冊的 contributor）。這裡釘的是**另一半、方向相反**的失效：
    /// **簽核設定明明存了，卻永遠找不到，於是單據完全不經簽核直接生效**。
    /// 前者是卡住（看得出來），後者是放行（看不出來），後者嚴重得多。
    ///
    /// 機制是兩層疊在一起：
    ///   ① `DocumentApprovalConfig` 實作 `IPlantEntity`，設定畫面存檔時
    ///      `ApplyGlobalValues` 會把 `PlantId ??= CurrentPlantId` 填進去——
    ///      **沒有任何入口能建出「不指定廠別」的設定**；
    ///   ② `GetApprovalConfig` 用的是 `_db.DocumentApprovalConfigs`（**沒有** IgnoreQueryFilters），
    ///      所以全域查詢過濾 `PlantId == CurrentPlantId` 也還壓在上面。
    ///
    /// 於是呼叫端傳 `null` 的那條路是**雙重不可能**：條件要 `PlantId == null`，
    /// 過濾又要 `PlantId == 目前廠別`。CLAUDE.md 的〈全域主檔規則〉寫著
    /// 「`GetApprovalConfig` 的 PlantId 參數傳 `null`（全域審核設定）」，
    /// 而 Product／Company／Tax 三個全域主檔其實都是傳廠別的——傳 null 的只有兩處，
    /// 剛好就是壞掉的那兩處。
    /// </summary>
    [Collection("Integration")]
    public class ApprovalConfigFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW5G";

        private const string DocType = "VendorEvaluation";

        public ApprovalConfigFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync();
            using var scope = NewScope(out var db);
            // 由子往父刪：設定的 FK 指向模板、步驟的 FK 指向模板，
            // 直接刪模板會撞 FK_ApprovalSteps_ApprovalTemplates_TemplateId。
            await db.DocumentApprovalConfigs.IgnoreQueryFilters()
                .Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            var templateIds = await db.ApprovalTemplates.IgnoreQueryFilters()
                .Where(t => t.PlantId == Plant).Select(t => t.Id).ToListAsync();
            if (templateIds.Count > 0)
            {
                await db.ApprovalSteps
                    .Where(s => s.TemplateId != null && templateIds.Contains(s.TemplateId))
                    .ExecuteDeleteAsync();
                await db.ApprovalTemplates.IgnoreQueryFilters()
                    .Where(t => templateIds.Contains(t.Id)).ExecuteDeleteAsync();
            }
        }

        /// <summary>
        /// 設定存檔時廠別是被自動填上的——這不是選項，是 `ApplyGlobalValues` 的行為。
        /// 所以「不指定廠別的全域簽核設定」在這個系統裡根本不存在，
        /// 任何以 `null` 去查設定的呼叫端都是必然落空。
        /// </summary>
        [Fact]
        public async Task ApprovalConfig_AlwaysCarriesTheCreatorsPlant_SoNullLookupCanNeverMatch()
        {
            var templateId = await SeedTemplateAndConfigAsync();

            using var scope = NewScope(out var db);

            // ① 存進去的那一列確實帶著建立者的廠別（呼叫端沒指定，是 DbContext 填的）
            var saved = await db.DocumentApprovalConfigs.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(c => c.DocumentType == DocType && c.PlantId == Plant);
            Assert.Equal(Plant, saved.PlantId);
            Assert.Equal(templateId, saved.ApprovalTemplateId);

            var approval = Svc<IApprovalService>(scope);

            // ② 傳廠別 → 找得到
            Assert.NotNull(await approval.GetApprovalConfig(DocType, Plant));

            // ③ 傳 null → 找不到。這正是 CLAUDE.md〈全域主檔規則〉教的寫法，
            //    而它在這個資料模型下**永遠**查不到東西：
            //    條件要 PlantId == null，全域查詢過濾又要 PlantId == 目前廠別。
            Assert.Null(await approval.GetApprovalConfig(DocType, null));
        }

        /// <summary>
        /// 供應商評鑑：設了簽核流程就必須真的走簽核。
        ///
        /// 這支釘的是 `VendorEvaluationController.SubmitApproval` 拿得到設定。
        /// 拿不到的話它會走「無簽核設定」那條分支——直接把單標成 Approved、
        /// 連動回寫供應商資格狀態，**完全不經任何人核准，而且沒有任何訊號**。
        /// </summary>
        [Fact]
        public async Task VendorEvaluation_FindsItsApprovalConfig_SoSubmissionCannotSilentlySelfApprove()
        {
            await SeedTemplateAndConfigAsync();

            using var scope = NewScope(out var db);
            var approval = Svc<IApprovalService>(scope);

            // 控制器實際使用的參數；查不到就等於「沒設定 → 直接核准」
            var config = await approval.GetApprovalConfig(DocType, Plant);
            Assert.NotNull(config);
            Assert.True(config!.IsApprovalRequired);
            Assert.False(string.IsNullOrEmpty(config.ApprovalTemplateId),
                "設定查到了卻沒有模板，送審一樣會走「無簽核設定」那條分支");
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 建立兩步驟模板與設定。刻意不經 `DocumentApprovalConfigController`：
        /// 受測的機制是 `ApplyGlobalValues` 與全域查詢過濾，兩者都在 `SaveChanges` 這一層，
        /// 走 DbContext 與走控制器完全等價（控制器也只是 `Add` 之後 `SaveChanges`）。
        /// </summary>
        private async Task<string> SeedTemplateAndConfigAsync()
        {
            using var scope = NewScope(out var db);

            var template = new ApprovalTemplate
            {
                Name = "ZZW5G 兩層簽核", TargetDocumentType = DocType,
            };
            template.Steps.Add(new ApprovalStep
            {
                TemplateId = template.Id, Sequence = 1,
                ApproverType = ApproverType.User, TargetUserId = "zzw5g-approver-1",
            });
            template.Steps.Add(new ApprovalStep
            {
                TemplateId = template.Id, Sequence = 2,
                ApproverType = ApproverType.User, TargetUserId = "zzw5g-approver-2",
            });
            db.ApprovalTemplates.Add(template);

            // PlantId 刻意不指定——與設定畫面存檔時一模一樣
            db.DocumentApprovalConfigs.Add(new DocumentApprovalConfig
            {
                DocumentType = DocType, DocumentTypeName = "供應商評鑑",
                IsApprovalRequired = true, ApprovalTemplateId = template.Id,
            });

            await db.SaveChangesAsync();
            return template.Id;
        }
    }
}
