using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 手冊索引與詞彙檢索的真 PostgreSQL 守門（規格 §10.3、§10.5、§10.10）。
    ///
    /// <para><b>為什麼非整合測試不可：</b>本功能的核心決策——「CJK 要先切成重疊雙字，
    /// 否則 <c>to_tsvector</c> 把整句話變成一個 token」——是 PostgreSQL 的行為，
    /// 單元測試怎麼寫都驗證不到。而它壞掉的樣子是**中文提問恆回 0 筆、不拋任何例外**。</para>
    ///
    /// <para>沿用既有的 "ScheduleIntegration" collection，**不另開 collection**
    /// （見 <c>CollectionLayoutGuardTests</c>：本專案所有整合測試共用同一個 PostgreSQL 測試庫）。</para>
    ///
    /// <para>本檔需要 <c>Features:QaAssistant:Enabled=true</c>，該開關由
    /// <c>TestWebApplicationFactory</c> 以**環境變數**設定。不能在這裡用
    /// <c>WithWebHostBuilder</c> ＋ <c>ConfigureAppConfiguration</c> 追加：
    /// <c>QAModule.RegisterServices</c> 讀的是 <c>builder.Configuration</c>，
    /// 而該回呼發生得更早，於是開關只在 <c>Configure(app)</c> 階段可見——
    /// 結果是服務沒註冊、應用程式啟動即崩潰（實際踩過）。</para>
    ///
    /// <para><b>〈通道要釘死〉本檔的檢索一律走純詞彙通道</b>（<see cref="SearchAsync"/>
    /// 預設注入 <see cref="NoEmbeddingClient"/>）。2026-08-21 實測確認：不釘死的話，
    /// 同一支測試在同一台機器上會**隨當下的向量補完進度**紅綠不定——
    /// fixture 會啟動 <c>DocIndexBackgroundService</c>，它以約 8 塊／分在背景補嵌入，
    /// 而測試用的是同一套真實 <c>IEmbeddingClient</c>。三輪實測的失敗集合各不相同
    /// （8 紅／6 紅／4 紅，且集合不重疊），那是競態不是回歸。</para>
    ///
    /// <para><b>⚠️ CI 全綠不等於它是對的。</b>CI 沒有 Ollama，語意通道恆不可用，
    /// 所以這些測試在 CI 上恆綠——開發機的 SSH 通道一通就開始紅。釘死之後兩邊走同一條路。</para>
    ///
    /// <para>更要緊的是：本檔有幾條斷言**只在詞彙通道上成立，而且是刻意的**——
    /// 「完全沒有共同詞的提問回空集合」「純標點的提問不回結果」都是 S1 的行為，
    /// S2 的 <c>ManualSearchService</c> 已明確改成「詞彙通道無事可做時，語意通道還可以」。
    /// 它們不是壞掉的測試，是**詞彙通道的規格**；不要為了讓它們通過而去改產品程式，
    /// 也不要把語意通道接回來。語意通道的行為請寫在專屬測試裡
    /// （<see cref="擷取物閘門在語意通道上同樣成立"/>）。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualIndexIntegrationTests : IAsyncLifetime
    {
        /// <summary>
        /// **本檔專用的角色，刻意不是 Admin（"1"）。**
        ///
        /// <para>本檔的權限測試會把角色的可讀鍵收窄到只剩一兩把（見
        /// <see cref="SetPermissionsAsync"/>），而且跑完不還原——那正是它要測的東西。
        /// 問題在 <c>MenuPermissions</c> 是**整個測試庫共用的一份資料**：
        /// <c>TestWebApplicationFactory</c> 把庫名寫死成 <c>SmartFactoryMES_Test</c>，
        /// 所以連「不同 worktree、不同 dotnet test 進程」也還是同一份。</para>
        ///
        /// <para><b>2026-08-10 因此毀掉兩輪各約兩小時的生成評測。</b>另一個工作區在
        /// 11:19 與 13:55 跑本檔，稽核日誌各留下 148 筆
        /// <c>{"Before":{"CanRead":true},"After":{"CanRead":false}}</c>；
        /// 同時間 <see cref="ManualGenerationBenchmarkTests"/> 正跑到一半，
        /// 於是它從第 11 題起只檢索得到沒有 MenuKey 的目錄頁，
        /// 而報表照樣印出一整組看起來很合理的數字。</para>
        ///
        /// <para>改用專屬角色之後，本檔再怎麼收窄都碰不到別人依賴的 Admin 權限。
        /// 這比「跑完還原」可靠：還原擋不住「跑到一半被另一個進程讀到」。</para>
        /// </summary>
        private const string RoleId = "QA-INDEX-TEST";

        /// <summary>
        /// 語料規模。**新增或刪除手冊頁時必須更新這兩個數字**，否則本檔多條測試會紅。
        ///
        /// <para>刻意寫死而不從檔案系統推算：這兩個值是「語料有沒有在我不知情的狀況下變動」
        /// 的絆索。改成動態計算就等於把絆索拆掉——手冊少了一整頁也照樣綠燈。</para>
        ///
        /// <para>取值方式：<c>ManualCorpusFacts.量測_切塊粒度與字數分佈</c> 會印出
        /// 「頁數＝N chunk 總數＝M（中英合計）」，直接抄過來即可。</para>
        /// </summary>
        private const int ManualPages = 183;   // +1：2026-11-12 sys/license（授權狀態，進管理系統選單後補手冊）；+1：2026-11-03 bas/container-unit（箱子／容器個體）；+1：2026-10-21 rpt/field-telemetry（現場遙測）；+4：2026-10-05 手機料件作業（mobile/issue、staging、outsource-receive、requisition）；134 +15：手機手冊 13 頁、tutor 2 頁；+3：報表工具 S-1／S-3／S-4；+1：S-7 手冊問答；+1：各廠參數覆寫一覽；+2：銷貨／進貨折讓單（W3-F3 Phase 2）；+15：2026-09-11 手冊補頁（sys 9＋apr 3＋apr 目錄頁＋bas/invoice-account＋mobile/approval）；+1：2026-09-18 sys/user-settings（個人設定，選單上有畫面卻從未登錄手冊）；+1：2026-09-22 rpt/consolidated-statement（合併財報）；+2：2026-09-23 osp/outsource-price（委外價格分析，新報表）與 rpt/saved-queries（常用查詢，跨報表功能頁——它沒有對應的 Controller，是 21 張報表共用的說明）；+1：2026-09-23 act/trial-balance（試算表，關帳前的科目層級對帳）
        // ⚠️ 686 這一跳（+7）**不是加了七段內容**，是 sys/qa-assistant.html 的**中文側**
        // 越過了 ManualDocParser.SinglePageMaxChars（2000 字）那條線：
        // 未達門檻的頁面刻意整頁併成一塊，所以該頁中文原本是 1 塊、英文 8 塊；
        // §11.3 的回顯確認補了約 330 字之後中文總長超過 2000，於是也拆成 8 塊。
        // 這條註記留著是因為「小改一段字卻多了七塊」看起來像 bug，其實是設計。
        private const int ManualChunks = 999;  // 994 → 999（2026-11-12：新增 sys/license 一頁（授權狀態，進管理系統選單後補手冊），另在 sys/index 補卡片與 Admin 專屬清單、sys/permission 補一句、各 sys 頁側欄加連結。實測值）。983 → 994（2026-11-04 第 4 期：bas/container-unit 補「進貨上架裝箱、既有庫存裝箱與換箱」並把三個情境與「查詢與維護」改為各自的 h2 段（原本全擠在「作業流程」一塊 5 千字，泛用詞把「倉庫間移轉庫存」題擠出前 5），另 inv/stock-transfer 撥入批號、inv/warehouse-ops 與手機 putaway 上架裝箱，頁數不變。實測值）。979 → 983（2026-11-04：容器第 2、3 期——bas/container-unit 補「生產中使用箱子」「出廠與回收」兩節與狀態列、mfg/dispatch 箱號欄、inv/cross-plant-transfer 批號欄、inv/warehouse-ops 與手機 production-report／pick 補掃箱號一句，頁數不變。實測值）。973 → 979（2026-11-03：新增 bas/container-unit 一頁、mfg/production-completion 補箱號步驟與欄位、bas/container 補一句與 BAS 各頁側欄連結。實測值）。968 → 973（2026-11-02：容器 L1／L1+——mfg/production-completion 補「分箱入庫與箱標籤」一節與三列欄位、bas/container 改寫用途與補兩列、mfg/dispatch 改容器欄說明，頁數不變。實測值）。974 → 968（2026-10-29：整合各分支——簽核單別名稱分支讓 mobile/approval 中文側跌破整頁併塊門檻（-6，減少是設計），疊上列印＋選單搬移＋稽核日誌、驗收後續等分支後實測值）。961 → 974（2026-10-28：各廠參數覆寫一覽搬到管理系統——sys/index 補卡片、bas/index 與該頁改選單位置。實測值）。946 → 961（2026-10-28：18 張單據頁各補「列印」一節、首頁補「列印單代碼一律配名稱」一句，頁數不變，補字後跨過門檻多切。實測值）。940 → 946（2026-10-27：inv/inventory-balance 加「品號彙總（全廠）」一節、inv/warehouse-ops 與手機 issue／pick／stock 改在手量說明，頁數不變，補字跨過門檻多切。實測值）。937 → 940（2026-10-27：inv/stock-shortage 改寫為目前／預計兩段並補四列欄位，頁數不變，補字後跨過門檻多切。實測值）。934 → 937（2026-10-24：act/nrv-assessment 改寫期末口徑、act/month-end 補外幣重評／ECL 期末口徑、ord/internal-order 補科目，頁數不變，實測值；稽核手冊 sys/audit-log 的改動另計）。930 → 934（2026-10-23：inv/landed-cost-allocation、inv/cross-plant-transfer、ord/sales-return、pur/goods-return 各補一段成本口徑說明，頁數不變，實測值；稽核手冊 sys/audit-log 的改動另計）。929 → 930（2026-10-22：rpt/data-audit 補「總帳存貨對明細帳」一節、fin/ar-receipt 與 fin/ap-payment 各補一句，實測值；稽核手冊 sys/audit-log 的改動另計）。928 → 929（2026-10-21：rpt/data-audit 補「可自動修正的累計欄位」一節，實測值）。922 → 928（2026-10-21：新增 rpt/field-telemetry 一頁，實測值；稽核手冊 sys/audit-log 的改動另計）。更早：917 → 922（2026-10-20：工單作廢與投產前審核——mfg/work-order、mfg/dispatch、mobile/work-session 三頁補字，頁數不變，補字後跨過門檻多切。實測值）。更早：915 → 917（2026-10-16：併入自動重排規則／凍結時界／排程紀錄分支——該分支原從 901 長到 903，疊在 915 之上實測 917。頁數不變）。更早：913 → 915（2026-10-16：重工成本分開統計——inv/rework-material 補分工提示與帳務表、mfg/work-order-variance 補「重工成本」一節與兩列欄位，另在 mfg/material-slip、mfg/work-order、mfg/production-completion 補一句。頁數不變。實測值）。更早：910 → 913（2026-10-13：已備料的料先從備料區扣，補 mfg/material-slip、mobile/issue、inv/material-staging 三頁說明，頁數不變。實測值）。更早：905 → 910（2026-10-12 晚：委外簡化 B1／B2 補 sys/plant、osp/outsource-receipt、mobile/outsource-receive 三頁說明，頁數不變。實測值）。更早：904 → 905（2026-10-12 晚：委外品檢／完工量回寫補手冊六頁，頁數不變，補字跨過門檻多切一塊。實測值）。更早：901 → 904（2026-10-12：「離線作業」改「斷線保護」改寫手機手冊 11 頁，另在 16 張單據頁補單位／來源單號說明。頁數不變，+3 全是既有頁補字後跨過門檻多切。實測值）。更早：882 → 901（2026-10-08：併入派工暫停／自動重排分支——該分支自身從 878 長到 897（+19，明細見下列「派工暫停分支」），疊在 882 之上即 901。實測值）。派工暫停分支（原基準 878）：886 → 897（2026-10-06 自動重排排程第 1 期：mfg/schedule 新增「自動重排排程」一節、sys/system-param 補三列參數、sys/plant 與 dispatch／準備看板／手機派工清單補提示。頁數不變。實測值）。更早：878 → 886（2026-10-06：派工暫停生命週期——mfg/dispatch 新增「暫停處理」一節，另補 mfg/schedule、mfg/machine-status-order、mfg/work-order、bas/work-center、qcr/inspection-order 與手機三頁。頁數不變，+8 全是既有頁補字後跨過門檻多切。實測值）。更早：878 → 882（2026-10-08：併入開工實際機台分支——bas/company 補結帳日／收付款天數兩列與截圖、fin/ar-invoice 與 fin/ap-invoice 的到期日說明改寫。**頁數沒變**，+4 全是既有頁補字後跨過門檻多切出來的。實測值）。更早：875 → 878（2026-10-05：inv/inventory-value 補「顯示品號明細」兩種模式與分類欄位表，頁數不變，+3 全是同頁補字後跨過門檻多切。實測值）。更早：863 → 872（2026-10-05：新增手機料件作業四頁，另改寫 mfg/material-slip 的狀態、流程與「五種作業的關係」一節。實測值）→ 875（同日：生產準備看板兩頁補「延續」、工單進度補完工量定義，補字後跨過門檻多切。實測值）。更早：563 → 652 → 662（報表工具三頁）→ 663（生產排程頁補甘特圖顏色一節）→ 670（S-7 sys/qa-assistant）→ 677（bas/plant-param-overview）→ 679（sys/qa-assistant 補資料查詢模式與並發說明）→ 686（同頁補 §11.3 回顯確認，中文側越過整頁併塊門檻） → 692（併入 main：aa10b87 那批四頁內容補完，同樣是跨過 2000 字門檻多切幾塊） → 706（新增銷貨／進貨折讓單兩頁，中英各 7 塊） → 813（2026-09-11 補 15 頁：sys 9＋apr 3＋apr 目錄頁＋bas/invoice-account＋mobile/approval。⚠️ 這個數字是**實測**不是算出來的——短頁整頁併一塊、長頁才分節，改一小段字塊數就可能跳好幾塊） → 819（2026-09-18：新增 sys/user-settings 一頁，另在 sys/user、sys/plant、rpt/report-schedule、act/accounting-period 四頁補欄位說明。**+6 不等於新頁的塊數**——新頁中英各一塊，其餘四塊是那四頁補字後跨過 2000 字門檻才多切出來的，所以「只補一列表格」也可能動到這個數字） → 823（2026-09-21：mfg/work-order-variance 補「實際成本從哪裡來」一整段——時點表＋提醒框＋欄位說明。**頁數沒變**，+4 全是同一頁補字後多切出來的塊。實測值，取自 ManualCorpusFacts.量測_切塊粒度與字數分佈） → 829（2026-09-22：新增 rpt/consolidated-statement 一頁。**+6 不是新頁的全部**——這頁中英各 3 塊，跨過 2000 字門檻才分節） → 837（2026-09-22：手冊對帳「後來改過但手冊沒跟上」的那批——會計科目頁由 bas 搬到 fin、倉庫頁補客供倉、兩支交期達成率改口徑、採購金額分析納入委外、供應商評分卡「無資料≠滿分」、12 支報表補期間必填／預設值。**頁數仍是 173**，+8 全是既有頁補字後跨過 2000 字門檻多切出來的。實測值，取自 ManualCorpusFacts.量測_切塊粒度與字數分佈） → 856（2026-09-23：新增 osp/outsource-price 與 rpt/saved-queries 兩頁，另在三支既有 PUR 報表頁補「存成常用查詢」一節。**+19 裡新頁只占一部分**——兩頁都夠長而分了節，加上既有頁補字後跨過 2000 字門檻多切出來的。實測值） → 863（2026-09-23：新增 act/trial-balance 一頁，另在 rpt/saved-queries 把報表張數 21 改 22。實測值）

        private readonly WebApplicationFactory<Program> _app;

        public ManualIndexIntegrationTests(AuthedScheduleFactory factory) => _app = factory;

        public async Task InitializeAsync()
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();
            await EnsureTestRoleAsync(scope.ServiceProvider);
        }

        /// <summary>
        /// <c>MenuPermissions.RoleId</c> 有 FK 指向 <c>AspNetRoles</c>，
        /// 所以專屬角色必須真的是一列角色，不能只是一個字串。
        /// </summary>
        private static async Task EnsureTestRoleAsync(IServiceProvider sp)
        {
            var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roleManager.FindByIdAsync(RoleId) != null) return;

            var result = await roleManager.CreateAsync(new ApplicationRole
            {
                Id = RoleId,
                Name = RoleId,
                NameZh = "手冊索引測試專用",
                NameEn = "Manual index test only",
            });
            Assert.True(result.Succeeded,
                $"建不出測試專用角色 {RoleId}：{string.Join("；", result.Errors.Select(e => e.Description))}");
        }

        public Task DisposeAsync() => Task.CompletedTask;

        // ── 索引建置（§10.3） ────────────────────────────────────────────

        [Fact]
        public async Task 索引涵蓋全部手冊頁且中英各一份()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();

            // 一律先限縮到手冊語料：同一張表還住著擷取物（§10.11.7 定案 #42），
            // 而這裡數的是「手冊頁有沒有全部進索引」。不限縮的話，這幾個寫死的數字
            // 會隨另一種語料的多寡而紅，紅的原因卻與手冊無關。
            var files = await ManualChunkQuery(qa).Select(c => c.FilePath).Distinct().CountAsync();
            var langs = await ManualChunkQuery(qa).Select(c => c.Lang).Distinct().OrderBy(l => l).ToListAsync();

            Assert.Equal(ManualPages, files);
            Assert.Equal(new[] { "en", "zh" }, langs);

            // 每一頁每一語系都至少要有一塊。少了就是雙語拆分在某頁壞掉——
            // 而那一頁的英文（或中文）使用者從此問不到任何東西。
            var missing = await qa.DocChunks
                .GroupBy(c => c.FilePath)
                .Where(g => g.Select(x => x.Lang).Distinct().Count() < 2)
                .Select(g => g.Key)
                .ToListAsync();

            Assert.True(missing.Count == 0, "以下頁面缺少某個語系：" + string.Join(", ", missing));
        }

        [Fact]
        public async Task 每一塊都有切詞結果()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();

            // Lexemes 為空 = 該塊永遠檢索不到，而且沒有任何錯誤。
            var empty = await ManualChunkQuery(qa)
                .Where(c => c.Lexemes == "")
                .Select(c => c.FilePath + " #" + c.ChunkIndex)
                .Take(10)
                .ToListAsync();

            Assert.True(empty.Count == 0, "以下塊沒有切詞結果：" + string.Join(", ", empty));
        }

        [Fact]
        public async Task 有標題的塊必須有標題切詞()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();

            // 這一支守的是**忘記 bump `DocIndexBuilder.IndexFormatVersion`**（待決 #48 的兩欄化）。
            // 舊格式的列長這樣：`Lexemes` 是「標題＋內文」平鋪、`HeadingLexemes` 空字串。
            // 那時加權查詢照樣回得出結果、名次也不會離譜——**標題權重只是安靜地沒有生效**，
            // 沒有任何錯誤訊息，而增量重建看 hash 沒變就一列都不會重做。
            //
            // 條件挑 `HeadingPath <> ''`：沒有麵包屑的塊（首頁前言）本來就切不出標題詞，
            // 那不是缺陷。切詞把標點與空白全當分隔符，所以有麵包屑就必然切得出東西。
            var missing = await ManualChunkQuery(qa)
                .Where(c => c.HeadingPath != "" && c.HeadingLexemes == "")
                .Select(c => c.FilePath + " #" + c.ChunkIndex + " 〈" + c.HeadingPath + "〉")
                .Take(10)
                .ToListAsync();

            Assert.True(missing.Count == 0,
                "以下塊有標題卻沒有標題切詞（多半是索引還停在兩欄化之前的格式，" +
                "確認 DocIndexBuilder.IndexFormatVersion 有跟著建列邏輯一起改）：" +
                string.Join(", ", missing));
        }

        [Fact]
        public async Task 內容沒變時不重建()
        {
            using var scope = _app.Services.CreateScope();
            var builder = scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>();

            // InitializeAsync 已經建過一次，這一次應該一個檔都不動。
            var result = await builder.RebuildAsync();

            Assert.Equal(ManualPages, result.FilesScanned);
            Assert.Equal(0, result.FilesReindexed);
            Assert.Equal(0, result.ChunksWritten);
        }

        [Fact]
        public async Task 內容hash變了才重建該檔()
        {
            const string target = "ord/delivery-rate.html";

            using (var scope = _app.Services.CreateScope())
            {
                var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                await qa.DocChunks
                    .Where(c => c.FilePath == target)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContentHash, "stale"));
            }

            using (var scope = _app.Services.CreateScope())
            {
                var result = await scope.ServiceProvider
                    .GetRequiredService<IDocIndexBuilder>().RebuildAsync();

                // 只有被動過手腳的那一檔重建，其餘各檔靠 hash 略過。
                Assert.Equal(1, result.FilesReindexed);
            }

            using (var scope = _app.Services.CreateScope())
            {
                var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                Assert.False(await qa.DocChunks.AnyAsync(c => c.ContentHash == "stale"));
            }
        }

        [Fact]
        public async Task 併行重建不會互相踩到()
        {
            // 啟動時的背景服務與手動觸發同時進行時，兩邊各自「先刪該檔的列、再插新列」會交錯，
            // 撞 (FilePath, Lang, ChunkIndex) 唯一鍵。這裡刻意把全部索引作廢，
            // 讓兩邊都必須真的重建全部手冊檔——沒有 semaphore 的話這是必然衝突而不是機率問題。
            using (var scope = _app.Services.CreateScope())
            {
                await ManualChunkQuery(scope.ServiceProvider.GetRequiredService<QaDbContext>())
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContentHash, "force-rebuild"));
            }

            async Task<int> RebuildAsync()
            {
                using var scope = _app.Services.CreateScope();
                var r = await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();
                return r.FilesReindexed;
            }

            var results = await Task.WhenAll(RebuildAsync(), RebuildAsync());

            // 兩邊都成功（沒有例外逸出），且一先一後：先跑完的重建全部檔案，
            // 後進場的看到 hash 已是最新值，一檔都不用動。
            Assert.Equal(new[] { 0, ManualPages }, results.OrderBy(x => x));

            using (var scope = _app.Services.CreateScope())
            {
                var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                Assert.Equal(ManualChunks, await ManualChunkQuery(qa).CountAsync());
                Assert.False(await ManualChunkQuery(qa).AnyAsync(c => c.ContentHash == "force-rebuild"));
            }
        }

        [Fact]
        public async Task 有畫面的頁存MenuKey_目錄頁不存()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();

            var ranking = await qa.DocChunks.FirstAsync(c => c.FilePath == "ord/sales-ranking.html");
            Assert.Equal("SalesRanking", ranking.MenuKey);
            Assert.Equal("ord", ranking.Module);
            Assert.Equal("/docs/user/ord/sales-ranking.html", ranking.DocUrl);

            var index = await qa.DocChunks.FirstAsync(c => c.FilePath == "ord/index.html");
            Assert.Equal("", index.MenuKey);
            Assert.Equal("ord", index.Module);
        }

        // ── §10.5 的核心：PostgreSQL 對 CJK 的實測行為 ───────────────────

        [Fact]
        public async Task PostgreSQL對未切詞的中文整句無法比對_這就是雙字切詞存在的理由()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var conn = qa.Database.GetDbConnection();
            await qa.Database.OpenConnectionAsync();

            const string sentence = "銷售排名報表可依客戶或品號彙總";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT to_tsvector('simple', $1) @@ plainto_tsquery('simple', $2) AS raw_match,
                       to_tsvector('simple', $3) @@ plainto_tsquery('simple', $4) AS tokenized_match
                """;
            AddParam(cmd, sentence);
            AddParam(cmd, "銷售");
            AddParam(cmd, CjkTokenizer.Tokenize(sentence));
            AddParam(cmd, CjkTokenizer.Tokenize("銷售"));

            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            // 這兩行是整個 §10.5 的依據：內建分詞器把整句話變成一個 lexeme，
            // 只有一字不差輸入整句才命中；切成重疊雙字之後才查得到。
            Assert.False(reader.GetBoolean(0), "未切詞竟然命中了——若 PostgreSQL 版本改變了 CJK 分詞行為，§10.5 的設計需要重新評估");
            Assert.True(reader.GetBoolean(1), "切詞後仍無法命中——雙字切詞與檢索組態不一致");

            static void AddParam(System.Data.Common.DbCommand cmd, string value)
            {
                var p = cmd.CreateParameter();
                p.Value = value;
                cmd.Parameters.Add(p);
            }
        }

        // ── 檢索（§10.4 語系硬過濾、§10.10 權限） ────────────────────────

        [Theory]
        [InlineData("銷售排名怎麼看", "ord/sales-ranking.html")]
        [InlineData("安全存量不足要去哪裡查", "inv/stock-shortage.html")]
        [InlineData("交期達成率", "ord/delivery-rate.html")]
        public async Task 中文提問命中對應手冊頁(string question, string expectedFile)
        {
            var hits = await SearchAsync(question, allowAll: true);

            Assert.NotEmpty(hits);
            var urls = hits.Select(h => h.DocUrl).ToList();
            Assert.Contains("/docs/user/" + expectedFile, urls);
        }

        [Fact]
        public async Task 英文提問只回英文塊_中文提問只回中文塊()
        {
            var en = await SearchAsync("How do I view the sales ranking report?", allowAll: true);
            var zh = await SearchAsync("銷售排名怎麼看", allowAll: true);

            Assert.NotEmpty(en);
            Assert.NotEmpty(zh);

            // §10.4：語系是硬過濾條件而不是排序參考。放寬成排序的話，
            // multilingual embedding 會讓英文使用者拿到中文引文。
            Assert.All(en, h => Assert.Equal("en", h.Lang));
            Assert.All(zh, h => Assert.Equal("zh", h.Lang));
        }

        [Theory]
        [InlineData("SalesRanking", "en")]
        [InlineData("SalesRanking 這個畫面怎麼用", "zh")]
        public async Task 語系過濾對含拉丁代號的提問也必須生效(string question, string expectedLang)
        {
            // 路由與代號（<c>路由：/SalesRanking</c>、<c>IQC</c>）在中英兩版 chunk 裡是**同一個詞**，
            // 這正是「語系只當排序參考」會滲漏的地方：英文查詢用 english 組態去掃中文塊的
            // Lexemes，salesranking 一樣 stem 成 salesrank，照樣命中。
            //
            // 純中文／純英文的提問測不出這一點（詞彙本來就不重疊），
            // 拿掉語系過濾也照樣全綠——本測試就是為了補上那個缺口而存在。
            var hits = await SearchAsync(question, allowAll: true);

            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.Equal(expectedLang, h.Lang));
        }

        [Fact]
        public async Task 沒有該畫面權限就檢索不到該畫面的手冊()
        {
            var withPermission = await SearchAsync("銷售排名怎麼看", allowAll: true);
            Assert.Contains(withPermission, h => h.MenuKey == "SalesRanking");

            // 只給一個不相干的鍵：SalesRanking 的內容必須整個消失。
            var without = await SearchAsync("銷售排名怎麼看", allowKeys: new[] { "StockShortage" });
            Assert.DoesNotContain(without, h => h.MenuKey == "SalesRanking");
        }

        [Fact]
        public async Task 沒有任何權限時檢索不到任何有對應畫面的內容()
        {
            var hits = await SearchAsync("銷售排名怎麼看", allowKeys: Array.Empty<string>());

            // 手冊首頁（無模組、無 MenuKey）與白名單目錄（手機說明）維持公開，其餘一律擋下。
            Assert.All(hits, h => Assert.Equal("", h.MenuKey));
            Assert.All(hits, h => Assert.True(
                ModuleOf(h.DocUrl) is "" || ManualCorpusPolicy.PublicModules.Contains(ModuleOf(h.DocUrl)),
                $"零權限角色檢索到了不該公開的模組內容：{h.DocUrl}"));
        }

        [Fact]
        public async Task 模組目錄頁需要該模組至少一項權限()
        {
            // ORD 目錄頁列出整個模組有哪些畫面，對 ORD 毫無權限的人不該檢索得到——
            // 那是系統功能結構的洩漏（§10.4）。
            var withOrd = await SearchAsync("訂單模組有哪些畫面", allowKeys: new[] { "SalesRanking" });
            var withInv = await SearchAsync("訂單模組有哪些畫面", allowKeys: new[] { "StockShortage" });

            Assert.Contains(withOrd, h => h.DocUrl == "/docs/user/ord/index.html");
            Assert.DoesNotContain(withInv, h => h.DocUrl == "/docs/user/ord/index.html");
        }

        [Fact]
        public async Task 未登入時檢索不到任何受權限保護的內容()
        {
            // 背景服務／未登入情境下 IUserContext 全為 null。這裡要的是「什麼都拿不到」，
            // 而不是「因為過濾器沒作用所以全都拿得到」。
            var hits = await SearchAsync("銷售排名怎麼看", roleId: null);

            Assert.All(hits, h => Assert.Equal("", h.MenuKey));
        }

        [Fact]
        public async Task 手機說明沒有對應畫面_但仍檢索得到()
        {
            // 手機 PWA 的畫面是靜態 HTML、沒有 controller，於是 mobile/ 底下每一頁都是
            // 「MenuKey 空、Module 非空」，落在權限過濾的「模組目錄頁」分支——
            // 而該分支比對的可讀模組集合是由使用者的 MenuKey 推得的，永遠不會含 mobile。
            //
            // 少了 DocChunkFilter 的白名單分支，這頁就是**索引得進去、任何人都檢索不到**，
            // 而且沒有任何錯誤訊號（2026-07 建立至今皆如此）。逐頁掃的版本見
            // SmartFactory.MES.Tests 的 ManualReachabilityTests，這裡驗的是真的跑一次 SQL。
            var hits = await SearchAsync("手機離線佇列怎麼同步", allowKeys: new[] { "StockShortage" });

            Assert.Contains(hits, h => h.DocUrl.StartsWith("/docs/user/mobile/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 手機說明對零權限角色也開放()
        {
            // 公開層級與手冊首頁相同：手機的功能可見性走的是另一套權限
            // （MobileModule／ModuleAction），無法用 MenuPermissions 的鍵表達，
            // 而手機說明的內容只有操作步驟、不含任何資料。理由見 ManualCorpusPolicy。
            var hits = await SearchAsync("手機離線佇列怎麼同步", allowKeys: Array.Empty<string>());

            Assert.Contains(hits, h => h.DocUrl.StartsWith("/docs/user/mobile/", StringComparison.Ordinal));
        }

        [Theory]
        // 每個問題只鎖定一頁：問題挑的是**只有那一頁講得清楚**的事，
        // 用整個手冊都找得到的字（例如「報工」）當問題就驗不出東西。
        [InlineData("備料完成後要按哪一顆才能開工", "mobile/work-session.html")]
        [InlineData("上架要掃目的儲位嗎", "mobile/putaway.html")]
        [InlineData("盤點的複盤可以自己盤嗎", "mobile/stockcount.html")]
        [InlineData("機台廢棄之後還能做什麼事件", "mobile/machine-status.html")]
        [InlineData("進料檢驗的驗證等級要選哪一級", "mobile/iqc.html")]
        public async Task 手機畫面層說明檢索得到對應那一頁(string question, string expected)
        {
            // 這五題就是「手冊增修後 RAG 會不會跟上」的驗收：頁面是 2026-08 新增的，
            // 索引由啟動時的 DocIndexBuilder 依 SHA256 自動補上，不需要任何手動步驟。
            // 若哪天有人把畫面層說明併回單頁，這裡會紅——那不是壞事，是提醒重跑一次驗收。
            var hits = await SearchAsync(question, allowKeys: Array.Empty<string>());

            Assert.Contains(hits, h => h.DocUrl == "/docs/user/" + expected);
        }

        [Fact]
        public async Task 完全沒有共同詞的提問回空集合()
        {
            // 「鸚鵡螺」的兩個雙字（鸚鵡／鵡螺）在手冊中都不存在。
            var hits = await SearchAsync("鸚鵡螺", allowAll: true);
            Assert.Empty(hits);
        }

        [Fact]
        public async Task 只要有一個雙字重疊就會回結果_這是OR檢索的必然特徵()
        {
            // 「鸚鵡螺保養週期」裡的「保養」「週期」確實出現在模具保養那幾頁，於是有命中。
            // 這**不是缺陷**，是 OR 召回的定義；精確度由 ts_rank 的排序承擔。
            //
            // 但它直接推導出 §10.8 的必要性：不能把「有結果」當成「有答案」，
            // 否則模型會拿著一段不相干的手冊內容認真回答一個它其實不知道的問題。
            // → **相關度門檻尚未設定**，刻意不在此時憑感覺填一個數字；
            //   它必須由 S1-f 的評測題庫量出「該答」與「不該答」兩群的分數分佈之後才有依據。
            var hits = await SearchAsync("鸚鵡螺保養週期", allowAll: true);
            Assert.NotEmpty(hits);
        }

        [Fact]
        public async Task 純標點的提問不打資料庫也不回結果()
        {
            Assert.Empty(await SearchAsync("？？？", allowAll: true));
        }

        // ── 擷取物語料的閘門（§10.11） ───────────────────────────────────

        /// <summary>
        /// 擷取物測試共用的標記詞。「紫鳶」「鳶尾」兩個雙字在 134 頁手冊裡都不存在，
        /// 所以拿它提問時，命中的只可能是本檔自己種下的列——
        /// 這讓「檢索得到／檢索不到」的斷言不必再排除手冊的雜訊。
        /// </summary>
        private const string ExtractMarker = "紫鳶尾";

        [Fact]
        public async Task 核可且未過期的擷取物_零權限角色也檢索得到()
        {
            // §10.11.5：擷取物不綁畫面，故不設受眾閘門。這是整個設計最反直覺的一點——
            // 一個連任何 MenuKey 都沒有的角色，照樣問得到規則類的說明。
            await WithExtractAsync(approved: true, isStale: false, async () =>
            {
                var hits = await SearchAsync($"{ExtractMarker}規則是什麼", allowKeys: Array.Empty<string>());
                Assert.Contains(hits, h => h.DocUrl == ExtractDocUrl);
            });
        }

        [Fact]
        public async Task 未核可的擷取物一律檢索不到()
        {
            // §10.11.4：擷取本身會幻覺——讓 LLM 讀 BuildJournalLines 寫出分錄規則，
            // 寫錯了沒有任何機制會發現，而它會被當成權威答案引用。
            // 搭配開放查詢之後，Approved 是**唯一防線**，所以它必須連 allowAll 都擋得住。
            await WithExtractAsync(approved: false, isStale: false, async () =>
            {
                var hits = await SearchAsync($"{ExtractMarker}規則是什麼", allowAll: true);
                Assert.DoesNotContain(hits, h => h.DocUrl == ExtractDocUrl);
            });
        }

        [Fact]
        public async Task 來源已變動的擷取物即使已核可也檢索不到()
        {
            // §10.11.3：stale 直接排除，不做「先用著」。失敗方向與 DocIndexOutOfDateException
            // 一致——寧可少答，不可錯答。過期的擷取物會用非常有自信的語氣講舊規則，
            // 而中間產物沒有人會去看，所以沒有任何人會發現它講錯了。
            await WithExtractAsync(approved: true, isStale: true, async () =>
            {
                var hits = await SearchAsync($"{ExtractMarker}規則是什麼", allowAll: true);
                Assert.DoesNotContain(hits, h => h.DocUrl == ExtractDocUrl);
            });
        }

        [Fact]
        public async Task 重建手冊索引不會刪掉擷取物()
        {
            // 兩種語料同住一張表的頭號陷阱（§10.11.7 定案 #42 的代價）：
            // RebuildAsync 的「掃描不到的檔案就刪」會把**每一列擷取物**判為已刪除的手冊頁——
            // 它們的來源檔本來就不在 wwwroot/docs/user 底下。
            // 那是一次靜默的全語料清空，而下一次重建不會把它們補回來（重建只讀手冊）。
            await WithExtractAsync(approved: true, isStale: false, async () =>
            {
                using (var scope = _app.Services.CreateScope())
                {
                    var result = await scope.ServiceProvider
                        .GetRequiredService<IDocIndexBuilder>().RebuildAsync();

                    // 順帶驗第二件事：擷取物不得被算進手冊的「已移除檔案」統計。
                    Assert.Equal(0, result.FilesRemoved);
                }

                using (var scope = _app.Services.CreateScope())
                {
                    var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                    Assert.True(
                        await qa.DocChunks.AnyAsync(c => c.Corpus == DocCorpus.Extract),
                        "手冊索引重建把擷取物語料整個刪掉了——DocIndexBuilder 的作用範圍沒有收斂到 Corpus=manual。");
                }
            });
        }

        [Fact]
        public async Task 擷取物不吃MenuKey規則_手冊仍舊要吃()
        {
            // 同一張表兩套權限規則，最容易寫壞的方向是「把其中一套套到另一種語料上」。
            // 這條把兩者放在同一次斷言裡：同一個零權限角色，擷取物看得到、手冊看不到。
            await WithExtractAsync(approved: true, isStale: false, async () =>
            {
                var hits = await SearchAsync($"{ExtractMarker}規則是什麼", allowKeys: Array.Empty<string>());

                Assert.Contains(hits, h => h.DocUrl == ExtractDocUrl);
                Assert.All(hits.Where(h => h.DocUrl != ExtractDocUrl), h => Assert.Equal("", h.MenuKey));
            });
        }

        /// <summary>
        /// 擷取物的三道閘門（核可／過期／不吃 MenuKey）在**語意通道**上也要成立。
        ///
        /// <para><b>為什麼要單獨寫一支：</b>上面五條全部釘在詞彙通道，那是為了確定性；
        /// 但 <c>Approved</c> 是開放查詢之後的唯一防線（§10.11.4），
        /// 「它在另一條通道上也擋得住」不能靠「兩條通道共用 <c>DocChunkFilter</c>」這句話
        /// 就算數——共用是實作細節，實作細節會被改。</para>
        ///
        /// <para><b>為什麼是 <see cref="LiveOllamaFactAttribute"/>：</b>語意通道要有真的向量。
        /// 這裡**不呼叫 <c>EmbedPendingAsync()</c>**——那會把 679 塊全部補完，
        /// CPU 推論下要跑近一小時。只補本檔自己種的那一列（一次 passage ＋一次 query，
        /// 兩次呼叫），其餘手冊塊補不補都不影響這三個斷言。</para>
        /// </summary>
        [LiveOllamaFact]
        public async Task 擷取物閘門在語意通道上同樣成立()
        {
            using var scope = _app.Services.CreateScope();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

            // 沒設模型的話語意通道會直接退場，於是三個斷言全部「通過」但什麼都沒測到。
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定嵌入模型，本測試會在語意通道不存在的情況下全部通過——那是假綠。");

            const string question = ExtractMarker + "規則是什麼";

            await WithExtractAsync(approved: true, isStale: false, async () =>
            {
                await EmbedSeededExtractAsync(embedding);
                var hits = await SearchAsync(question, allowKeys: Array.Empty<string>(), embedding: embedding);

                // 先確認語意通道真的在運作，否則下面兩個 DoesNotContain 會因為
                // 「通道根本沒開」而通過——那是這一整支測試最容易變成假綠的地方。
                Assert.Contains(hits, h => h.DocUrl == ExtractDocUrl && h.SemanticScore != null);

                // 順帶把「擷取物不吃 MenuKey 規則」也在這條通道上驗掉。
                Assert.All(hits.Where(h => h.DocUrl != ExtractDocUrl), h => Assert.Equal("", h.MenuKey));
            });

            await WithExtractAsync(approved: false, isStale: false, async () =>
            {
                await EmbedSeededExtractAsync(embedding);
                var hits = await SearchAsync(question, allowAll: true, embedding: embedding);
                Assert.DoesNotContain(hits, h => h.DocUrl == ExtractDocUrl);
            });

            await WithExtractAsync(approved: true, isStale: true, async () =>
            {
                await EmbedSeededExtractAsync(embedding);
                var hits = await SearchAsync(question, allowAll: true, embedding: embedding);
                Assert.DoesNotContain(hits, h => h.DocUrl == ExtractDocUrl);
            });
        }

        /// <summary>
        /// 把 <see cref="WithExtractAsync"/> 剛種下的那一列補上向量。
        ///
        /// <para>三個欄位要一起寫：<c>DocVectorStore.Rank</c> 會比對
        /// <c>EmbeddingModel</c> 與現行組態，對不上就整列略過——
        /// 只寫 <c>Embedding</c> 的話候選集會是空的，而且不會有任何錯誤訊息。</para>
        /// </summary>
        private async Task EmbedSeededExtractAsync(IEmbeddingClient embedding)
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();

            var row = await qa.DocChunks.SingleAsync(c => c.Corpus == DocCorpus.Extract);

            // 索引期用 Passage、查詢期用 Query：前綴由 EmbeddingKind 決定，
            // 兩邊用錯邊的後果是相似度普遍偏低而不是報錯（§10.9）。
            var vector = await embedding.EmbedAsync(new[] { row.Text }, EmbeddingKind.Passage);

            row.Embedding = vector[0];
            row.EmbeddingModel = embedding.EmbeddingModelName;
            row.EmbeddingDim = vector[0].Length;
            await qa.SaveChangesAsync();
        }

        // ── 輔助 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 手冊語料的查詢起點。**每一處數手冊數量或改手冊欄位的地方都要用它**——
        /// 擷取物與手冊同住 <c>DocChunks</c>，未限縮的 <c>ExecuteUpdate</c> 會連擷取物一起改，
        /// 而未限縮的 <c>Count</c> 會讓寫死的語料規模數字因為另一種語料而紅。
        /// </summary>
        private static IQueryable<Modules.QA.Models.DocChunk> ManualChunkQuery(QaDbContext qa) =>
            qa.DocChunks.Where(c => c.Corpus == DocCorpus.Manual);

        private const string ExtractDocUrl = "/docs/rules/test-extract";

        /// <summary>
        /// 種一列擷取物、跑測試、無論成敗都清掉。
        ///
        /// <para><b>清理放在 finally 不是禮貌而是必要</b>：整合測試共用同一個
        /// PostgreSQL 測試庫（見 <see cref="RoleId"/> 的說明），殘留的擷取物會讓
        /// 別的測試——包含本檔數手冊塊數的那幾條——因為與它們無關的原因而紅或假綠。</para>
        /// </summary>
        private async Task WithExtractAsync(bool approved, bool isStale, Func<Task> body)
        {
            using (var scope = _app.Services.CreateScope())
            {
                var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
                await DeleteExtractsAsync(qa);

                var text = $"{ExtractMarker}規則：本列是 §10.11 擷取物閘門的測試資料，不是真的規則說明。";
                qa.DocChunks.Add(new Modules.QA.Models.DocChunk
                {
                    Corpus = DocCorpus.Extract,
                    FilePath = "extract/test-extract",
                    Lang = "zh",
                    ChunkIndex = 0,
                    HeadingPath = $"{ExtractMarker}規則",
                    Text = text,

                    // 切詞一定要走 CjkTokenizer——查詢期用的是同一個方法，
                    // 這裡自己拼字串的話會永遠對不上，而且不會有任何錯誤訊息。
                    Lexemes = CjkTokenizer.Tokenize($"{ExtractMarker}規則 {text}"),

                    DocUrl = ExtractDocUrl,
                    MenuKey = "",
                    Module = "",
                    SourcePath = ".claude/specifications/does-not-exist.md",
                    SourceHash = new string('0', 64),
                    Approved = approved,
                    IsStale = isStale,
                    IndexedAt = DateTime.UtcNow,
                });
                await qa.SaveChangesAsync();
            }

            try
            {
                await body();
            }
            finally
            {
                using var scope = _app.Services.CreateScope();
                await DeleteExtractsAsync(scope.ServiceProvider.GetRequiredService<QaDbContext>());
            }
        }

        private static Task DeleteExtractsAsync(QaDbContext qa) =>
            qa.DocChunks.Where(c => c.Corpus == DocCorpus.Extract).ExecuteDeleteAsync();

        private static string ModuleOf(string docUrl)
        {
            var rest = docUrl["/docs/user/".Length..];
            var idx = rest.IndexOf('/');
            return idx < 0 ? "" : rest[..idx];
        }

        /// <summary>
        /// 直接建構受測服務並注入受控的 <see cref="IUserContext"/>。
        ///
        /// **不透過 HTTP 請求**：S1 還沒有畫面，而且權限矩陣要能逐案設定。
        /// 這裡刻意不用真實 <c>UserContext</c>——它在沒有 HttpContext 時所有屬性皆為 null，
        /// 測起來會全部「通過」但其實是因為什麼都查不到（見 MEMORY 的背景服務陷阱）。
        ///
        /// <para><b><paramref name="embedding"/> 預設是
        /// <see cref="NoEmbeddingClient"/>，也就是本檔一律走純詞彙通道。</b>
        /// 理由見類別註解的〈通道要釘死〉一段——那不是為了避開語意通道，
        /// 而是因為本檔量的就是詞彙通道與索引本身。要驗語意通道的行為，
        /// 傳真實的 client 進來（見 <see cref="擷取物閘門在語意通道上同樣成立"/>）。</para>
        /// </summary>
        private async Task<IReadOnlyList<Modules.QA.Contracts.DocSearchHit>> SearchAsync(
            string question, bool allowAll = false, string[]? allowKeys = null, string? roleId = RoleId,
            IEmbeddingClient? embedding = null)
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            if (roleId != null) await SetPermissionsAsync(db, qa, roleId, allowAll, allowKeys);

            var service = new ManualSearchService(
                qa, db, new StubUserContext(roleId),
                embedding ?? NoEmbeddingClient.Instance,
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());
            return await service.SearchAsync(question, topK: 10);
        }

        /// <summary>
        /// 未設定嵌入模型的 client。<c>ManualSearchService.SemanticAsync</c> 見到空名稱就
        /// **直接不走語意通道**，這與正式環境「沒有設定嵌入模型」走的是同一條路徑，
        /// 不是測試專用的旁路（既有做法，見 <c>ManualRetrievalBenchmarkTests</c>）。
        /// </summary>
        private sealed class NoEmbeddingClient : IEmbeddingClient
        {
            public static readonly NoEmbeddingClient Instance = new();

            public string EmbeddingModelName => string.Empty;

            public Task<IReadOnlyList<float[]>> EmbedAsync(
                IReadOnlyList<string> inputs, EmbeddingKind kind, CancellationToken ct = default) =>
                throw new InvalidOperationException(
                    "本檔釘死在詞彙通道，不應呼叫嵌入。要測語意通道請明確傳入真實 client。");
        }

        /// <summary>
        /// 把該角色的可讀鍵設成指定集合（其餘一律關閉），讓權限測試互不干擾。
        ///
        /// <para><see cref="RoleId"/> 是本檔專屬且一開始沒有任何列，所以
        /// <paramref name="allowAll"/> 不能只把「現有的列」打開——那會是一場空。
        /// 先依**索引裡實際出現過的每個 MenuKey** 補齊列，all 才真的是 all。</para>
        /// </summary>
        private static async Task SetPermissionsAsync(
            ApplicationDbContext db, QaDbContext qa, string roleId, bool allowAll, string[]? allowKeys)
        {
            var indexed = await qa.DocChunks
                .Where(c => c.MenuKey != "")
                .Select(c => c.MenuKey)
                .Distinct()
                .ToListAsync();

            // 這三把是幾支測試直接指名的，就算某次索引沒切到也要有列可以開關。
            var known = indexed.Concat(new[] { "SalesRanking", "StockShortage", "DeliveryRate" })
                               .Distinct(StringComparer.Ordinal)
                               .ToList();

            var perms = await db.MenuPermissions.Where(p => p.RoleId == roleId).ToListAsync();
            EnsureRows(db, roleId, perms, known);

            var allow = allowAll
                ? new HashSet<string>(known, StringComparer.Ordinal)
                : new HashSet<string>(allowKeys ?? Array.Empty<string>(), StringComparer.Ordinal);

            EnsureRows(db, roleId, perms, allow);   // allowKeys 可能指到索引外的鍵
            foreach (var p in perms) p.CanRead = allow.Contains(p.MenuKey);

            await db.SaveChangesAsync();
        }

        /// <summary>缺列就補一列（<c>CanRead</c> 交給呼叫端統一決定，這裡不碰）。</summary>
        private static void EnsureRows(
            ApplicationDbContext db, string roleId, List<MenuPermission> existing, IEnumerable<string> keys)
        {
            foreach (var key in keys)
            {
                if (existing.Any(p => p.MenuKey == key)) continue;

                var row = new MenuPermission { RoleId = roleId, MenuKey = key };
                db.MenuPermissions.Add(row);
                existing.Add(row);
            }
        }

        private sealed class StubUserContext : IUserContext
        {
            public StubUserContext(string? roleId) => ActiveRoleId = roleId;

            public string? ActiveRoleId { get; }
            public string? PlantId => "P1";
            public string? TimeZoneId => "Asia/Taipei";
            public string? Language => "zh-TW";
            public string? UserId => ActiveRoleId == null ? null : "admin";
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => Array.Empty<string>();
        }
    }
}
