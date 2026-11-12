using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SmartFactory.MES.Services;
using Xunit;
using Match = System.Text.RegularExpressions.Match;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 畫面/操作一致性框架的建置期把關（R1–R5）。
    /// 任一規則出現「不在 baseline」的新違規即失敗 → 在交付前擋住 Vibe Coding 遺漏。
    /// </summary>
    public class ConventionTests
    {
        private static readonly Regex LocalizerKey =
            new(@"Localizer\[\s*""([^""]+)""", RegexOptions.Compiled);

        // R0（meta）：文件↔測試對帳。
        //   掃 ConventionTests.cs / ConventionTestsCx.cs 內所有 `public void (R\d+\w*|Cx\d+)_…`
        //   現役規則方法，斷言每個規則 ID 都登錄在 consistency-framework.md 的〈規則索引〉表
        //   （以獨立 token 比對，R1 不會誤配 R10/R11/R12，R4 不會誤配 R4b）。
        //   任何新增規則若漏記文件即失敗 → 以框架自身機制守框架自身文件，杜絕 R10 那類
        //   「程式新增/改義、文件未同步」的漂移。baseline 起點為空（零容忍）。
        [Fact]
        public void R0_ConventionDocInSync()
        {
            var root = ConventionScan.RepoRoot();
            var testDir = Path.Combine(root, "SmartFactory.MES.Tests", "Conventions");
            var ruleMethod = new Regex(
                @"public\s+void\s+((?:R\d+[a-z]*)|(?:Cx\d+))_",
                RegexOptions.Compiled);

            var ruleIds = new[] { "ConventionTests.cs", "ConventionTestsCx.cs" }
                .Select(f => Path.Combine(testDir, f))
                .Where(File.Exists)
                .SelectMany(f => ruleMethod.Matches(File.ReadAllText(f))
                                            .Select(m => m.Groups[1].Value))
                .Where(id => id != "R0")          // 排除本 meta 規則自身
                .Distinct()
                .ToList();

            var docPath = Path.Combine(root, ".claude", "specifications",
                "consistency-framework.md");
            var doc = File.Exists(docPath) ? File.ReadAllText(docPath) : string.Empty;

            // 獨立 token 比對：前後皆非英數，故 "R1" 不會命中 "R10"、"R4" 不會命中 "R4b"。
            bool DocHas(string id) =>
                Regex.IsMatch(doc,
                    @"(?<![A-Za-z0-9])" + Regex.Escape(id) + @"(?![A-Za-z0-9])");

            var violations = ruleIds
                .Where(id => !DocHas(id))
                .Select(id => $"{id}: 現役慣例規則未登錄於 consistency-framework.md〈規則索引〉表")
                .ToList();

            AssertRule("R0", violations);
        }

        // R1：View 用到的 @Localizer["k"]，其配對 resx（zh-TW 與 en-US）皆須有該 key。
        [Fact]
        public void R1_LocalizerKeys_ExistInBothResx()
        {
            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var text = File.ReadAllText(view);
                var keys = LocalizerKey.Matches(text)
                    .Select(m => m.Groups[1].Value)
                    .Where(k => !k.Contains('@') && !k.Contains('+'))
                    .Distinct().ToList();
                if (keys.Count == 0) continue;

                var rel = ConventionScan.Rel(view);                 // Modules/.../X.cshtml
                var noExt = rel[..^".cshtml".Length];
                var baseResx = Path.Combine(ConventionScan.RepoRoot(),
                    "Resources", noExt.Replace('/', Path.DirectorySeparatorChar));
                var zh = ConventionScan.ResxKeys(baseResx + ".zh-TW.resx");
                var en = ConventionScan.ResxKeys(baseResx + ".en-US.resx");

                if (keys.Any(k => !zh.Contains(k) || !en.Contains(k)))
                    violations.Add(rel);
            }
            AssertRule("R1", violations);
        }

        // R2：Controller 的 MenuKey 必須登錄於選單 resx 或 ProjectZMenuContributor。
        [Fact]
        public void R2_MenuKey_IsRegistered()
        {
            var registry = new System.Text.StringBuilder();
            foreach (var f in new[]
            {
                "Resources/ProjectZSharedResource.zh-TW.resx",
                "Resources/ProjectZSharedResource.en-US.resx",
                "Services/ProjectZMenuContributor.cs",
            })
            {
                var p = Path.Combine(ConventionScan.RepoRoot(), f);
                if (File.Exists(p)) registry.Append(File.ReadAllText(p));
            }
            var reg = registry.ToString();

            var violations = new List<string>();
            foreach (var ctrl in ConventionScan.Files("Modules", "*Controller.cs"))
            {
                var m = Regex.Match(File.ReadAllText(ctrl),
                    @"MenuKey\s*=\s*""([^""]+)""");
                if (m.Success && !reg.Contains($"\"{m.Groups[1].Value}\""))
                    violations.Add(ConventionScan.Rel(ctrl));
            }
            AssertRule("R2", violations);
        }

        // R3：每個 Print.cshtml 必須透過 Localizer 取字串（不得整頁硬編碼）。
        [Fact]
        public void R3_PrintViews_UseLocalizer()
        {
            var violations = ConventionScan.Files("Modules", "Print.cshtml")
                .Where(f => !File.ReadAllText(f).Contains("Localizer["))
                .Select(ConventionScan.Rel)
                .ToList();
            AssertRule("R3", violations);
        }

        // R4：wwwroot/js/*.js 的 alert/confirm 不得含硬編碼 CJK。
        [Fact]
        public void R4_Js_NoHardcodedCjkInDialogs()
        {
            var jsRoot = Path.Combine(ConventionScan.RepoRoot(), "wwwroot", "js");
            var violations = new List<string>();
            if (Directory.Exists(jsRoot))
            {
                foreach (var js in Directory.EnumerateFiles(jsRoot, "*.js"))
                {
                    var bad = File.ReadLines(js).Any(line =>
                        (line.Contains("alert(") || line.Contains("confirm("))
                        && ConventionScan.HasCjk(line));
                    if (bad) violations.Add(ConventionScan.Rel(js));
                }
            }
            AssertRule("R4", violations);
        }

        // R5：每個 *.zh-TW.resx 必有 *.en-US.resx，且 key 集合完全相同。
        [Fact]
        public void R5_ResxPairs_KeySetsMatch()
        {
            var violations = new List<string>();
            foreach (var zh in ConventionScan.Files("Resources", "*.zh-TW.resx"))
            {
                var en = zh.Replace(".zh-TW.resx", ".en-US.resx");
                if (!File.Exists(en)) { violations.Add(ConventionScan.Rel(zh)); continue; }
                if (!ConventionScan.ResxKeys(zh).SetEquals(ConventionScan.ResxKeys(en)))
                    violations.Add(ConventionScan.Rel(zh));
            }
            AssertRule("R5", violations);
        }

        // R4b：Modules/**/*.cshtml 內聯 <script> 的 alert/confirm 不得含硬編碼 CJK
        //（R4 僅掃 wwwroot/js；本規則補齊計畫設計的 .cshtml <script> 區塊覆蓋）。
        [Fact]
        public void R4b_CshtmlInlineScript_NoHardcodedCjkInDialogs()
        {
            var scriptBlock = new Regex(@"<script\b[^>]*>(.*?)</script>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var text = File.ReadAllText(view);
                foreach (Match blk in scriptBlock.Matches(text))
                {
                    var bad = blk.Groups[1].Value.Split('\n').Any(line =>
                        (line.Contains("alert(") || line.Contains("confirm("))
                        && ConventionScan.HasCjk(line));
                    if (bad) { violations.Add(ConventionScan.Rel(view)); break; }
                }
            }
            AssertRule("R4b", violations);
        }

        // R5b：Model 帶 [Required]／非可空實值型別（隱含必填）且未標 [Display]/
        // [DisplayName] 的屬性名，必須雙語皆有 key（否則驗證訊息 {0} 退回英文屬性名）。
        // 查找來源＝Core 驗證在地化的鏈式順序：SharedResource（Core 主導，本專案不修改）
        // → ProjectZValidationResource（ProjectZ 自有，顧問模組欄位放這裡）。
        // 兩者聯集即可，與 BASModule 的 AddValidationResourceFallback 登記一致。
        // 注意：ProjectZSharedResource 是選單／畫面用語，刻意「不」納入——它有同名但語意
        // 不同的鍵（DefectRate＝「不良率報表」選單 vs「不良率」欄位），納入會讓守門失效。
        [Fact]
        public void R5b_ValidationFieldNames_InSharedResource()
        {
            var root = ConventionScan.RepoRoot();
            var zh = ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "SharedResource.zh-TW.resx"));
            var en = ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "SharedResource.en-US.resx"));
            zh.UnionWith(ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "ProjectZValidationResource.zh-TW.resx")));
            en.UnionWith(ConventionScan.ResxKeys(
                Path.Combine(root, "Resources", "ProjectZValidationResource.en-US.resx")));

            Type[] types;
            try
            {
                types = typeof(SmartFactory.MES.SharedResource).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            var violations = new HashSet<string>();
            foreach (var t in types)
            {
                if (t is not { IsClass: true, IsAbstract: false }) continue;
                var ns = t.Namespace ?? "";
                if (!ns.StartsWith("SmartFactory.MES.Modules") &&
                    ns != "SmartFactory.MES.Models" &&
                    !ns.Contains(".ViewModels") && !ns.Contains(".Models"))
                    continue;

                // R5b 框架/基礎建設型別排除（非業務表單，欄位不經驗證 {0} 本地化呈現）：
                //  - ASP.NET Identity 使用者（ApplicationUser : IdentityUser）：
                //    AccessFailedCount/EmailConfirmed/LockoutEnabled/MustChangePassword/
                //    PhoneNumberConfirmed/TwoFactorEnabled
                //  - 例外型別（MrpBom*Exception : InvalidOperationException）：HResult
                //  - 分頁輔助型別（PaginatedList<T>）：PageIndex/TotalPages/HasNextPage/HasPreviousPage
                //  - 安全基礎建設（L4 PasswordHistory）：內部密碼歷史表，UserId/PasswordHash
                //    永不呈現於業務表單、不經驗證 {0} 本地化，屬 infra 非使用者輸入欄位
                //  - DbContext 子類別（模組自有上下文，如 QA 的 QaDbContext）：
                //    基底 DbContext 自帶 public DbContextId ContextId（非可空實值型別），
                //    對「模組命名空間下的 public class」這個條件而言是必然誤報——
                //    這是通則而非個案：任何放進 Modules/ 的 DbContext 都會撞到同一筆。
                //  - 手冊索引表（QA 的 DocChunk）與其無鍵投影（DocChunkHit/DocChunkVector）：
                //    ChunkIndex/IndexedAt/EmbeddingDim 是索引內部欄位，與 PasswordHistory 同類——
                //    沒有任何表單會繫結它，補 resx 鍵等於造假欄位名。
                //  - 擷取物 manifest 的序列化契約（QA 的 ExtractManifest/ExtractEntry）：
                //    它描述的是 .claude/qa-extracts/*.json 的檔案格式，只被離線匯入器讀，
                //    **沒有任何 Razor 檢視繫結它**，欄位名不會經由驗證訊息的 {0} 呈現。
                //    與上一筆同類；之所以不是 nested private（那條通則會自動接住），
                //    是因為整合測試要在另一個組件裡建構它來寫測試用 manifest。
                //  - private 巢狀型別（如 OllamaClient 內描述 Ollama JSON 線上格式的 DTO）：
                //    它們**在宣告類別之外根本無法被引用**，Razor 的 @model 也拿不到，
                //    因此不可能是業務表單欄位。這與上面幾筆同樣是通則而非個案——
                //    任何「只在一個類別內部使用的序列化 DTO」都會撞到同一筆。
                if (t.IsNestedPrivate) continue;
                if (typeof(Exception).IsAssignableFrom(t)) continue;
                if (typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(t)) continue;
                if (t.Name.StartsWith("PaginatedList")) continue;
                //  - 超量判定結果（OverQtyCheck，2026-10-12）：服務回傳值，不是表單，Allowed／BySupervisor 不會出現在畫面欄位
                if (t.Name == "OverQtyCheck") continue;
                //  - 總帳存貨對明細帳的對帳結果（InventoryGlReconciler，2026-10-22）：稽核內部的計算值，
                //    畫面看到的是由它組出的 AuditFailureDetail 訊息，沒有任何表單繫結這三個 record
                if (t.Name is "InventoryGlBridge" or "WipInGlRow" or "DocGlDiffRow") continue;
                //  - 擷取物只讀畫面的投影（QA 的 ExtractChunkVm）：與 DocChunk 同一類——
                //    它是唯讀清單的投影，**沒有任何表單繫結它**，欄位標題全部來自該檢視自己的
                //    resx（PageCode／HeadingPath／State／IndexedAt／Text）。把 Approved／IsStale／
                //    IndexedAt 補進 SharedResource 只會多三個沒有任何畫面在用的鍵，
                //    也就是這條註解開頭那句「補 resx 鍵等於造假欄位名」。
                //  - 廠別參數的描述與欄位模型（BAS 的 PlantParamDef／PlantParamFieldModel）：
                //    與上面幾筆同一個通則。`PlantParamDef` 描述「有哪些參數可以廠別化」，
                //    `PlantParamFieldModel` 是三態欄位的唯讀投影；廠別參數面板的表單欄位名是
                //    `pp_ovr_{key}` / `pp_val_{key}` 這種手寫字串，由 PlantFormEnhancementFilter
                //    直接讀 Request.Form，**沒有任何模型繫結**，因此 Kind／IsOverridden 不會
                //    經由驗證訊息的 {0} 呈現，補 resx 鍵等於造假欄位名。
                //  - NL 查詢的追問狀態與稽核（QA 的 QaTurn）：與 DocChunk／PasswordHistory 同一類——
                //    它是內部狀態表，唯一的讀取入口是「同一個 UserId 的下一輪確認」，
                //    沒有任何 Razor 檢視繫結它，ThreadId／RoundNo／RowsReturned 不會經由
                //    驗證訊息的 {0} 呈現。補 resx 鍵等於造假欄位名。
                //  - 庫存過帳請求 DTO（SimplifiedPostLine）：呼叫端在程式裡組出來交給
                //    InventoryPostingService 的參數物件，**沒有任何表單繫結它**，也不會被
                //    任何檢視渲染；欄位名不會經由驗證訊息的 {0} 呈現。同上一個通則：
                //    補 resx 鍵等於造假欄位名。
                //  - 手機簽核摘要的回應契約（ApprovalSummary／ApprovalSummaryRow／
                //    ApprovalSummaryLine）：它描述的是 /api/mobile/approval/{id}/summary 的
                //    **JSON 形狀**，由 list.page.js 以 DOM API 逐格渲染，
                //    **沒有任何 Razor 檢視繫結它**，欄位名不會經由驗證訊息的 {0} 呈現。
                //    與 ExtractManifest 同類。之所以不是改成 record struct（那條通則會自動接住），
                //    是因為 provider 回傳 ApprovalSummary?，改值型別會讓每個使用點都要寫 .Value——
                //    為了通過一條規則而扭曲呼叫端，代價比一筆具名排除大。
                //  - 報表排程定義（RPS 的 ReportDefinition）：與上面幾筆同一個通則。
                //    排程畫面的 Create/Edit 繫結的是 `ReportScheduleFormVm`，**不是這個 entity**；
                //    它唯一出現在檢視裡的地方是 ReportSchedule/Index.cshtml 的唯讀清單
                //    （`PaginatedList<ReportDefinition>`），欄位標題來自該檢視自己的 resx。
                //    因此 IsActive／ManualRunRequested 不會經由驗證訊息的 {0} 呈現。
                //    ManualRunRequested 更是純系統旗標：RunNow 設 true、派工器跑完清 false，
                //    沒有任何使用者輸入得到它——補 resx 鍵等於造假欄位名。
                //  - 現場遙測（ClientTelemetryEvent 與儀表板投影 FieldTelemetry*）：與 ScheduledJobRun 同一個通則。
                //    事件由 /api/telemetry/events 經 ClientTelemetryService 以 SQL 寫入，**沒有任何表單繫結它**；
                //    儀表板的 VM／Row 只在唯讀檢視渲染，欄位標題全部來自該檢視自己的 resx。
                //    Count／Hours／ErrorCount 等不會經由驗證訊息的 {0} 呈現，補 resx 鍵等於造假欄位名。
                //  - 累計欄位自動修正的預覽與結果（DerivedFieldRepairProposal／Result）：服務的回傳值，
                //    資料稽核畫面只唯讀渲染、勾選送出的是字串鍵，沒有任何模型繫結它們。
                //  - 排程執行紀錄（ScheduledJobRun）：與 PasswordHistory／QaTurn 同一個通則。
                //    它記的是「某支每日排程在某個當地日期跑過了沒」，唯一的讀取入口是
                //    DailyJobServiceBase 自己的去重判定，**沒有任何 Razor 檢視繫結它**，
                //    也沒有任何表單寫得到它——JobDate／StartedAtUtc／CompletedAtUtc／Succeeded
                //    全部由基底在執行前後填入。補 resx 鍵等於造假欄位名。
                if (t.Name is "PasswordHistory" or "DocChunk" or "DocChunkHit" or "DocChunkVector"
                    or "ExtractManifest" or "ExtractEntry" or "ExtractChunkVm"
                    or "PlantParamDef" or "PlantParamFieldModel"
                    or "ApprovalSummary" or "ApprovalSummaryRow" or "ApprovalSummaryLine"
                    or "QaTurn" or "SimplifiedPostLine" or "ReportDefinition"
                    or "ScheduledJobRun"
                    or "ClientTelemetryEvent" or "FieldTelemetryReportVm" or "FieldTelemetryErrorRow"
                    or "FieldTelemetryEndpointRow" or "FieldTelemetryDeviceRow"
                    or "DerivedFieldRepairProposal" or "DerivedFieldRepairResult") continue;
                var derivesIdentityUser = false;
                for (var b = t.BaseType; b != null; b = b.BaseType)
                    if (b.Name == "IdentityUser") { derivesIdentityUser = true; break; }
                if (derivesIdentityUser) continue;

                System.Reflection.PropertyInfo[] props;
                try { props = t.GetProperties(System.Reflection.BindingFlags.Public |
                                              System.Reflection.BindingFlags.Instance); }
                catch { continue; }

                foreach (var p in props)
                {
                    if (p.GetIndexParameters().Length > 0) continue;

                    // 唯讀計算屬性：沒有公開 setter 就不會被表單繫結，也就不可能出現在
                    // 驗證訊息的 {0} 裡——要求它有 resx key 是純粹的誤報。
                    // （init-only 與 record 位置參數都算有 setter，仍在掃描範圍內。）
                    // 觸發點：AuditFailureDetail.IsBlocking —— 由 AuditType 現算的嚴重度判斷。
                    if (p.GetSetMethod(nonPublic: false) == null) continue;

                    var attrs = p.GetCustomAttributes(true);
                    if (attrs.Any(a =>
                        a is System.ComponentModel.DataAnnotations.DisplayAttribute ||
                        a is System.ComponentModel.DisplayNameAttribute))
                        continue;   // 顯式顯示名 → 不需 resx key

                    // 排除系統內部/資料庫自動生成/審計欄位
                    if (attrs.Any(a =>
                        a is System.ComponentModel.DataAnnotations.KeyAttribute ||
                        a is System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute ||
                        a is System.ComponentModel.DataAnnotations.TimestampAttribute))
                        continue;

                    var lowerName = p.Name.ToLower();
                    if (lowerName == "rowversion" ||
                        lowerName == "createdat" || lowerName == "updatedat" ||
                        lowerName == "createdby" || lowerName == "updatedby" ||
                        lowerName == "id" ||
                        lowerName == "xmin" ||        // PostgreSQL 並發 token（UseXminAsConcurrencyToken）
                        lowerName == "timestamp")     // 系統時間戳（SyncLog 等），非使用者輸入欄位
                        continue;

                    // 規格縮寫／原始欄位代碼（含底線，如 CD_BOR/QT_BOR/TM_SETUP/TM_UNIT，
                    // mfg-domain.md §3.1）：以代碼識別、不經驗證 {0} 本地化呈現，排除之。
                    if (p.Name.Contains('_')) continue;

                    var hasRequired = attrs.Any(a =>
                        a is System.ComponentModel.DataAnnotations.RequiredAttribute);
                    var pt = p.PropertyType;

                    // 排除非簡單型別（只保留 string、值型別、Nullable值型別，排除自定義導覽屬性）
                    var realType = Nullable.GetUnderlyingType(pt) ?? pt;
                    if (realType != typeof(string) && !realType.IsValueType)
                        continue;

                    var implicitReq = pt.IsValueType &&
                        Nullable.GetUnderlyingType(pt) == null;
                    if (!hasRequired && !implicitReq) continue;

                    if (!zh.Contains(p.Name) || !en.Contains(p.Name))
                        violations.Add(p.Name);
                }
            }
            AssertRule("R5b", violations);
        }

        // R6：表單不外露瀏覽器原生驗證泡泡。
        //  (a) crud-panel.js 必含 armValidation 並於注入後呼叫（自我完整性，硬性）
        //  (c) crud-panel.js 注入容器 id 契約一致（sidePanelBody，防漂移，硬性）
        //  (b) 用 openPanel 的 Index 必引入 _CrudPanelScripts（baseline 收斂）
        [Fact]
        public void R6_Forms_NoNativeValidationBubble()
        {
            var js = Path.Combine(ConventionScan.RepoRoot(),
                "wwwroot", "js", "crud-panel.js");
            Assert.True(File.Exists(js), "crud-panel.js 不存在");
            var jsText = File.ReadAllText(js);

            Assert.True(jsText.Contains("function armValidation"),
                "R6(a)：crud-panel.js 缺 armValidation（原生驗證泡泡收斂邏輯）");
            Assert.True(
                Regex.Matches(jsText, @"armValidation\s*\(").Count >= 3,
                "R6(a)：armValidation 未在 openPanel/submitPanel 注入後呼叫" +
                "（定義+兩處呼叫至少 3 次）");
            Assert.True(jsText.Contains("sidePanelBody"),
                "R6(c)：crud-panel.js 注入容器 id 契約漂移（應為 sidePanelBody）");

            var violations = new List<string>();
            foreach (var idx in ConventionScan.Files("Modules", "Index.cshtml"))
            {
                var text = File.ReadAllText(idx);
                if (text.Contains("openPanel(") &&
                    !text.Contains("_CrudPanelScripts"))
                    violations.Add(ConventionScan.Rel(idx));
            }
            AssertRule("R6", violations);
        }

        // R6b：面板成功契約一致。
        //  (a) submitPanel 以「<script 開頭」結構判斷成功，禁子字串嗅探（硬性）
        //  (b) 控制器成功回傳必為純 <script> 文件（含 refreshList 卻非 <script
        //      開頭的 Content(...) 即違規；baseline 收斂）
        [Fact]
        public void R6b_PanelSuccessContract_Structural()
        {
            var js = Path.Combine(ConventionScan.RepoRoot(),
                "wwwroot", "js", "crud-panel.js");
            var jsText = File.ReadAllText(js);
            // 去註解後再判斷（說明性註解可保留舊寫法字樣，不算違規）
            var code = Regex.Replace(jsText, @"/\*[\s\S]*?\*/", "");
            code = Regex.Replace(code, @"//[^\n]*", "");
            Assert.False(code.Contains("includes('refreshList')"),
                "R6b(a)：submitPanel 仍用子字串嗅探 includes('refreshList')");
            Assert.True(code.Contains("'<script'"),
                "R6b(a)：submitPanel 缺『<script 開頭』結構判斷");

            var contentCall = new Regex(
                @"Content\(\s*(@?""(?:[^""\\]|\\.)*"")", RegexOptions.Singleline);
            var violations = new List<string>();
            foreach (var ctrl in ConventionScan.Files("Modules", "*Controller.cs"))
            {
                var text = File.ReadAllText(ctrl);
                foreach (Match m in contentCall.Matches(text))
                {
                    var lit = m.Groups[1].Value;
                    var body = lit.TrimStart('@', '"');
                    if (lit.Contains("refreshList") &&
                        !body.TrimStart().StartsWith("<script"))
                    {
                        violations.Add(ConventionScan.Rel(ctrl));
                        break;
                    }
                }
            }
            AssertRule("R6b", violations);
        }

        // R6c：動態子表（Grid）新增列須重註冊 unobtrusive 驗證。
        //   面板開啟後用 JS append 的明細列（addLine 等），其 data-val-* 不會被
        //   unobtrusive 重新 parse → 缺即時前端驗證（伺服端 novalidate 仍兜底，屬 UX
        //   一致性把關，非正確性破口 — 見 consistency-framework.md Phase F1）。
        //   規則：.cshtml 內聯 <script> 出現動態建列（appendChild/insertAdjacentHTML/
        //   insertRow/cloneNode）且該列含表單輸入（<input>/<select>/<textarea> 或 cloneNode
        //   模板）者，該檔必須出現重註冊呼叫。合規 API 兩種：crud-panel.js 的 armValidation(、
        //   BAS legacy 面板（#crudPanelBody + module-validation.js）的 parseModuleValidation(。
        //   既有違規入 baseline 分批回補。
        [Fact]
        public void R6c_DynamicRows_RearmValidation()
        {
            var scriptBlock = new Regex(@"<script\b[^>]*>(.*?)</script>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var rowInsert = new Regex(
                @"appendChild\(|insertAdjacentHTML\(|\.insertRow\(|cloneNode\(",
                RegexOptions.Compiled);
            var buildsInputs = new Regex(
                @"<input\b|<select\b|<textarea\b|cloneNode\(",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var text = File.ReadAllText(view);
                // 已重註冊 → 合規。兩種 API：crud-panel.js armValidation()、
                // BAS legacy 面板 module-validation.js parseModuleValidation()。
                if (text.Contains("armValidation(") ||
                    text.Contains("parseModuleValidation(")) continue;

                foreach (Match blk in scriptBlock.Matches(text))
                {
                    var js = blk.Groups[1].Value;
                    if (rowInsert.IsMatch(js) && buildsInputs.IsMatch(js))
                    {
                        violations.Add(ConventionScan.Rel(view));
                        break;
                    }
                }
            }
            AssertRule("R6c", violations);
        }

        // R7：Controller 回傳給使用者的訊息字面不得硬編碼 CJK
        //（message=/AddModelError/TempData/alert 內的中文 → 改走 IStringLocalizer）。
        //  R4/R4b 僅掃 js/cshtml；本規則補齊 controller .cs 的伺服端訊息覆蓋。
        //  既有各模組大量違規以 baseline 收斂，未來分批回補；新違規一律擋下。
        [Fact]
        public void R7_ControllerMessages_NoHardcodedCjk()
        {
            var marker = new Regex(
                @"\bmessage\s*=|AddModelError\s*\(|TempData\[|\balert\s*\(",
                RegexOptions.Compiled);
            var violations = new List<string>();
            foreach (var ctrl in ConventionScan.Files("Modules", "*Controller.cs"))
            {
                var text = File.ReadAllText(ctrl);
                // 去註解（block + line），避免中文程式碼註解誤判
                var code = Regex.Replace(text, @"/\*[\s\S]*?\*/", "");
                code = Regex.Replace(code, @"//[^\n]*", "");
                var bad = code.Split('\n').Any(line =>
                    marker.IsMatch(line) && ConventionScan.HasCjk(line));
                if (bad) violations.Add(ConventionScan.Rel(ctrl));
            }
            AssertRule("R7", violations);
        }

        // R8：View 內「直接輸出且無參數」的 @Localizer["k"]，若其 resx 值含未跳脫的
        //   {N} 佔位符，IViewLocalizer 回傳的 LocalizedHtmlString 在 WriteTo 時會立即
        //   套用 string.Format → FormatException、整頁渲染中斷（曾導致 Routing 新增
        //   面板「載入失敗」）。修法：resx 值改用 {{N}} 跳脫（給 JS .replace 用），
        //   或呼叫端帶入對應參數。
        //   註：Localizer["k"].Value（取原始字串，不渲染）為安全寫法，以負向預查排除。
        //   ⚠️ 2026-10-19：原本只認變數名 Localizer，`@inject IViewLocalizer L` 的 L["k"] 全數漏網——
        //   選擇庫存視窗（_StockPickerModal）的 data-reserved="@L["PlantReservedFmt"]" 讓 6 種單據面板
        //   整個渲染失敗、UI 全套 55 支紅，本規則卻是綠的。改為讀每個 View 的 @inject 取得實際變數名。
        [Fact]
        public void R8_LocalizerNoArgs_NoUnescapedPlaceholder()
        {
            var injected = new Regex(@"@inject\s+(?:[\w.]+\.)?I(?:View|Html)Localizer(?:<[^>]*>)?\s+(\w+)",
                RegexOptions.Compiled);
            var placeholder = new Regex(@"\{\d", RegexOptions.Compiled);
            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var rel = ConventionScan.Rel(view);
                var noExt = rel[..^".cshtml".Length];
                var baseResx = Path.Combine(ConventionScan.RepoRoot(),
                    "Resources", noExt.Replace('/', Path.DirectorySeparatorChar));
                var zhVals = ConventionScan.ResxValues(baseResx + ".zh-TW.resx");
                var enVals = ConventionScan.ResxValues(baseResx + ".en-US.resx");
                if (zhVals.Count == 0 && enVals.Count == 0) continue;

                var text = File.ReadAllText(view);
                var names = injected.Matches(text).Select(x => x.Groups[1].Value)
                    .Append("Localizer").Distinct().Select(Regex.Escape);
                // "k" 後緊跟 ] 且其後非 .（排除 .Value/.Name/.ToString() 等取原始字串的安全用法）
                var noArg = new Regex(@"(?<![\w.])(?:" + string.Join("|", names) + @")\[\s*""([^""]+)""\s*\](?!\s*\.)");
                foreach (Match m in noArg.Matches(text))
                {
                    var key = m.Groups[1].Value;
                    if (key.Contains('@') || key.Contains('+')) continue;
                    foreach (var vals in new[] { zhVals, enVals })
                    {
                        if (vals.TryGetValue(key, out var v) &&
                            placeholder.IsMatch(v.Replace("{{", "").Replace("}}", "")))
                        {
                            violations.Add($"{rel}:{key}");
                            break;
                        }
                    }
                }
            }
            AssertRule("R8", violations);
        }

        // R9：number input 的 spinner step 一律 "any"（上下三角形 ±1）。
        //   HTML number input 的 step 同時決定「spinner 增減量」與「允許輸入精度」；
        //   回精確小數（step="0.000001"）會使三角形每按一次只 ±0.000001。修法集中三個
        //   產生源回 "any"：C# UomStepHelper/CurrencyStepHelper.GetStep、
        //   JS uom-step.js applyUomStep / currency-format.js applyCurrencyStep+currencyStepOf；
        //   精度改由 min（最小正值）+ 伺服端 RoundQtyAsync/RoundAsync 把關。
        //   (a) 守住核心產生源不回精確 step（防「修了 JS 沒修 C#」的 MFG 類漂移復發）。
        //   (b) Modules/**/*.cshtml 不得新增寫死 step="0.xxx"（既有入 baseline 分批回補；
        //       少數刻意值如 step="0.5"（加班半小時）/工時 step="0.01" 留 baseline 視為 won't-fix）。
        [Fact]
        public void R9_NumberInputStep_IsAny()
        {
            var root = ConventionScan.RepoRoot();

            // (a) 核心產生源契約：GetStep(int) 必回 "any"；JS 必設 step="any"。
            var core = new List<string>();
            var getStepAny = new Regex(@"GetStep\(int\s+\w+\)\s*=>\s*""any""",
                RegexOptions.Compiled);
            foreach (var cs in new[]
            {
                "Modules/BAS/Services/UomStepHelper.cs",
                "Modules/BAS/Services/CurrencyStepHelper.cs",
            })
            {
                if (!getStepAny.IsMatch(File.ReadAllText(Path.Combine(root, cs))))
                    core.Add($"{cs}: GetStep(int) 未回 \"any\"");
            }
            if (!File.ReadAllText(Path.Combine(root, "wwwroot/js/uom-step.js"))
                    .Contains("inputEl.step = \"any\""))
                core.Add("wwwroot/js/uom-step.js: applyUomStep 未設 step=\"any\"");
            if (!File.ReadAllText(Path.Combine(root, "wwwroot/js/currency-format.js"))
                    .Contains("inputEl.step = \"any\""))
                core.Add("wwwroot/js/currency-format.js: applyCurrencyStep 未設 step=\"any\"");
            Assert.True(core.Count == 0,
                "R9 核心產生源 step 契約破損：\n  " + string.Join("\n  ", core));

            // (b) view 不得寫死小數 step（spinner ±0.xxx）。
            //     以「檔案路徑」為違規鍵（同 R4b/R7），不帶行號——行號會隨無關編輯漂移，
            //     使 baseline 失準（曾因鄰近功能改動位移而誤判 CI 紅）。
            var lit = new Regex(@"step\s*=\s*""0\.\d+""", RegexOptions.Compiled);
            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                if (File.ReadLines(view).Any(l => lit.IsMatch(l)))
                    violations.Add(ConventionScan.Rel(view));
            }
            AssertRule("R9", violations);

            // (c) 數量欄位不得把 step／min 寫死成整數。
            //     (b) 只掃 step="0.x"，對 step="1" 是盲的——而後者才是嚴重的那一個：
            //     0.x 只是 spinner 步進不好按，"1"（與 min="1"）卻讓瀏覽器原生驗證
            //     直接擋掉小數，小數單位的品號在該畫面上根本輸不進去，且畫面上沒有
            //     任何訊息指向真正的原因。2026-09-11 實地撞到：SalesReturn／GoodsShipment
            //     兩支 _Form 各 4 處寫死，而兩支的 Controller 都呼叫 RoundQtyAsync——
            //     伺服端按單位精度收數量，畫面卻只收整數，兩邊契約相反。
            //     判準只認「數量」欄位（class 帶 line-qty，或 name 以 Qty 結尾），
            //     故天數／次數／序號那類整數欄位不受影響；readonly 顯示欄位也排除。
            //     語意上就是整數的（抽樣數／不良數）記入 baseline-R9c.txt。
            var numberInput = new Regex("<input\\b[^>]*>",
                RegexOptions.Compiled | RegexOptions.Singleline);
            var qtyName = new Regex("name=\"[^\"]*Qty(\\]|\")", RegexOptions.Compiled);
            var hardStep = new Regex("step\\s*=\\s*\"1\"", RegexOptions.Compiled);
            var hardMin = new Regex("min\\s*=\\s*\"1\"", RegexOptions.Compiled);
            var intQty = new List<string>();
            // 兩個判準分開計數：它們是 OR，若只檢查聯集，一邊失效時另一邊會把它掩護過去
            // （2026-09-11 突變驗證實測：class 判準改名後聯集仍 42 >= 40，規則靜默全綠）。
            var probedByClass = 0;
            var probedByName = 0;
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var text = File.ReadAllText(view);
                foreach (Match m in numberInput.Matches(text))
                {
                    var tag = m.Value;
                    if (!tag.Contains("type=\"number\"")) continue;
                    if (tag.Contains("readonly")) continue;
                    var byClass = tag.Contains("line-qty");
                    var byName = qtyName.IsMatch(tag);
                    if (!byClass && !byName) continue;
                    if (byClass) probedByClass++;
                    if (byName) probedByName++;
                    if (hardStep.IsMatch(tag) || hardMin.IsMatch(tag))
                        intQty.Add(ConventionScan.Rel(view));
                }
            }
            // 自我檢查：選擇器若失效（欄位改名、模板重寫）規則會靜默永遠綠，
            // 那比沒有規則更糟——它讓人以為這一面有守。比照 R21／R22／R32。
            Assert.True(probedByClass >= 35,
                $"R9(c) 掃描器的 class 判準可能失效：line-qty 只探測到 {probedByClass} 個（預期 >= 35）");
            Assert.True(probedByName >= 35,
                $"R9(c) 掃描器的 name 判準可能失效：name 以 Qty 結尾只探測到 {probedByName} 個（預期 >= 35）");
            AssertRule("R9c", intQty);
        }

        // R6d：編輯表單每個可編輯 asp-for 欄位都要有對應 asp-validation-for span（否則欄位級
        //   驗證錯誤無處顯示 → 面板只閃一下、沒訊息）。排除 hidden/checkbox(布林旗標)/readonly；
        //   表單若用 asp-validation-summary="All" 則整表顯示全部錯誤、視為合規。
        //   以檔案路徑為違規鍵（同 R4b/R7/R9）；既有缺漏入 baseline 分批回補。
        [Fact]
        public void R6d_FormFields_HaveValidationSpan()
        {
            var aspFor = new Regex(@"asp-for\s*=\s*""([^""]+)""", RegexOptions.Compiled);
            var aspValFor = new Regex(@"asp-validation-for\s*=\s*""([^""]+)""", RegexOptions.Compiled);
            var violations = new List<string>();

            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var lines = File.ReadAllLines(view);
                var text = string.Join('\n', lines);

                if (!text.Contains("<form")) continue;                          // 僅編輯表單
                if (text.Contains("asp-validation-summary=\"All\"")) continue;   // All summary 已顯示全部欄位錯誤

                // 可編輯且可能產生驗證錯誤的繫結欄位（排除 hidden/checkbox/readonly）
                var bound = new HashSet<string>();
                foreach (var line in lines)
                {
                    if (line.Contains("type=\"hidden\"") || line.Contains("type=\"checkbox\"")
                        || line.Contains("readonly")) continue;
                    foreach (Match m in aspFor.Matches(line)) bound.Add(m.Groups[1].Value);
                }
                if (bound.Count == 0) continue;   // 無繫結欄位（純 JS 表單/查詢列）

                var spans = aspValFor.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
                if (bound.Any(b => !spans.Contains(b)))
                    violations.Add(ConventionScan.Rel(view));
            }

            AssertRule("R6d", violations);
        }

        // R10：Modules/**/*.cshtml 的 HTML placeholder 屬性不得含硬編碼 CJK 字串。
        //   placeholder="統一編號" 在英文模式下仍顯示中文。
        //   正確寫法：placeholder="@Localizer["SomeKey"]"。
        //   既有違規入 baseline 分批回補；新違規一律擋下。
        [Fact]
        public void R10_HtmlPlaceholders_NoHardcodedCjk()
        {
            // 匹配 placeholder="..." 且值不以 @ 開頭（排除 @Localizer 用法）
            var hardcoded = new Regex(
                @"placeholder\s*=\s*""([^""@][^""]*)""",
                RegexOptions.Compiled);
            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var lines = File.ReadAllLines(view);
                var bad = lines.Any(line =>
                    hardcoded.Matches(line).Cast<Match>()
                              .Any(m => ConventionScan.HasCjk(m.Groups[1].Value)));
                if (bad) violations.Add(ConventionScan.Rel(view));
            }
            AssertRule("R10", violations);
        }

        // R11：分頁 UI 一律走共用 partial，不得各畫面自己複製一份。
        //   (a) `@model PaginatedList<T>` 的畫面必須引用 `_Pagination`（摘要 + 頁碼）
        //   (b) 同一支畫面必須引用 `_PageSizeSelect`（每頁筆數，住在查詢表單內）
        //   (c) 任何 View 都不得自帶 `class="pagination"` 或 `<select name="pageSize"`
        //
        // ⚠️ 2026-09-11 之前這條檢查的是「畫面內有沒有 name="pageSize" 與 Model.PageIndex」——
        //    也就是**每支畫面各自複製一份分頁 markup 才算合格**。86 份複製品的下場：
        //    摘要措辭長出 zh 4 種 / en 9 種，`PageInfo` 同一個 key 同時存在 2 參數與 3 參數
        //    兩種格式（複製貼上時會少印一個數字，而且不會有任何錯誤）。
        //    更值得記的是 baseline 裡那一行 `AccountingPeriod: 缺分頁導航`——那不是格式問題，
        //    是使用者**翻不到第 2 頁**（controller 每頁 20 筆，畫面一個分頁控制項都沒有）。
        //    規則看見了，baseline 把它變成永久容忍。所以這次收口連 baseline 一起清空。
        [Fact]
        public void R11_PaginationUi_GoesThroughSharedPartials()
        {
            var modelDecl = new Regex(@"@model\s+.*PaginatedList\s*<", RegexOptions.Compiled);
            var violations = new List<string>();
            var paginated = 0;

            foreach (var view in AllViews())
            {
                var text = File.ReadAllText(view);
                var rel  = ConventionScan.Rel(view);
                var isPartial = Path.GetFileName(view) is "_Pagination.cshtml" or "_PageSizeSelect.cshtml";

                if (!isPartial)
                {
                    if (text.Contains("class=\"pagination"))
                        violations.Add($"{rel}: 自帶分頁 markup，請改用 _Pagination partial");
                    if (text.Contains("<select name=\"pageSize\""))
                        violations.Add($"{rel}: 自帶每頁筆數下拉，請改用 _PageSizeSelect partial");
                }

                if (!modelDecl.IsMatch(text)) continue;
                paginated++;
                if (!text.Contains("\"_Pagination\""))
                    violations.Add($"{rel}: @model 是 PaginatedList 卻沒有引用 _Pagination——使用者翻不到第 2 頁");
                if (!text.Contains("\"_PageSizeSelect\""))
                    violations.Add($"{rel}: 缺每頁筆數下拉（_PageSizeSelect）");
            }

            // 每頁筆數住在查詢表單裡，那個表單一定得有送出的辦法。
            // ⚠️ 這一條是 2026-09-11 用血換來的：把各畫面的 pageSize <select> 換成共用 partial 時，
            //    有兩支畫面的下拉和「查詢／重設／列印／匯出」擠在同一個 <div class="col-12">，
            //    整欄被一起換掉——使用者連查詢鈕都不見了。標籤配平檢查抓不到，因為被刪掉的
            //    區塊本身是配平的；Razor 也照樣編譯成功。
            foreach (var view in AllViews())
            {
                var text = File.ReadAllText(view);
                if (!text.Contains("\"_PageSizeSelect\"")) continue;
                if (!text.Contains("type=\"submit\""))
                    violations.Add($"{ConventionScan.Rel(view)}: 掛了 _PageSizeSelect 卻沒有任何 submit 按鈕——查詢鈕可能被一起刪掉了");
            }

            Assert.True(paginated >= 80,
                $"R11 只掃到 {paginated} 支分頁畫面——掃描條件已失效，規則形同關閉");

            AssertRule("R11", violations);
        }

        // R12：Index.cshtml 中顯示 RequestedBy / CreatedBy 等 UserId 欄位時，
        //   必須透過 userNames 字典（ViewBag.UserNames）轉換為可讀使用者名稱；
        //   直接渲染 UserId 會對使用者顯示 GUID（mfg-domain §13 MachineStatusOrder 曾發生）。
        [Fact]
        public void R12_IndexViews_UserIdFields_UseDisplayNameLookup()
        {
            var userIdField = new Regex(
                @"\b(RequestedBy|CreatedBy)\b",
                RegexOptions.Compiled);
            var violations = new List<string>();
            foreach (var view in ConventionScan.Files("Modules", "Index.cshtml"))
            {
                var text = File.ReadAllText(view);
                if (!userIdField.IsMatch(text)) continue;       // 無 UserId 欄位，略過
                if (!text.Contains("userNames"))                // 無字典查詢 → 直接顯示 GUID
                    violations.Add(ConventionScan.Rel(view));
            }
            AssertRule("R12", violations);
        }

        // R13：所有 [HttpPost] action 本體必須含伺服端權限檢查（安全規則）。
        //   背景：2026-06 安全審查發現 BAS/PUR/ORD/INV 148 個寫入 POST 僅靠 ViewBag
        //   隱藏按鈕，任何已登入帳號可直接發 POST 繞過角色設定；修正後以本規則防回歸。
        //   判定：action 本體（brace-balanced）須命中權限 token——
        //     Forbid() / _permissionService.CanX / Permissions.CanX /
        //     CanExecuteAsync / RequireMobilePermissionAsync / JsonFail(No*Permission)。
        //   類別層級 [Authorize(Roles = ...)] 視為整個控制器已閘（Core 系統管理模式）；
        //   動作層級的 [Authorize(Roles = ...)]（緊貼在該 action 的屬性區）同理只閘那一支
        //   （2026-10-16 同步 Core #64 LicenseController：匯入授權等四支限 Admin）。
        //   登入/登出/切換廠別角色等自我服務 action 由 baseline 豁免（逐筆注明原因）。
        [Fact]
        public void R13_HttpPostActions_HavePermissionCheck()
        {
            var permToken = new Regex(
                @"Forbid\(\)|\.Can(Read|Create|Update|Delete|Print)\(|CanExecuteAsync|" +
                @"RequireMobilePermissionAsync|No(Create|Update|Delete)Permission|" +
                // 送/撤簽委派至 ModuleControllerBase 助手，權限檢查在助手內統一執行。
                @"SubmitForApprovalAsync|WithdrawApprovalAsync",
                RegexOptions.Compiled);
            var httpPost = new Regex(@"\[HttpPost[^\]]*\]", RegexOptions.Compiled);
            var sig = new Regex(
                @"public\s+(?:async\s+)?(?:Task<)?(?:IActionResult|ActionResult|JsonResult)>?\s+(\w+)\s*\(",
                RegexOptions.Compiled);

            var files = ConventionScan.Files("Modules", "*Controller.cs")
                .Concat(ConventionScan.Files("Controllers", "*Controller.cs"))
                .Concat(ConventionScan.Files("Api", "*Controller.cs"));

            var violations = new List<string>();
            foreach (var ctrl in files)
            {
                var text = File.ReadAllText(ctrl);

                // 類別層級角色閘（[Authorize(Roles = "Admin")] 等）→ 整個控制器視為已保護
                var classIdx = text.IndexOf(" class ", StringComparison.Ordinal);
                // [RequireActiveAdmin]（2026-11-12 取代 Roles = "Admin"，看作用中角色）同為整個控制器的閘。
                // 類別層級的 [RequireMenuPermission] 只閘「讀取」，不算寫入動作的閘——寫入動作要各自標註。
                if (classIdx > 0 && Regex.IsMatch(text[..classIdx], @"\[(?:Authorize\(Roles\s*=|RequireActiveAdmin\b)"))
                    continue;

                foreach (Match post in httpPost.Matches(text))
                {
                    var m = sig.Match(text, post.Index);
                    // 簽章須緊跟在屬性區之後（容許其他屬性，間隔上限 300 字元）
                    if (!m.Success || m.Index - post.Index > 300) continue;

                    // 動作層級角色閘：屬性區（[HttpPost] 到簽章之間）帶 [Authorize(Roles = ...)]
                    // 動作層級的 [RequireActiveAdmin]／[RequireMenuPermission(鍵, 動作)] 同理（見 Services/AdminAccess.cs）
                    if (Regex.IsMatch(text[post.Index..m.Index], @"\[(?:Authorize\(Roles\s*=|RequireActiveAdmin\b|RequireMenuPermission\()"))
                        continue;

                    var body = ExtractMethodBody(text, m.Index);
                    if (body == null) continue;
                    if (!permToken.IsMatch(body))
                        violations.Add($"{ConventionScan.Rel(ctrl)}: {m.Groups[1].Value}");
                }
            }
            AssertRule("R13", violations);
        }

        // R14：以 class 隱藏（u-display-none / d-none，皆帶 !important）的元素，
        //   不得用 style.display 切換顯示——清空/覆寫 inline style 蓋不掉 class，
        //   元素永久隱藏（CSP style-src 外部化 de550f5 的回歸樣態；使用者症狀＝
        //   「單位換算/儲位/週班表不見了」，2026-07-04 已修 11 處）。一律改 classList。
        //   配對範圍（控誤報）：
        //     ① .cshtml 以「同目錄」為界（面板 partial 定義 id、Index script 切換屬常態）；
        //     ② wwwroot 頁面以「同名 X.html ↔ X.page.js」配對；
        //   共用 js（crud-panel.js 等）無法靜態定位來源頁，不掃（接受的盲區）。
        [Fact]
        public void R14_ClassHiddenElements_NotToggledViaStyleDisplay()
        {
            var tagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
            var idAttr = new Regex(@"\bid=""([^""]+)""", RegexOptions.Compiled);
            // ⚠️ class 屬性可能包著 Razor 運算式，而運算式裡有自己的雙引號：
            //    class="mb-3 @(Model.IsApprovalRequired ? "" : "d-none")"
            //    舊版的 \bclass="([^"]*)" 會在運算式的第一個內層引號就截斷，
            //    擷到的是 `mb-3 @(Model.IsApprovalRequired ? `——d-none 不在裡面，
            //    於是整個元素被判成「沒有被 class 隱藏」。DocumentApprovalConfig 的
            //    templateDiv 就是這樣躲過 R14 五個月的（2026-09-18 修）。
            //    所以 @(...) 要當成一個不可分割的單元先吃掉（容許一層巢狀括號）。
            var classAttr = new Regex(
                @"\bclass=""((?:@\((?:[^()]|\([^()]*\))*\)|[^""])*)""", RegexOptions.Compiled);
            // 邊界要收錄引號：class 值裡的 Razor 三元式會把 class 名包在字串常值裡
            //（`? "" : "d-none"`），只認空白的話，剝完屬性仍然比對不到。
            var hiddenCls = new Regex(@"(?:^|[\s""'])(?:u-display-none|d-none)(?:[\s""']|$)",
                RegexOptions.Compiled);
            var toggle = new Regex(
                @"(?:getElementById\(\s*['""]([^'""]+)['""]\s*\)|querySelector\(\s*['""]#([^'""\s]+)['""]\s*\))\s*\.style\.display\s*=",
                RegexOptions.Compiled);
            // ⚠️ 第二種漏判：先把元素存進區域變數再切。
            //      const div = document.getElementById('templateDiv');
            //      if (div) div.style.display = required ? '' : 'none';
            //    上面那條 toggle 只認直接串接，這種寫法完全看不到。
            //    做法是先在同一個檔案裡收「變數 → id」的繫結，再找 `變數.style.display =`。
            var idBinding = new Regex(
                @"(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*document\s*\.\s*(?:getElementById\(\s*['""]([^'""]+)['""]\s*\)|querySelector\(\s*['""]#([^'""\s]+)['""]\s*\))",
                RegexOptions.Compiled);

            HashSet<string> HiddenIds(IEnumerable<string> markupFiles)
            {
                var ids = new HashSet<string>();
                foreach (var f in markupFiles)
                {
                    foreach (Match tag in tagRegex.Matches(File.ReadAllText(f)))
                    {
                        var id = idAttr.Match(tag.Value);
                        var cls = classAttr.Match(tag.Value);
                        if (id.Success && cls.Success &&
                            hiddenCls.IsMatch(cls.Groups[1].Value))
                            ids.Add(id.Groups[1].Value);
                    }
                }
                return ids;
            }

            // ⚠️ 掃描前必須先剝掉註解。修這類缺陷時，註解裡幾乎一定會**引用那個壞寫法**
            //    來說明為什麼不能那樣寫（Core #39 的 toggleTemplateDiv 就是這樣——
            //    程式已經改成 classList，註解裡留著一句
            //    「原本 div.style.display = '' 的版本，那一欄永遠打不開」，
            //    R14 於是指著一段正確的程式說它違規）。
            //    一條會被「解釋自己」的文字絆倒的規則，只會被 baseline 掉然後失效。
            //    ⚠️ `//` 前面是冒號的不剝（`https://…`），否則同一行後面的真違規會跟著消失。
            var stripComments = new Regex(@"/\*.*?\*/|(?<!:)//[^\n]*",
                RegexOptions.Compiled | RegexOptions.Singleline);

            void ScanToggles(string scriptFile, HashSet<string> hiddenIds,
                List<string> sink)
            {
                var text = stripComments.Replace(File.ReadAllText(scriptFile), "");

                foreach (Match m in toggle.Matches(text))
                {
                    var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (hiddenIds.Contains(id))
                        sink.Add($"{ConventionScan.Rel(scriptFile)}: {id}");
                }

                // 變數繫結那一路：同檔內 `const x = document.getElementById('id')`
                // 之後只要出現 `x.style.display =` 就算數。變數名重複繫結到不同 id
                // 的情況（很少見）一律當成全部都要看——寧可多報，也不要像舊版那樣整條漏掉。
                foreach (Match b in idBinding.Matches(text))
                {
                    var id = b.Groups[2].Success ? b.Groups[2].Value : b.Groups[3].Value;
                    if (!hiddenIds.Contains(id)) continue;

                    var varName = b.Groups[1].Value;
                    var viaVar = new Regex(
                        @"\b" + Regex.Escape(varName) + @"\s*\.\s*style\s*\.\s*display\s*=");
                    if (viaVar.IsMatch(text))
                        sink.Add($"{ConventionScan.Rel(scriptFile)}: {id}");
                }
            }

            var violations = new List<string>();

            // ① .cshtml：同目錄內 markup 定義 ↔ script 切換
            var cshtmlByDir = new[] { "Modules", "Views", "Areas", "Pages" }
                .SelectMany(d => ConventionScan.Files(d, "*.cshtml"))
                .GroupBy(Path.GetDirectoryName);
            foreach (var dir in cshtmlByDir)
            {
                var hidden = HiddenIds(dir);
                if (hidden.Count == 0) continue;
                foreach (var f in dir)
                    ScanToggles(f, hidden, violations);
            }

            // ② wwwroot：X.page.js ↔ 同名 X.html
            foreach (var js in ConventionScan.Files("wwwroot", "*.page.js"))
            {
                var html = js[..^".page.js".Length] + ".html";
                if (!File.Exists(html)) continue;
                var hidden = HiddenIds(new[] { html });
                if (hidden.Count > 0)
                    ScanToggles(js, hidden, violations);
            }

            AssertRule("R14", violations);
        }

        // R15：凡「送出簽核」的 DocumentType，都必須有已註冊的 IApprovalSyncContributor
        //   （或 Core 內建 fallback），否則核准後 ApprovalService.SyncSourceDocument 靜默什麼都不做，
        //   單據永遠停在 PendingApproval。實害樣態：委外收貨 Confirm 要求 Approved，
        //   一旦該部署替委外收貨設定簽核流程，整條「收貨→過帳→應付」就此死鎖；
        //   BAS 主檔（Product/Company/Tax）送審時 IsActive=false，核准後無人改回 → 主檔永不生效。
        //   本規則零容忍、不走 baseline：漏掛 contributor 沒有「既有違規」的正當性，
        //   ApprovalContributorsIntegrationTests 是手寫列舉 14 型別，擋不住新單據漂移。
        //   掃描範圍限 Modules/（顧問模組責任範圍）；Core 自身型別走 fallback 清單。
        [Fact]
        public void R15_SubmittedDocTypes_HaveApprovalSyncContributor()
        {
            var root = ConventionScan.RepoRoot();
            var moduleFiles = ConventionScan.Files("Modules", "*.cs").ToList();

            // ── ① 送審端：收集所有送出簽核的 DocumentType ───────────────────
            // 泛型委派式：SubmitForApprovalAsync<Entity>(id, MenuKey, DocType)
            var genericSubmit = new Regex(
                @"SubmitForApprovalAsync\s*<[^>]+>\s*\(\s*[^,]+,\s*[^,]+,\s*(?<t>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*\)",
                RegexOptions.Compiled);
            // 直呼服務：SubmitForApproval(docId, DocType, templateId, userId)
            var directSubmit = new Regex(
                @"SubmitForApproval\s*\(\s*[^,]+,\s*(?<t>""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*,",
                RegexOptions.Compiled | RegexOptions.Singleline);

            var submitted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in moduleFiles)
            {
                var text = File.ReadAllText(f);
                if (!text.Contains("SubmitForApproval")) continue;

                foreach (Match m in genericSubmit.Matches(text).Concat(directSubmit.Matches(text)))
                {
                    var token = m.Groups["t"].Value;
                    string? docType = token.StartsWith('"')
                        ? token.Trim('"')
                        // 識別字（多為 private const string DocType = "..."）→ 回同檔解析常值
                        : Regex.Match(text, @"\b" + Regex.Escape(token) + @"\s*=\s*""([^""]+)""")
                               is { Success: true } c ? c.Groups[1].Value : null;

                    if (string.IsNullOrEmpty(docType)) continue;
                    submitted.TryAdd(docType, ConventionScan.Rel(f));
                }
            }

            Assert.True(submitted.Count > 0,
                "R15: 掃不到任何送審呼叫——掃描器已失效（regex 或目錄結構變動），" +
                "此時測試恆綠等同無守門，必須修正掃描器。");

            // ── ② 回寫端：已「註冊到 DI」的 contributor 所宣告的 DocumentType ──
            // 只認註冊過的：宣告了 class 卻沒 AddScoped 等同沒有落點。
            var registered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in ConventionScan.Files("Modules", "*Module.cs"))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(f),
                    @"IApprovalSyncContributor\s*,\s*(?:[A-Za-z0-9_.]+\.)?([A-Za-z0-9_]+)\s*>"))
                    registered.Add(m.Groups[1].Value);
            }

            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in moduleFiles)
            {
                var text = File.ReadAllText(f);
                if (!text.Contains("IApprovalSyncContributor")) continue;

                // class XApprovalSync : IApprovalSyncContributor { public string DocumentType => "Y"; }
                foreach (Match cls in Regex.Matches(text,
                    @"class\s+([A-Za-z0-9_]+)\s*:\s*(?:[A-Za-z0-9_.]+\.)?IApprovalSyncContributor"))
                {
                    if (!registered.Contains(cls.Groups[1].Value)) continue;
                    var dt = Regex.Match(text[cls.Index..],
                        @"DocumentType\s*(?:=>|=)\s*""([^""]+)""");
                    if (dt.Success) covered.Add(dt.Groups[1].Value);
                }
            }

            // ── ③ Core 內建 fallback（ApprovalService.SyncSourceDocument 的 switch）──
            var svc = Path.Combine(root, "Services", "ApprovalService.cs");
            if (File.Exists(svc))
            {
                var text = File.ReadAllText(svc);
                var at = text.IndexOf("SyncSourceDocument(string documentId", StringComparison.Ordinal);
                if (at >= 0)
                {
                    var body = ExtractMethodBody(text, at) ?? string.Empty;
                    foreach (Match m in Regex.Matches(body, @"case\s+""([^""]+)""\s*:"))
                        covered.Add(m.Groups[1].Value);
                }
            }

            var violations = submitted
                .Where(kv => !covered.Contains(kv.Key))
                .Select(kv => $"{kv.Key}（送審於 {kv.Value}）：核准後無回寫落點，" +
                              "單據將永遠停在 PendingApproval")
                .ToList();

            Assert.True(violations.Count == 0,
                $"R15: {violations.Count} 個送審型別缺少簽核回寫落點：\n  " +
                string.Join("\n  ", violations) +
                "\n→ 於該模組新增 IApprovalSyncContributor 實作並在 {Module}.cs 註冊 AddScoped。");
        }

        // R16：`RemoveRange(x.明細集合)` 之後必須緊接 `x.明細集合.Clear()`。
        //   背景：這是實際發生過的重複過帳缺陷（見 ScrapDocController 該處註解與
        //   StockAdjustmentDuplicateLineReproTests）。EF 的 RemoveRange 只把舊行標記為
        //   Deleted，**不會從導覽屬性的記憶體集合移除**；單據 Save 的慣例又是「刪光明細
        //   再重建」，若不 Clear，接著 Add 的新行與殘留的舊行同時存在於 doc.Lines，
        //   過帳時被一併算入 → 沿用舊行號的行被重複過帳，庫存與帳務雙雙多算。
        //   壞法特別隱蔽：存檔看起來成功、單據畫面正常，只有庫存數字悄悄多扣。
        //   全庫有近 20 支 controller 是同一套寫法，靠複製貼上時記得帶走那行 Clear()
        //   顯然不可靠，故以規則守住。
        [Fact]
        public void R16_RemoveRangeOnLines_IsFollowedByClear()
        {
            // 只鎖「單據明細」這類導覽集合（Lines/Items/Details/LocationDetails），
            // 不鎖 RemoveRange(某個查詢結果) —— 那類本來就沒有記憶體集合殘留問題。
            var removeRange = new Regex(
                @"RemoveRange\(\s*(?<owner>\w+(?:\.\w+)*)\.(?<coll>Lines|Items|Details|LocationDetails)\s*\)",
                RegexOptions.Compiled);

            var files = ConventionScan.Files("Modules", "*Controller.cs")
                .Concat(ConventionScan.Files("Modules", "*Service.cs"))
                .Concat(ConventionScan.Files("Controllers", "*Controller.cs"));

            var violations = new List<string>();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (Match m in removeRange.Matches(text))
                {
                    var owner = m.Groups["owner"].Value;
                    var coll  = m.Groups["coll"].Value;

                    // 往後看到「本方法結束」為止，而不是固定字元窗口：PUR 的寫法是先跑
                    // 一整個 foreach 把新明細組好、最後才指派回去，中間隔了二十幾行；
                    // 用窗口會把它誤報成缺陷。以大括號深度判定方法邊界最準確。
                    var tail = TailWithinMethod(text, m.Index + m.Length);

                    // 兩種安全寫法都接受：
                    //   ① x.Lines.Clear()      —— 清空後往同一個集合 Add（INV/ORD/MFG 慣例）
                    //   ② x.Lines = 新集合      —— 整個換掉，舊行同樣不會殘留（PUR 慣例）
                    // 只認 Clear 會把 ② 誤報成缺陷，那會讓 baseline 凍結 3 筆假違規，
                    // 反而遮蔽該檔案日後的真違規。
                    var cleared  = tail.Contains($"{owner}.{coll}.Clear()", StringComparison.Ordinal);
                    var reseated = Regex.IsMatch(tail,
                        $@"\b{Regex.Escape(owner)}\.{coll}\s*=\s*(?!=)");

                    if (!cleared && !reseated)
                        violations.Add($"{ConventionScan.Rel(file)}: RemoveRange({owner}.{coll}) 之後"
                                       + $"既未 {owner}.{coll}.Clear() 也未重新指派集合");
                }
            }

            // 掃不到任何 RemoveRange＝掃描器失效（目錄結構或寫法變更），不能靜默通過。
            Assert.True(removeRange.IsMatch(string.Join('\n', files.Select(File.ReadAllText))),
                "R16: 全庫掃不到任何 RemoveRange(x.Lines) —— 掃描器已失效，請檢查 regex 與掃描目錄。");

            AssertRule("R16", violations);
        }

        // R17：`IDateTimeService.Now` 的值不得寫入實體的日期時間欄位。
        //   Now 回的是「已換算成使用者時區、Kind=Unspecified」的當地時間，而全庫的
        //   *At 欄位都是 PostgreSQL timestamptz —— Npgsql 對 Kind=Unspecified 直接拋
        //   ArgumentException，整個動作連同交易一起失敗。
        //   實際踩到過兩處：LandedCostService.ConfirmAsync（確認分攤單在 PG 上 100% 失敗）
        //   與 InspectionOrderController（檢驗結果存檔失敗）。兩處都逃過了單元測試，
        //   因為 InMemory 不檢查 Kind，而測試又把 IDateTimeService.Now 模擬成 Kind=Utc，
        //   等於把唯一會露餡的地方也一併掩蓋掉。
        //   語意上也只能是 UTC：同一批欄位的其他寫入點用的是 DateTime.UtcNow，
        //   查詢端（如 SpcAnalysisService）亦以 UTC 區間篩選，混入當地時間會讓資料錯位 8 小時。
        //   Now 的正當用途是「畫面顯示／報表表頭」，那類是區域變數不會進 DB，故不在此規則範圍。
        [Fact]
        public void R17_DateTimeServiceNow_IsNotPersistedToEntity()
        {
            var files = ConventionScan.Files("Modules", "*.cs")
                .Concat(ConventionScan.Files("Controllers", "*.cs"))
                .Concat(ConventionScan.Files("Services", "*.cs"))
                .Where(f => !f.Contains("DateTimeService.cs", StringComparison.Ordinal));

            // 該檔案裡哪些欄位／參數是 IDateTimeService（名稱不固定：_dt、_dateTimeService…）
            var fieldDecl = new Regex(@"IDateTimeService\s+(?<f>_?\w+)", RegexOptions.Compiled);

            var violations = new List<string>();
            foreach (var file in files)
            {
                var text   = File.ReadAllText(file);
                var fields = fieldDecl.Matches(text).Select(m => m.Groups["f"].Value).Distinct().ToList();
                if (fields.Count == 0) continue;

                foreach (var f in fields)
                {
                    var now = $@"{Regex.Escape(f)}\.Now\b";

                    // ① 直接寫進成員：order.InspectedAt = _dateTimeService.Now;
                    foreach (Match m in Regex.Matches(text, $@"(?<lhs>\b\w+\.\w+)\s*=\s*{now}"))
                    {
                        // ViewBag 是畫面顯示袋，不是實體——本規則開頭就寫明「畫面顯示／報表表頭」
                        // 不在範圍內，只是當初假設那類一定是區域變數，漏掉了 ViewBag.X = 這種寫法。
                        // 2026-09-14 起 R22(b) 反過來**要求**顯示用時間戳走 IDateTimeService.Now，
                        // 不排除的話兩條規則會互相打架（列印頁尾時間戳無論怎麼寫都有一條紅）。
                        if (IsViewBagLhs(m.Groups["lhs"].Value)) continue;
                        violations.Add($"{ConventionScan.Rel(file)}: {m.Groups["lhs"].Value} = {f}.Now");
                    }

                    // ② 先落地成區域變數再寫進成員：var now = _dt.Now; … alloc.UpdatedAt = now;
                    foreach (Match d in Regex.Matches(text, $@"\bvar\s+(?<v>\w+)\s*=\s*{now}\s*;"))
                    {
                        var v    = d.Groups["v"].Value;
                        var tail = TailWithinMethod(text, d.Index + d.Length);
                        foreach (Match a in Regex.Matches(tail, $@"(?<lhs>\b\w+\.\w+)\s*=\s*{Regex.Escape(v)}\s*[;,]"))
                        {
                            if (IsViewBagLhs(a.Groups["lhs"].Value)) continue;
                            violations.Add($"{ConventionScan.Rel(file)}: {a.Groups["lhs"].Value} = {v}（來自 {f}.Now）");
                        }
                    }
                }
            }

            AssertRule("R17", violations);
        }

        /// <summary>
        /// R17 的排除判準：賦值左邊是不是 ViewBag（畫面顯示袋，永遠不會進 DB）。
        /// 刻意只認 ViewBag，不放行任意「看起來不像實體」的名字——放寬到猜測就等於把規則關掉。
        /// ViewData["x"]／TempData["x"] 是索引器語法，本來就配不上 `\w+\.\w+`，不需另外處理。
        /// </summary>
        private static bool IsViewBagLhs(string lhs) =>
            lhs.StartsWith("ViewBag.", StringComparison.Ordinal);

        // R18：載入 `_CrudPanelScripts` 的頁面必須含 `id="successToast"` 元素。
        //   crud-panel.js 的 toastSuccess() 是 `new bootstrap.Toast(getElementById('successToast')).show()`，
        //   元素不存在時 Toast 建構子直接拋例外。而它被呼叫的位置是 submitPanel 成功分支的
        //   **第一行**：`toastSuccess(); refreshList();` —— 於是例外連帶讓 refreshList() 永遠跑不到。
        //   後果是資料已經存進資料庫，但面板不關、列表不刷新、什麼提示都沒有，
        //   例外只落在 submitPanel 的 .catch 裡變成一行 console.error。
        //   使用者看到的是「按了儲存沒反應」，於是再按一次 —— 撞唯一鍵才第一次看到錯誤訊息。
        //   2026-07-29 實測有 9 個畫面缺這塊 markup（VendorPriceList 由新增的 smoke test 揪出）。
        //   刪除與 postAction 走的也是同一支 toastSuccess，故不限於存檔。
        [Fact]
        public void R18_PagesUsingCrudPanel_DeclareSuccessToast()
        {
            var violations = new List<string>();
            foreach (var dir in new[] { "Modules", "Views" })
            {
                foreach (var file in ConventionScan.Files(dir, "*.cshtml"))
                {
                    // partial 自己（檔名就含這個字串）不算使用者
                    if (Path.GetFileNameWithoutExtension(file) == "_CrudPanelScripts") continue;

                    var text = File.ReadAllText(file);
                    if (!text.Contains("_CrudPanelScripts", StringComparison.Ordinal)) continue;
                    if (text.Contains("id=\"successToast\"", StringComparison.Ordinal)) continue;
                    violations.Add($"{ConventionScan.Rel(file)}: 載入 _CrudPanelScripts 但缺 id=\"successToast\"");
                }
            }

            // 掃描器自我檢查：一定要掃得到「有載入 _CrudPanelScripts」的頁面，
            // 否則路徑或副檔名寫錯時這條規則會靜默變成永遠綠。
            Assert.Contains(
                ConventionScan.Files("Modules", "*.cshtml"),
                f => File.ReadAllText(f).Contains("_CrudPanelScripts", StringComparison.Ordinal));

            AssertRule("R18", violations);
        }

        /// <summary>
        /// R19：controller 會加「整表層級」的 ModelState 錯誤時，對應的 _Form 必須有
        /// <c>asp-validation-summary="ModelOnly"</c>。
        ///
        /// <c>AddModelError("", …)</c> 的訊息不屬於任何欄位，所以 <c>asp-validation-for</c>
        /// 的 span 一個都接不到它——表單沒有 ModelOnly 摘要時，那些訊息**渲染不到任何地方**。
        /// 後果不是「訊息醜」而是**按了存檔沒反應**：面板原地重繪、資料沒進去、
        /// 畫面上沒有任何字說明為什麼。實測領料單就是這樣，六條守門訊息
        /// （非草稿、找不到單據、無權限、來源工單未核准…）全部隱形。
        ///
        /// 只掃「controller 真的會加整表錯誤」的表單：另外 34 支面板表單沒有摘要，
        /// 但它們的 controller 從不加整表錯誤，欄位級錯誤走 span 顯示正常，
        /// 一律要求摘要只會製造 34 筆沒有意義的違規。
        /// </summary>
        [Fact]
        public void R19_FormsWithModelLevelErrors_DeclareValidationSummary()
        {
            var violations = new List<string>();
            var probed = 0;

            foreach (var dir in new[] { "Modules", "Views" })
            {
                foreach (var file in ConventionScan.Files(dir, "*.cshtml"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith("_", StringComparison.Ordinal) ||
                        !name.Contains("Form", StringComparison.Ordinal)) continue;

                    var text = File.ReadAllText(file);
                    if (!text.Contains("method=\"post\"", StringComparison.Ordinal)) continue;

                    // 由 …/Views/<Ctrl>/… 推出 controller 檔；推不出來就跳過（不猜）
                    var ctrl = FindControllerForView(file);
                    if (ctrl == null) continue;

                    var ctrlText = File.ReadAllText(ctrl);
                    if (!ctrlText.Contains("AddModelError(\"\"", StringComparison.Ordinal) &&
                        !ctrlText.Contains("AddModelError(string.Empty", StringComparison.Ordinal)) continue;

                    probed++;
                    if (text.Contains("asp-validation-summary=\"ModelOnly\"", StringComparison.Ordinal)) continue;
                    violations.Add($"{ConventionScan.Rel(file)}: " +
                                   $"{Path.GetFileName(ctrl)} 會加整表 ModelState 錯誤，但表單缺 " +
                                   "asp-validation-summary=\"ModelOnly\"，訊息無處可顯示");
                }
            }

            // 掃描器自我檢查：必須真的探測到表單，否則路徑／命名規則一改，這條就靜默變成永遠綠
            Assert.True(probed > 0, "R19 沒有探測到任何表單——掃描條件已失效，規則形同關閉");

            AssertRule("R19", violations);
        }

        /// <summary>
        /// R20：Controller 不得以表單帶回的 <c>ApprovalStatus</c> 做狀態守衛判斷。
        ///
        /// 各 _Form 的簽核狀態是 hidden 欄位，而且刻意放在 <c>&lt;fieldset disabled&gt;</c>
        /// 外面（disabled 的欄位瀏覽器不送，放裡面會讓存檔少欄位），所以它是**使用者可改的**。
        /// 以它判斷「這張單能不能改」，等於把守衛的鑰匙交給被守的人：自行組一個 POST
        /// 把 PendingApproval 改成 Draft 就能改掉一張審核中的單，存檔後狀態又被
        /// <c>ResolveSaveStatusAsync</c> 寫回 PendingApproval，而簽核申請那筆記錄完全沒動
        /// ——簽核人核准的是他沒看過的內容，事後也看不出來改過。
        ///
        /// 正確作法是 <c>_context.LoadApprovalStatusAsync&lt;Entity&gt;(vm.XxxId)</c>（讀 DB）。
        /// 這條規則掃的是**讀取**：寫入（<c>vm.ApprovalStatus = …</c>，例如把解析後的
        /// 目標狀態回填給重繪的表單）是正當的，不算違規。
        ///
        /// 曾經同時有 13 支 controller 中招——因為它是「複製上一支 controller」傳開的，
        /// 而每一支單獨看都像是有守衛的。
        /// </summary>
        [Fact]
        public void R20_ControllerGuards_DoNotTrustPostedApprovalStatus()
        {
            var violations = new List<string>();
            var probed = 0;

            foreach (var dir in new[] { "Modules", "Controllers" })
            {
                foreach (var file in ConventionScan.Files(dir, "*Controller.cs"))
                {
                    var lines = File.ReadAllLines(file);
                    probed++;

                    for (var i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        var m = Regex.Match(line, @"\b(vm|model|form|dto)\.ApprovalStatus\b");
                        if (!m.Success) continue;

                        // 寫入不算：`vm.ApprovalStatus = …`（但 == / != 是讀取）
                        var after = line[(m.Index + m.Length)..].TrimStart();
                        if (after.StartsWith("=", StringComparison.Ordinal)
                            && !after.StartsWith("==", StringComparison.Ordinal)) continue;

                        violations.Add($"{ConventionScan.Rel(file)}:{i + 1}: " +
                                       $"守衛讀取表單帶回的 {m.Value}（使用者可改的 hidden 欄位）；" +
                                       "應改用 _context.LoadApprovalStatusAsync<Entity>(vm.XxxId) 讀資料庫");
                    }
                }
            }

            // 掃描器自我檢查：抓不到任何 controller 表示路徑條件失效，規則形同關閉
            Assert.True(probed > 0, "R20 沒有探測到任何 Controller——掃描條件已失效，規則形同關閉");

            AssertRule("R20", violations);
        }

        /// <summary>
        /// R21：mapped 的 decimal 欄位不得落在無界的 PostgreSQL <c>numeric</c>。
        ///
        /// 沒寫 <c>[Column(TypeName = "decimal(p,s)")]</c> 時，EF 產出的是無界 numeric。
        /// 那不是「更精確」，是**沒人決定過**：同一個語意的欄位在不同資料表存進不同精度，
        /// 對帳差異落在小數末位，看起來像四捨五入誤差而不像結構問題。
        /// 曾經 11 個 <c>ExchangeRate</c> 是 decimal(18,6)、只有兩個無界；
        /// 13 個 <c>TaxRate</c> 是 decimal(8,4)、只有 <c>Company.TaxRate</c> 無界。
        ///
        /// 掃 EF snapshot 而非原始碼：snapshot 只列**真的被 mapped 的欄位**，
        /// ViewModel／報表 DTO 的 decimal 屬性不會混進來，也就不需要維護排除清單。
        /// 各 scale 的適用範圍見 <c>DecimalPrecision</c> 的說明。
        /// </summary>
        [Fact]
        public void R21_MappedDecimalColumns_DeclareExplicitPrecision()
        {
            // 檢驗量測值：精度由量測儀器決定，不受 UOM 小數位數約束，維持無界是正確的。
            var allowedUnbounded = new HashSet<string>(StringComparer.Ordinal)
            {
                "InspectionItem.LowerSpec", "InspectionItem.UpperSpec",
                "InspectionItem.TargetValue", "InspectionItem.MeasuredValue",
                "InspectionItem.TargetStdDev",
                "InspectionSpecItem.LowerSpec", "InspectionSpecItem.UpperSpec",
                "InspectionSpecItem.TargetValue", "InspectionSpecItem.TargetStdDev",
            };

            var snapshot = Path.Combine(ConventionScan.RepoRoot(),
                "Migrations", "ApplicationDbContextModelSnapshot.cs");
            Assert.True(File.Exists(snapshot), $"R21 找不到 EF snapshot：{snapshot}");

            var text = File.ReadAllText(snapshot);
            var violations = new List<string>();
            var probed = 0;

            foreach (var block in text.Split("modelBuilder.Entity(\"").Skip(1))
            {
                var entity = block[..block.IndexOf('"', StringComparison.Ordinal)];
                var shortName = entity[(entity.LastIndexOf('.') + 1)..];

                foreach (Match m in Regex.Matches(block,
                    @"b\.Property<decimal\??>\(""(\w+)""\)(.*?)(?=b\.Property|b\.HasKey|b\.HasIndex|$)",
                    RegexOptions.Singleline))
                {
                    probed++;
                    var column = m.Groups[1].Value;
                    var type = Regex.Match(m.Groups[2].Value, @"HasColumnType\(""([^""]+)""\)");
                    if (type.Success && type.Groups[1].Value != "numeric") continue;

                    var key = $"{shortName}.{column}";
                    if (allowedUnbounded.Contains(key)) continue;

                    violations.Add($"{key}: 未宣告精度（產出無界 numeric）；" +
                                   "請依 DecimalPrecision 的分類加 [Column(TypeName = \"decimal(p,s)\")]");
                }
            }

            // 掃描器自我檢查：snapshot 格式一變就抓不到任何欄位，規則會靜默變成永遠綠
            Assert.True(probed > 0, "R21 沒有在 snapshot 探測到任何 decimal 欄位——掃描條件已失效，規則形同關閉");

            AssertRule("R21", violations);
        }

        // R22：業務日不得取自伺服器時鐘（`DateTime.Today` / `DateTime.UtcNow` 的日期部分），
        //   一律走 `IDateTimeService.GetLocalDate()`——它解析的是 `Plant.DefaultTimeZone`
        //   （`IUserContext.TimeZoneId` = ActivePlant.DefaultTimeZone ?? 使用者 tz）。
        //
        //   兩種寫法各自錯在不同地方：
        //     • `DateTime.Today` 取的是**伺服器**當地時區——容器幾乎都跑 UTC，於是
        //       UTC+8 廠別在當地 00:00–08:00 之間做的任何事都被記成前一天。
        //     • `DateOnly.FromDateTime(DateTime.UtcNow)` 取 UTC 日，同樣在當地凌晨錯一天，
        //       只是錯得比較「一致」而已。
        //
        //   為什麼是零容忍等級的規則而不是風格偏好：這個日期會一路帶進**單號採番的年月段**、
        //   **StockMovement.MovementDate**、**日記帳分錄日**。分錄日錯一天就可能落到
        //   已關帳期間——輕則撞期間鎖讓使用者莫名其妙存不了檔，重則靜默入到上個月的帳。
        //   實際踩到過：APInvoiceService 的 AP 退貨沖銷分錄用 UTC，而**同一支服務**的作廢
        //   路徑用 GetLocalDate()，同一張發票的兩個沖銷動作會落在不同日期。
        //
        //   單元測試抓不到這類缺陷：測試把 IDateTimeService 模擬成固定日期，而違規碼
        //   根本不呼叫它，斷言的又是「日期 == 那個固定值」——mock 從頭到尾沒被問過。
        //   故只能以原始碼掃描守門。
        //
        //   不在此規則範圍：`DateTime.UtcNow`（不取日期部分）本身是正確的稽核時戳寫法，
        //   `*At` 欄位就該存 UTC 瞬時（見 R17）。
        [Fact]
        public void R22_BusinessDate_NotFromServerClock()
        {
            var patterns = new (string Name, Regex Rx)[]
            {
                ("DateTime.Today",
                 new Regex(@"DateTime\.Today", RegexOptions.Compiled)),
                ("DateTime.UtcNow.Date",
                 new Regex(@"DateTime\.UtcNow\.Date", RegexOptions.Compiled)),
                ("DateOnly.FromDateTime(DateTime.UtcNow)",
                 new Regex(@"DateOnly\.FromDateTime\(\s*DateTime\.UtcNow\s*\)", RegexOptions.Compiled)),
                ("DateOnly.FromDateTime(DateTime.Now)",
                 new Regex(@"DateOnly\.FromDateTime\(\s*DateTime\.Now\s*\)", RegexOptions.Compiled)),
            };

            var files = ConventionScan.Files("Modules", "*.cs")
                .Concat(ConventionScan.Files("Controllers", "*.cs"))
                .Concat(ConventionScan.Files("Services", "*.cs"))
                // DateTimeService 自身就是「唯一允許讀伺服器時鐘」的地方
                .Where(f => !f.EndsWith("DateTimeService.cs", StringComparison.Ordinal));

            var violations = new List<string>();
            var probed = 0;

            foreach (var file in files)
            {
                // 去註解後再比對：本規則的說明本身就得把被禁的寫法寫出來，
                // 連註解一起掃會把「解釋為什麼不可以」的註解判成違規。
                var text = ConventionScan.StripComments(File.ReadAllText(file));
                foreach (var (name, rx) in patterns)
                {
                    probed++;
                    if (!rx.IsMatch(text)) continue;
                    // 以「檔案 + 違規寫法」為鍵：行號會隨無關編輯漂移，
                    // 但同一檔案內同一種寫法是穩定且足以定位的粒度。
                    violations.Add($"{ConventionScan.Rel(file)}: 業務日取自 {name}，應改用 IDateTimeService.GetLocalDate()");
                }
            }

            // 掃描器自我檢查：掃不到任何檔案代表目錄或副檔名寫錯，規則會靜默永遠綠。
            Assert.True(probed > 0, "R22 沒有掃到任何 .cs 檔——掃描條件已失效，規則形同關閉");

            AssertRule("R22", violations);

            // (b) 顯示用時間戳也不得讀伺服器時鐘。
            //     (a) 只掃業務日（Today / UtcNow.Date / FromDateTime），對「拿現在時刻直接秀給
            //     使用者看」的 DateTime.Now 是盲的——而開發與 CI 刻意跑 TZ=UTC
            //     （見 DEVELOPMENT.md），這種寫法會整整差一個時區，且畫面上看不出是哪一種。
            //     正解一律是 IDateTimeService.Now（依 ActivePlant.DefaultTimeZone ?? 個人時區）。
            var nowPatterns = new (string Name, Regex Rx)[]
            {
                ("DateTime.Now", new Regex(@"DateTime\.Now", RegexOptions.Compiled)),
                ("DateTimeOffset.Now", new Regex(@"DateTimeOffset\.Now", RegexOptions.Compiled)),
            };

            // Razor 註解（@* *@）與 HTML 註解要先拿掉，理由同 (a)：說明「為什麼不可以用 X」
            // 本來就得把 X 寫出來。但 .cshtml 不可套用 StripComments——它是 C# 語法的，
            // HTML 文字裡的單引號（don't）會被當成字元字面值起點，把後面連同真違規一起吃掉。
            var razorComment = new Regex(@"@\*.*?\*@|<!--.*?-->",
                RegexOptions.Compiled | RegexOptions.Singleline);

            var nowViolations = new List<string>();
            // 兩個來源分開計數：它們會獨立失效，只驗總數的話一邊掛了另一邊會把它掩護過去
            // （2026-09-11 R9(c) 的教訓：自我檢查門檻要對著每一個會獨立失效的判準）。
            var probedCs = 0;
            var probedRazor = 0;

            foreach (var file in files)
            {
                probedCs++;
                var text = ConventionScan.StripComments(File.ReadAllText(file));
                foreach (var (name, rx) in nowPatterns)
                {
                    if (!rx.IsMatch(text)) continue;
                    nowViolations.Add(
                        $"{ConventionScan.Rel(file)}: 顯示用時間戳取自 {name}，應改用 IDateTimeService.Now");
                }
            }

            var razorFiles = ConventionScan.Files("Modules", "*.cshtml")
                .Concat(ConventionScan.Files("Views", "*.cshtml"))
                .Concat(ConventionScan.Files("Pages", "*.cshtml"));

            foreach (var file in razorFiles)
            {
                probedRazor++;
                var text = razorComment.Replace(File.ReadAllText(file), "");
                foreach (var (name, rx) in nowPatterns)
                {
                    if (!rx.IsMatch(text)) continue;
                    nowViolations.Add(
                        $"{ConventionScan.Rel(file)}: 顯示用時間戳取自 {name}，應改用 IDateTimeService.Now");
                }
            }

            Assert.True(probedCs > 0, "R22(b) 沒有掃到任何 .cs 檔——掃描條件已失效，這一半形同關閉");
            Assert.True(probedRazor > 0, "R22(b) 沒有掃到任何 .cshtml 檔——掃描條件已失效，這一半形同關閉");

            AssertRule("R22b", nowViolations);
        }

        // R23：選單登錄畫面的 `Index` 必須有伺服端讀取閘門。
        //
        //   為什麼需要規則而不是靠 code review：專案**沒有全域權限 ActionFilter**，
        //   也沒有自訂授權 Attribute，`[Authorize]` 只保證「有登入」。因此「這個人能不能開
        //   這張清單」完全靠 action 自己寫那一行；漏了不會有任何徵兆——畫面正常顯示、
        //   測試全綠、選單也仍然依權限隱藏，只有「知道 URL 的人直接輸入網址」會穿過去。
        //
        //   誘因來自命名：`ModuleControllerBase.PrepareIndexPermissionsAsync(MenuKey)`
        //   名字像在守門，實際只準備 ViewBag.CanCreate/Update/Delete/Print。呼叫它會讓人
        //   以為權限已經處理完了。2026-08-03 全庫稽核發現 **30 支**選單畫面的 Index 因此
        //   完全沒有讀取閘門（含 SalesOrder／WorkOrder／Ncr／Schedule 等作業型畫面）。
        //
        //   單元測試同樣抓不到：`TestBase` 的權限 mock 當時 stub 了 CanCreate/Update/
        //   Delete/Print 卻獨漏 CanRead——測試端複製了與生產程式一模一樣的盲點。
        //
        //   母體＝`ProjectZMenuContributor` 登錄的畫面（權限以 MenuKey 為鍵，未登錄選單者
        //   沒有 MenuKey 可檢查）。沒有 Index action 的 controller 不在範圍內。
        //
        //   **判定的是「有沒有守衛」，不是「用哪個動詞」**：`CanRead` 或 `CanCreate` 當閘門
        //   都算通過。動詞該不該統一是**業務機密分級的決策**（見 report-permission-inventory.md），
        //   不是慣例規則能代為裁定的；規則若強制 `CanRead`，等於用一條 lint 悄悄放寬 61 張報表。
        //
        //   反過來，「有呼叫 `CanCreate`」也不等於有守衛——`ViewBag.CanCreate = await
        //   _permissionService.CanCreate(MenuKey);` 只是把旗標丟給 View 決定要不要畫按鈕。
        //   實際踩過：`ProductController.Index` 四行 ViewBag 賦值俱全，就是沒有那個 if。
        //   故比對的是**否定式權限呼叫**（`!await …CanX(…)`）或 `Forbid(`／`CallerIsAdminAsync`。
        //
        //   已知限制：以大括號配對取方法本體，字串字面值內的不成對 `{`／`}` 會使邊界失準；
        //   probed 自我檢查只保證規則沒有整體失效。
        [Fact]
        public void R23_MenuScreenIndex_HasReadPermissionGate()
        {
            var root = ConventionScan.RepoRoot();
            var contributor = Path.Combine(root, "Services", "ProjectZMenuContributor.cs");
            Assert.True(File.Exists(contributor),
                "R23 找不到 ProjectZMenuContributor.cs——掃描條件已失效，規則形同關閉");

            var screens = Regex.Matches(File.ReadAllText(contributor), @"Controller\s*=\s*""(\w+)""")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
            Assert.True(screens.Count > 0, "R23 沒有從選單解析到任何畫面——掃描條件已失效");

            // controller 可能拆成 partial：{Ctrl}Controller.cs 與 {Ctrl}Controller.Xxx.cs
            var allControllers = ConventionScan.Files("Modules", "*Controller*.cs")
                .Concat(ConventionScan.Files("Controllers", "*Controller*.cs"))
                .ToList();

            var violations = new List<string>();
            var probed = 0;

            foreach (var ctrl in screens)
            {
                var files = allControllers.Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return name == ctrl + "Controller.cs"
                        || name.StartsWith(ctrl + "Controller.", StringComparison.Ordinal);
                }).ToList();
                if (files.Count == 0) continue;   // 選單指向不存在的 controller：不歸本規則管

                string? body = null, owner = null;
                foreach (var file in files)
                {
                    body = IndexMethodBody(ConventionScan.StripComments(File.ReadAllText(file)));
                    if (body != null) { owner = file; break; }
                }
                if (body == null) continue;       // 該 controller 沒有 Index action

                probed++;
                var guarded = NegatedPermissionCall.IsMatch(body)
                           || body.Contains("Forbid(", StringComparison.Ordinal)
                           || body.Contains("CallerIsAdminAsync", StringComparison.Ordinal);
                if (!guarded)
                    violations.Add($"{ConventionScan.Rel(owner!)}: Index 缺少讀取閘門" +
                                   "（須 if (!await …CanRead(MenuKey)) return Forbid(); —— " +
                                   "ViewBag.CanX 賦值與 PrepareIndexPermissionsAsync 都只餵旗標給 View，不擋人）");
            }

            Assert.True(probed > 0, "R23 沒有解析到任何 Index action——掃描條件已失效，規則形同關閉");

            AssertRule("R23", violations);
        }

        // R24：`/api/…` 端點的每個 action 必須有權限閘門，或明寫 [PermissionGateExempt] 說明為何不用。
        //
        //   R23 只管 MVC 畫面的 Index。但畫面上的資料多半是前端 fetch 回來的，**JSON 端點
        //   才是資料真正離開伺服器的地方**——只擋畫面等於鎖了門卻開著窗：直接對
        //   `/api/warehouse/stock/{品號}` 下 curl（帶著任何一個合法登入 cookie）照樣拿得到
        //   全廠可用量，`/api/warehouse/product-detail` 甚至直接吐 StandardCost。
        //   2026-08-03 稽核 59 支 `/api/…` action：只有 18 支有閘門，25 支補上真閘門、
        //   9 支判定為豁免（純查表／只讀自己的資料）、3 支 [AllowAnonymous] 本來就正確、
        //   4 支屬 Core 檔案進 baseline。
        //
        //   兩種閘門都算通過：桌面 `IPermissionService`（MenuKey × 動詞）與手機
        //   `IMobilePermissionService`（模組 × 作業鍵）。系統有兩套權限模型，規則不該逼誰改用另一套。
        //
        //   **共用端點是這條規則最容易做錯的地方**：許多查詢 API 同時被多張畫面使用
        //   （`/api/warehouse/stock` 有 9 張 INV 單據 _Form 在呼叫，`/api/mobile/dispatches`
        //   有 4 個手機頁在呼叫）。鎖成單一 MenuKey／單一 actionKey 會讓「有 A 沒有 B」的角色
        //   **靜默壞掉**——前端收到 403 通常只是欄位空白、清單沒資料，不會有錯誤訊息，
        //   而開發與測試都用全權限帳號，永遠重現不出來。故提供 `CanReadAny(…)` 與
        //   `RequireMobileModuleAsync(…)`，把合法呼叫端列成白名單。
        //
        //   豁免必須用 `[PermissionGateExempt("理由")]` 寫在該 action 上，不放 baseline：
        //   豁免是**永久設計決定**（純查表、只讀自己的資料），該跟著程式走、改端點時看得見；
        //   baseline 是「既有待修」，語意不同。baseline 目前只放 Core 檔案（見 baseline-R24.txt）。
        //
        //   已知限制：以文字掃描判定，`[Http…]` 後找不到 `public …(` 簽章的 action 會被跳過；
        //   probed 自我檢查只保證規則沒有整體失效。
        [Fact]
        public void R24_ApiEndpoints_HavePermissionGate()
        {
            var files = ConventionScan.Files("Api", "*.cs")
                .Concat(ConventionScan.Files("Modules", "*Controller*.cs"))
                .Concat(ConventionScan.Files("Controllers", "*Controller*.cs"))
                .Distinct()
                .ToList();

            var violations = new List<string>();
            var probed = 0;

            var texts = files.ToDictionary(f => f, f => ConventionScan.StripComments(File.ReadAllText(f)));

            // 類別層 [Route("api/…")] 只寫在其中一個 partial 檔上（如 WarehouseApiController.cs），
            // 另一個 partial（…Controller.Fulfillment.cs）看不到它。若逐檔判定，那些 action
            // 會整批被跳過而不是被檢查——規則靜默漏掉一整個檔案。故先收集「屬於 api 類別的檔名前綴」。
            var apiClasses = texts
                .Where(kv => kv.Value.Contains("[Route(\"api/", StringComparison.Ordinal)
                          || kv.Value.Contains("[Route(\"~/api/", StringComparison.Ordinal))
                .Select(kv => ControllerBaseName(kv.Key))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var file in files)
            {
                var text = texts[file];
                // 類別層 [Route("api/…")]（含 partial 手足）；沒有的話仍要看 action 層是否自帶絕對路徑 "/api/…"
                var classIsApi = apiClasses.Contains(ControllerBaseName(file));

                foreach (Match http in HttpMethodAttribute.Matches(text))
                {
                    var routeArg = http.Groups["route"].Success ? http.Groups["route"].Value : null;
                    var isApiEndpoint = classIsApi
                        || (routeArg != null && routeArg.TrimStart('~').StartsWith("/api/", StringComparison.Ordinal));
                    if (!isApiEndpoint) continue;

                    // [Http…] 之後到方法本體結束為止＝屬性群 ＋ 本體；豁免屬性寫在中間也吃得到
                    var sig = Regex.Match(text[http.Index..], @"\bpublic\s+[^;{}]*?\b(\w+)\s*\(");
                    if (!sig.Success) continue;
                    var body = ExtractMethodBody(text, http.Index + sig.Index + sig.Length);
                    if (body == null) continue;

                    probed++;
                    var segment = text[http.Index..(http.Index + sig.Index)] + body;
                    var guarded = NegatedPermissionCall.IsMatch(segment)
                               || segment.Contains("Forbid(", StringComparison.Ordinal)
                               || segment.Contains("RequireMobilePermissionAsync(", StringComparison.Ordinal)
                               || segment.Contains("RequireMobileModuleAsync(", StringComparison.Ordinal)
                               || segment.Contains("CallerIsAdminAsync", StringComparison.Ordinal)
                               || segment.Contains("PermissionGateExempt", StringComparison.Ordinal)
                               || segment.Contains("AllowAnonymous", StringComparison.Ordinal);
                    if (!guarded)
                        violations.Add($"{ConventionScan.Rel(file)}::{sig.Groups[1].Value} " +
                                       "缺少權限閘門（補 if (!await …Can…) return Forbid(); / " +
                                       "RequireMobilePermissionAsync；確定不需要則加 " +
                                       "[PermissionGateExempt(\"理由\")]）");
                }
            }

            Assert.True(probed > 0, "R24 沒有掃到任何 /api/… action——掃描條件已失效，規則形同關閉");

            AssertRule("R24", violations);
        }

        // R25：非 `/api/` 的「會吐資料」GET action 也必須有權限閘門。
        //
        //   R23 管畫面 `Index`、R24 管 `/api/…`，中間還漏著最大的一塊：MVC controller 自己的
        //   `Details`／`Edit`(GET)／`Create`(GET)／`AvailableLines`／`GetOpenInvoices`…
        //   這些同樣是「直接輸入網址就拿得到資料」的入口，而且量最大。
        //   2026-08-03 稽核 459 支：**112 支無閘門**——36 支 `Details`（`/SalesOrder/Details/{單號}`
        //   直接吐整張訂單含單價金額）、34 支 Create/Edit GET、37 支 JSON 查詢、5 支 Core。
        //
        //   有一個明確的規律值得記著：**漏的全是讀取**。`File(...)` 回傳（Excel／PDF 匯出）
        //   一支都沒漏，全都有 `CanPrint`；寫入端也早有 R13。這個專案對「寫入」與「出境」
        //   很有紀律，唯獨「查詢不算危險動作」的直覺讓讀取端整片裸奔。
        //
        //   `Details` 不能單純用 `CanRead(MenuKey)`：`Views/Approval/_DocDetailsUrls.cshtml`
        //   把「待我簽核」的單號點擊導向各模組 `Details`（25 條映射），簽核者未必持有該模組
        //   的 MenuKey。故 `ModuleControllerBase.CanReadDocumentAsync` 以**單筆授權**旁路——
        //   只放行「這一張單」的關係人，不會讓他看到同模組的其他單據。
        //
        //   母體刻意排除 `/api/…`（歸 R24）與寫入動詞（歸 R13），避免同一個缺陷被三條規則重複報。
        //
        //   已知限制：以「本體是否出現 Json(/PartialView(/View(/File(」判定「會吐資料」，
        //   純導向（RedirectToAction）或純 NotFound 的 action 不在母體；
        //   probed 自我檢查只保證規則沒有整體失效。
        [Fact]
        public void R25_DataReturningGetActions_HavePermissionGate()
        {
            var files = ConventionScan.Files("Modules", "*Controller*.cs")
                .Concat(ConventionScan.Files("Controllers", "*Controller*.cs"))
                .Distinct()
                .ToList();

            var texts = files.ToDictionary(f => f, f => ConventionScan.StripComments(File.ReadAllText(f)));
            var apiClasses = texts
                .Where(kv => kv.Value.Contains("[Route(\"api/", StringComparison.Ordinal)
                          || kv.Value.Contains("[Route(\"~/api/", StringComparison.Ordinal))
                .Select(kv => ControllerBaseName(kv.Key))
                .ToHashSet(StringComparer.Ordinal);

            var violations = new List<string>();
            var probed = 0;

            foreach (var file in files)
            {
                var text = texts[file];
                if (apiClasses.Contains(ControllerBaseName(file))) continue;   // 歸 R24
                // 類別級 [Authorize(Roles = …)]＝整個 controller 已限角色（如 Admin 專用畫面）
                var classRoleGated = ClassAuthorizeRoles.IsMatch(text);

                foreach (Match sig in ActionSignature.Matches(text))
                {
                    var attrs = ActionAttributes(text, sig.Index);
                    if (WriteVerbAttribute.IsMatch(attrs)) continue;           // 歸 R13
                    if (attrs.Contains("/api/", StringComparison.Ordinal)) continue;

                    var close = MatchingParen(text, sig.Index + sig.Length - 1);
                    if (close < 0) continue;
                    var body = ExtractMethodBody(text, close + 1);
                    if (body == null) continue;

                    var returnsData = body.Contains("Json(", StringComparison.Ordinal)
                                   || body.Contains("PartialView(", StringComparison.Ordinal)
                                   || body.Contains("return View(", StringComparison.Ordinal)
                                   || body.Contains("File(", StringComparison.Ordinal);
                    if (!returnsData) continue;

                    probed++;
                    var segment = attrs + body;
                    var guarded = classRoleGated
                               || NegatedPermissionCall.IsMatch(segment)
                               || segment.Contains("Forbid(", StringComparison.Ordinal)
                               || segment.Contains("CanReadDocumentAsync(", StringComparison.Ordinal)
                               || segment.Contains("CallerIsAdminAsync", StringComparison.Ordinal)
                               || segment.Contains("PermissionGateExempt", StringComparison.Ordinal)
                               || segment.Contains("AllowAnonymous", StringComparison.Ordinal);
                    if (!guarded)
                        violations.Add($"{ConventionScan.Rel(file)}::{sig.Groups[1].Value} " +
                                       "缺少讀取閘門（補 if (!await …Can…) return Forbid();，" +
                                       "單據 Details 用 CanReadDocumentAsync，" +
                                       "共用查詢用 CanReadAny；確定不需要則加 [PermissionGateExempt(\"理由\")]）");
                }
            }

            Assert.True(probed > 0, "R25 沒有掃到任何會吐資料的 action——掃描條件已失效，規則形同關閉");

            AssertRule("R25", violations);
        }

        // R26：GET action 不得寫入資料庫或變更帳號狀態。
        //
        //   R13 要求寫入 POST 有權限閘門，但沒有人擋「寫入寫在 GET 裡」。這兩件事不同：
        //   有閘門的 GET 仍然可以被**跨站誘導**觸發——受害者自己有權限，攻擊者只需要讓
        //   他的瀏覽器發出那個 GET（`<img src="…">`、一個縮網址、論壇貼圖都行），
        //   AntiForgery token 對 GET 不生效，CORS 也擋不住 img 送出請求。
        //
        //   真實案例（2026-08-03 全庫稽核，Core PR #16 修正）：
        //   `TwoFactor/RegenerateRecoveryCodes` 是 GET，而它會**作廢**使用者既有的 2FA 復原碼。
        //   誘導點一下，受害者手抄保存的復原碼全部失效且畫面毫無徵兆；等到 2FA 裝置遺失
        //   要用復原碼時，帳號等同鎖死。已改為 POST + AntiForgery。
        //   全庫掃描的結論是**它是唯一一支**——所以 baseline 從零開始，之後有就是新的。
        //
        //   判定：非寫入動詞（無 [HttpPost/Put/Patch/Delete]）的 action 本體若出現
        //   SaveChanges／DbSet 的 Add|Remove|Update／ExecuteUpdate|Delete／UserManager 的
        //   狀態變更方法即違規。修法一律是「改成 POST + [ValidateAntiForgeryToken]」，
        //   不是加閘門——閘門擋的是「誰能做」，這裡的問題是「他被騙著做」。
        //
        //   已知限制：只認直接寫在 action 本體裡的寫入。委派給 Service 再由 Service
        //   SaveChanges 的情形掃不到（那是 R13 的守備範圍，且服務層本來就會被 POST 共用）。
        [Fact]
        public void R26_GetActions_DoNotMutateState()
        {
            var files = ConventionScan.Files("Modules", "*Controller*.cs")
                .Concat(ConventionScan.Files("Controllers", "*Controller*.cs"))
                .Concat(ConventionScan.Files("Api", "*Controller*.cs"))
                .Distinct()
                .ToList();

            var violations = new List<string>();
            var probed = 0;

            foreach (var file in files)
            {
                var text = ConventionScan.StripComments(File.ReadAllText(file));

                foreach (Match sig in ActionSignature.Matches(text))
                {
                    if (WriteVerbAttribute.IsMatch(ActionAttributes(text, sig.Index))) continue;

                    var close = MatchingParen(text, sig.Index + sig.Length - 1);
                    if (close < 0) continue;
                    var body = ExtractMethodBody(text, close + 1);
                    if (body == null) continue;

                    probed++;
                    var mutation = StateMutation.Match(body);
                    if (mutation.Success)
                        violations.Add($"{ConventionScan.Rel(file)}::{sig.Groups[1].Value} " +
                                       $"是 GET 卻會改狀態（{mutation.Value.Trim()}）——" +
                                       "改為 [HttpPost] + [ValidateAntiForgeryToken]，" +
                                       "呼叫端的 <a> 改成表單按鈕");
                }
            }

            Assert.True(probed > 0, "R26 沒有掃到任何 GET action——掃描條件已失效，規則形同關閉");

            AssertRule("R26", violations);
        }

        private static readonly Regex StateMutation = new(
            @"SaveChangesAsync\(|SaveChanges\(|" +
            @"(?:_context|_db|Db|context)\.[A-Za-z]\w*\.(?:Add|AddAsync|AddRange|AddRangeAsync|" +
            @"Remove|RemoveRange|Update|UpdateRange)\(|" +
            @"ExecuteUpdateAsync\(|ExecuteDeleteAsync\(|" +
            @"_userManager\.(?:SetTwoFactorEnabled|GenerateNewTwoFactorRecoveryCodes|" +
            @"ResetAuthenticatorKey|UpdateAsync|CreateAsync|DeleteAsync|AddToRole|RemoveFromRole|" +
            @"ResetPassword|ChangePassword|SetLockout)",
            RegexOptions.Compiled);

        /// <summary>
        /// 取 action 簽章前的屬性區：自簽章往回吃掉「空白 ＋ 完整的 <c>[ … ]</c> 區塊」。
        /// <para>不可改用「往回找最近的 <c>}</c>／<c>;</c>」——route template 內含大括號
        /// （<c>[HttpPost("dispatches/{id}/setup/start")]</c>）會把屬性區從中截斷，
        /// <c>[HttpPost]</c> 因而消失、POST 被當成 GET 檢查。</para>
        /// </summary>
        private static string ActionAttributes(string text, int sigIndex)
        {
            var start = sigIndex;
            while (true)
            {
                var j = start - 1;
                while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
                if (j < 0 || text[j] != ']') break;

                var depth = 0;
                var k = j;
                for (; k >= 0; k--)
                {
                    if (text[k] == ']') depth++;
                    else if (text[k] == '[' && --depth == 0) break;
                }
                if (k < 0) break;
                start = k;
            }
            return text[start..sigIndex];
        }

        // 排除 `public record X(…)` 這類**位置參數型別宣告**：它長得和方法簽章一模一樣，
        // 但後面沒有本體，取本體時會抓到檔案裡下一個方法的內容而張冠李戴
        // （MobileMfgController 檔頭 12 個 request record 全被誤判成會寫入的 GET）。
        private static readonly Regex ActionSignature = new(
            @"public\s+(?!record\b|class\b|struct\b|enum\b|interface\b|delegate\b)" +
            @"(?:async\s+)?(?:virtual\s+)?[\w<>,\.\?\[\]]+\s+(\w+)\s*\(", RegexOptions.Compiled);

        private static readonly Regex WriteVerbAttribute = new(
            @"\[Http(?:Post|Put|Patch|Delete)", RegexOptions.Compiled);

        // 類別級的讀取閘：[Authorize(Roles = …)]、[RequireActiveAdmin]、[RequireMenuPermission(鍵)]（見 Services/AdminAccess.cs）
        private static readonly Regex ClassAuthorizeRoles = new(
            @"\[(?:Authorize\s*\(\s*Roles|RequireActiveAdmin\b|RequireMenuPermission\s*\()", RegexOptions.Compiled);

        /// <summary>自 <paramref name="openIdx"/> 的 '(' 找到配對的 ')'；找不到回 −1。</summary>
        private static int MatchingParen(string text, int openIdx)
        {
            var depth = 0;
            for (var i = openIdx; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// 取 partial 手足共用的類別檔名（<c>X.Fulfillment.cs</c> → <c>X</c>），
        /// 用來把「類別層屬性寫在另一個 partial 檔」的情形接回來。
        /// </summary>
        private static string ControllerBaseName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);   // 已去掉最後的 .cs
            var dot = name.IndexOf('.');
            return dot < 0 ? name : name[..dot];
        }

        /// <summary>HTTP 動詞屬性；有路由參數時擷取之（判斷 action 層的絕對路徑 "/api/…"）。</summary>
        private static readonly Regex HttpMethodAttribute = new(
            @"\[Http(?:Get|Post|Put|Patch|Delete)(?:\s*\(\s*""(?<route>[^""]*)""[^)]*\))?\s*\]",
            RegexOptions.Compiled);

        /// <summary>否定式權限呼叫（<c>!await …CanX(…)</c>）＝ R23／R24 認定的守衛樣式。</summary>
        private static readonly Regex NegatedPermissionCall =
            new(@"!\s*await\s+[\w\.]*Can\w+\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// 取出 <c>Index</c> action 的方法本體（大括號配對）；沒有 Index action 時回 null。
        /// 刻意不用「起點起算固定字元數」的窗口——那會把**下一個 action** 的權限檢查
        /// 誤算成 Index 的（實際踩過：`ScheduleController.Index` 無閘門卻被判成有 `CanUpdate`）。
        /// </summary>
        private static string? IndexMethodBody(string text)
        {
            var m = Regex.Match(text,
                @"public\s+(?:async\s+)?(?:Task<)?IActionResult>?\s+Index\s*\(");
            if (!m.Success) return null;

            var open = text.IndexOf('{', m.Index + m.Length);
            if (open < 0) return null;

            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text[open..i];
            }
            return null;
        }

        /// <summary>由 View 路徑 …/Views/{Ctrl}/… 找出對應的 {Ctrl}Controller.cs；找不到回 null。</summary>
        private static string? FindControllerForView(string viewPath)
        {
            var parts = viewPath.Replace('\\', '/').Split('/');
            var idx = Array.LastIndexOf(parts, "Views");
            if (idx < 0 || idx + 1 >= parts.Length) return null;
            var ctrlName = parts[idx + 1] + "Controller.cs";

            foreach (var dir in new[] { "Modules", "Controllers" })
                foreach (var f in ConventionScan.Files(dir, ctrlName))
                    return f;
            return null;
        }

        /// <summary>
        /// 自 <paramref name="fromIdx"/> 取到所在方法結束為止的文字（不含方法外的內容）。
        /// 以大括號深度判定：遇到把深度帶到 −1 的 '}' 即為本方法的結尾。
        /// </summary>
        private static string TailWithinMethod(string text, int fromIdx)
        {
            var depth = 0;
            for (var i = fromIdx; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    if (--depth < 0) return text[fromIdx..i];
                }
            }
            return text[fromIdx..];
        }

        /// <summary>取出方法本體（含外層大括號，brace-balanced）。</summary>
        private static string? ExtractMethodBody(string text, int fromIdx)
        {
            var open = text.IndexOf('{', fromIdx);
            // 運算式本體（=> ...;）：箭頭出現在區塊括號之前，取箭頭到分號為止，
            // 避免誤抓到下一個區塊方法的本體（否則委派式 Submit/Withdraw 會被誤判）。
            var arrow = text.IndexOf("=>", fromIdx, System.StringComparison.Ordinal);
            if (arrow >= 0 && (open < 0 || arrow < open))
            {
                var semi = text.IndexOf(';', arrow);
                return semi < 0 ? text[arrow..] : text[arrow..(semi + 1)];
            }
            if (open < 0) return null;
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text[open..(i + 1)];
            }
            return null;
        }

        // R27：zh-TW resx 的值不得出現「搜尋」——查詢列的動作一律叫「查詢」。
        //
        //   這條規則守的是一個**裁決**，不是一個 bug。2026-08-12 之前，同一個 resx 鍵
        //   `Search` 有兩種中譯：72 支檔案譯「查詢」、58 支譯「搜尋」，另有 20 支用
        //   `SearchLabel` 鍵譯「搜尋」，12 支檔案內部兩種譯法並存；使用手冊則有 116 頁
        //   內文在教「點『查詢』」。英文兩者都是 Search，所以只有中文使用者看得到這個分裂。
        //
        //   量測過的事實：這**不影響**手冊 Q&A 的語意檢索（`snowflake-arctic-embed2`
        //   是多語模型，兩詞在它的空間裡本來就近，top-1／top-3 集合完全相同）。
        //   所以統一的理由是產品一致性，不是「不改會壞掉」——也因此它需要一條守門，
        //   否則下一個人新增畫面時憑手感寫「搜尋」，分裂會慢慢長回來，而且不會有任何徵兆。
        //
        //   只掃 zh-TW：en-US 兩者都是 Search，沒有這個問題。
        [Fact]
        public void R27_ZhResx_UsesQueryWordConsistently()
        {
            var files = ConventionScan.Files("Resources", "*.zh-TW.resx").ToList();
            Assert.True(files.Count > 100,
                $"R27 只掃到 {files.Count} 個 zh-TW resx——掃描條件已失效，規則形同關閉");

            var violations = new List<string>();

            foreach (var file in files)
            {
                var doc = System.Xml.Linq.XDocument.Load(file);
                foreach (var data in doc.Root!.Elements("data"))
                {
                    var key = (string?)data.Attribute("name");
                    var value = data.Element("value")?.Value;
                    if (key == null || value == null || !value.Contains("搜尋")) continue;

                    violations.Add($"{ConventionScan.Rel(file)}::{key} = 「{value}」——" +
                                   "查詢列動作的中譯一律用「查詢」（2026-08-12 裁決），請改字");
                }
            }

            AssertRule("R27", violations);
        }

        // R28：使用手冊教使用者「點『X』」時，X 必須真的還是畫面上的字。
        //
        //   這條補的是既有缺口 G2（`llm-qa-and-report-tools.md` §17.9）：
        //   `HelpDocResolverTests` 只斷言「已登錄畫面必有實體手冊頁」——抓得到「缺頁」，
        //   抓不到「頁在但內容過期」。今天過期手冊只是沒人看；接上手冊 Q&A 之後，
        //   它會被**用很有自信的語氣講出來**，而且沒有任何訊號。
        //
        //   判定刻意收得很窄：只看 `點「X」`／`按「X」`／`按下「X」` 這個句型。
        //   手冊裡的引號有 707 處，其中大部分是敘述用語（「還沒展開的量」這種），
        //   全部拿來比對會產生 188 筆雜訊，把真正的漂移淹掉。收窄到點擊句型之後
        //   是 262 命中／8 未命中——訊號夠強，才值得當守門。
        //
        //   詞彙表＝所有 zh-TW resx 的值 ∪ 手機字典 `wwwroot/mobile/js/i18n.js` 的中文值。
        //   手機那半必須加，否則 mobile/ 底下的手冊頁會整批誤報（它們的標籤不在 resx）。
        //
        //   修法有兩種，取決於哪一邊才是對的：畫面改了名 → 改手冊；手冊寫的才對 → 改畫面。
        //   兩者都不對時（例如手冊把兩個按鈕寫成「開始備料 / 備料完成」）才進 baseline。
        [Fact]
        public void R28_ManualClickTargets_StillExistInUi()
        {
            var vocabulary = ManualUiVocabulary();
            Assert.True(vocabulary.Count > 1000,
                $"R28 詞彙表只收集到 {vocabulary.Count} 筆——來源已失效，規則形同關閉");

            var violations = new List<string>();
            var probed = 0;

            foreach (var file in ConventionScan.Files("wwwroot/docs/user", "*.html"))
            {
                var rel = ConventionScan.Rel(file);
                // /en/ 是英文版（點的是 Search 不是查詢）、/tutor/ 是教學系統資料，都不在範圍。
                if (rel.Contains("/en/") || rel.Contains("/tutor/")) continue;

                foreach (Match m in ManualClickTarget.Matches(File.ReadAllText(file)))
                {
                    probed++;
                    var label = m.Groups[1].Value.Trim();
                    if (vocabulary.Contains(label)) continue;

                    violations.Add($"{rel}：手冊寫「點『{label}』」，但畫面上已經沒有這個字——" +
                                   "畫面改名就改手冊，手冊才對就改畫面");
                }
            }

            Assert.True(probed > 0, "R28 沒有掃到任何點擊指示——句型或手冊結構已變，規則形同關閉");

            AssertRule("R28", violations);
        }

        private static readonly Regex ManualClickTarget =
            new(@"(?:點|按下?)「([^」]{1,12})」", RegexOptions.Compiled);

        /// <summary>畫面上實際會出現的中文字：zh-TW resx 的值 ∪ 手機字典的中文值。</summary>
        private static HashSet<string> ManualUiVocabulary()
        {
            var vocabulary = new HashSet<string>(StringComparer.Ordinal);

            foreach (var resx in ConventionScan.Files("Resources", "*.zh-TW.resx"))
            {
                System.Xml.Linq.XDocument doc;
                try { doc = System.Xml.Linq.XDocument.Load(resx); }
                catch (System.Xml.XmlException) { continue; }

                foreach (var value in doc.Root!.Elements("data").Select(d => d.Element("value")?.Value))
                    if (!string.IsNullOrWhiteSpace(value)) vocabulary.Add(value.Trim());
            }

            // 第三個來源：View 裡直接寫死的雙語三元式（`isEn ? "Reset" : "重置"`）。
            //   有些畫面沒有走 resx——`Views/TwoFactorAdmin/Index.cshtml` 整頁都是這種寫法——
            //   而使用者看到的就是這些字。不收它，手冊寫「點『重置』」會被判成漂移，
            //   但手冊沒錯、畫面也沒錯，**錯的是詞彙表少了一半畫面**。
            //   （2026-09-11 加入。在此之前這類頁面的手冊只能靠 baseline 放行，
            //    而 baseline 是給「兩邊都不對」用的，拿來擋規則自己的盲點會把盲點永久化。）
            foreach (var view in ConventionScan.Files("Views", "*.cshtml")
                         .Concat(ConventionScan.Files("Modules", "*.cshtml")))
            {
                foreach (Match m in InlineBilingualLiteral.Matches(File.ReadAllText(view)))
                    vocabulary.Add(m.Groups[1].Value.Trim());
            }

            // 手機字典：i18n.js（與 Core 一致的共用字串）＋ i18n.modules.js（ProjectZ 模組字串）
            foreach (var dictName in new[] { "i18n.js", "i18n.modules.js" })
            {
                var dictPath = Path.Combine(ConventionScan.RepoRoot(), "wwwroot", "mobile", "js", dictName);
                if (!File.Exists(dictPath)) continue;
                var text = File.ReadAllText(dictPath);
                var zhStart = text.IndexOf("'zh-TW':", StringComparison.Ordinal);
                var enStart = text.IndexOf("'en-US':", StringComparison.Ordinal);
                if (zhStart >= 0 && enStart > zhStart)
                    foreach (Match m in Regex.Matches(text[zhStart..enStart], @":\s*'([^']+)'"))
                        vocabulary.Add(m.Groups[1].Value.Trim());
            }

            return vocabulary;
        }

        // `@(isEn ? "Reset" : "重置")` 取中文那一半；只收含中日韓字元的字面值，
        // 避免把純英文或 class 名稱之類的字串灌進詞彙表。
        private static readonly Regex InlineBilingualLiteral = new(
            @"\?\s*""[^""\r\n]*""\s*:\s*""([^""\r\n]*[一-鿿][^""\r\n]*)""",
            RegexOptions.Compiled);

        // R29：規格文件裡「還沒結案的待決項」，必須出現在 STATUS.md〈真正未完成〉清單；
        //      已經結案的，不得繼續掛在那份清單上。雙向對帳。
        //
        //   為什麼需要這條：STATUS.md〈真正未完成〉自陳是「下次規劃工作可直接看這裡，
        //   不用重讀全文」的入口，但它是**手抄**的聚合——同一件事寫在兩個地方，
        //   兩邊就會慢慢對不起來。2026-08-13 逐條查證那份清單時，7 條裡有 3 條已經不成立
        //   （待決 #9 早已裁決、G2 已由 R28 守門、§9 #4 的檔案早在 `a0641be` 刪掉了），
        //   同時 §14.2 的待決 #10 從來沒被收進去過。
        //
        //   兩個方向的代價不一樣，所以兩邊都要守：
        //     - 漏收（來源開著、清單沒有）→ 缺口被藏起來，**這個嚴重得多**；
        //     - 過期（來源結案、清單還開著）→ 有人照著清單把已經做完的事再做一遍。
        //
        //   刻意**不做自動產生**：清單上每一條都帶著產生器寫不出來的判斷
        //   （「卡外部 ERP 介接方式」「L1 過濾本體要 P2 才實作，屆時一次做對即可」），
        //   拿產生器覆蓋等於把這些理由洗掉。所以人繼續寫散文，機器只負責對帳。
        //
        //   契約（三件事，都刻意選了不必改寫既有排版的形式）：
        //     1. 來源端用 `<!-- open-items ref="…" -->` … `<!-- /open-items -->` 圈出範圍；
        //     2. 範圍內每個項目的編號取自表格第一欄或有序清單的序號；
        //        編號被 `~~` 圍起來＝已結案，帶 `⏸`＝刻意延後（等上線數據，不列入清單）；
        //     3. STATUS.md 那一條只要在同一個項目符號裡同時出現 `ref` 與編號就算對上，
        //        所以 `待決 #3／#11` 這種合寫法不必拆開。
        [Fact]
        public void R29_OpenSpecItems_TrackedInStatusIndex()
        {
            var items = OpenItemRegions();
            Assert.True(items.Count >= 15,
                $"R29 只解析到 {items.Count} 個待決項——`<!-- open-items -->` 標記已失效，規則形同關閉");
            Assert.Contains(items, i => i.State == OpenItemState.Open);
            Assert.Contains(items, i => i.State == OpenItemState.Closed);

            var bullets = StatusOutstandingBullets();
            Assert.True(bullets.Count >= 3,
                "R29 在 STATUS.md 找不到〈真正未完成〉的項目符號——章節標題已變，規則形同關閉");

            var violations = new List<string>();

            foreach (var item in items)
            {
                var covering = bullets
                    .Where(b => b.Text.Contains(item.Ref, StringComparison.Ordinal)
                                && MentionsItemId(b.Text, item.Id))
                    .ToList();

                switch (item.State)
                {
                    case OpenItemState.Open when !covering.Any(b => b.Live):
                        violations.Add(
                            $"{item.Source}：{item.Ref} #{item.Id} 尚未結案，但 STATUS.md" +
                            "〈真正未完成〉沒有列——請補一條，或在來源用 ~~ 標示結案／用 ⏸ 標示刻意延後");
                        break;

                    case OpenItemState.Closed when covering.Any(b => b.Live):
                        violations.Add(
                            $"{item.Source}：{item.Ref} #{item.Id} 在來源已標示結案（~~），但 STATUS.md" +
                            "〈真正未完成〉仍把它列為未完成——請把該條也劃掉，理由留在原地不要刪");
                        break;
                }
            }

            AssertRule("R29", violations);
        }

        // R31：每個手機畫面都必須有一頁使用手冊在講它。
        //
        //   為什麼需要這條：桌機側早就有 `HelpDocResolverTests`（已登錄畫面必有實體手冊頁），
        //   **手機側一條都沒有**——手機畫面不進 MenuContributor、沒有 MenuKey，
        //   那支測試的母體裡根本沒有它們。
        //
        //   代價已經發生過一次：手機「待我簽核」自 2026-08-27（唯讀）到 09-10（可簽核）
        //   經過三個 PR、14 個單別的摘要，**手冊裡一個字都沒有**，
        //   連 `wwwroot/docs/user/mobile/index.html` 全文搜「簽核」都是 0 命中。
        //   沒有任何測試會紅，因為沒有任何測試在看這件事。
        //
        //   判準刻意選「手冊頁自己宣告它在講哪一頁」而不是反過來由檔名推導：
        //   手冊頁的檔名是給人看的（`dispatch-list.html` 講的是 `/mobile/mfg/list.html`），
        //   用檔名配對會逼手冊改名去迎合程式，那是把因果弄反了。
        //   每頁 doc-header 裡的「路徑：/mobile/…」就是那份宣告。
        //
        //   例外要寫理由，不是列白名單了事——照 `ApprovalSummaryCoverageTests`
        //   的形狀：說不出理由的那一刻，就是應該補手冊的時候。
        private static readonly Dictionary<string, string> MobilePagesWithoutOwnManualPage =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"]    = "登入頁。手冊在 mobile/index.html 的〈登入（含兩階段驗證）〉一節，不另立頁。",
            ["home.html"]     = "功能首頁。手冊在〈功能模組〉與〈切換廠別與推播通知〉兩節，不另立頁。",
            ["offline.html"]  = "離線替代頁，沒有任何操作。離線行為寫在〈離線佇列〉一節。",
            ["inv/list.html"] = "倉儲作業的模組入口清單，本身只是一排連結；各作業各自有頁。",

            ["mfg/ipqc-process.html"] =
                "IPQC／FPI 共用的量測頁，從 ipqc.html 與 fpi.html 點進來，沒有自己的入口。"
              + "量測步驟寫在那兩頁裡——拆成第三頁反而讓人找不到自己在哪條流程上。",

            ["mfg/schedule-query.html"] =
                "排程結果查詢，入口在生產排程頁內。說明寫在 schedule.html。",

            ["workorderexecution/list.html"] =
                "轉址殼（<title>轉址中…</title>），實際畫面是 mfg/list.html。沒有東西可以寫。",
        };

        [Fact]
        public void R31_MobileScreens_HaveAManualPage()
        {
            var root = ConventionScan.RepoRoot();
            var appDir = Path.Combine(root, "wwwroot", "mobile");
            Assert.True(Directory.Exists(appDir), "R31 找不到 wwwroot/mobile——手機畫面的位置變了，規則形同關閉");

            // 手冊頁自己宣告它在講哪一支畫面：doc-header 的「路徑：/mobile/…」。
            var documented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in ConventionScan.Files("wwwroot/docs/user/mobile", "*.html"))
                foreach (Match m in ManualMobilePath.Matches(File.ReadAllText(doc)))
                    documented.Add(m.Groups[1].Value.Trim());

            Assert.True(documented.Count >= 10,
                $"R31 只從手冊解析到 {documented.Count} 條「路徑：/mobile/…」宣告——"
              + "doc-header 的排版變了，規則形同關閉");

            var violations = new List<string>();
            var probed = 0;

            foreach (var page in Directory.EnumerateFiles(appDir, "*.html", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(appDir, page).Replace('\\', '/');
                probed++;

                if (MobilePagesWithoutOwnManualPage.ContainsKey(rel)) continue;
                if (documented.Contains("/mobile/" + rel)) continue;

                violations.Add(
                    $"wwwroot/mobile/{rel}：這個手機畫面沒有任何手冊頁在講它。"
                  + "請在 wwwroot/docs/user/mobile/ 新增一頁（doc-header 寫上「路徑：/mobile/" + rel + "」）"
                  + "並掛進 index.html 的側欄，或加進 MobilePagesWithoutOwnManualPage 並寫下**為什麼這一頁不需要手冊**");
            }

            Assert.True(probed > 0, "R31 沒有掃到任何手機畫面——目錄結構已變，規則形同關閉");

            // 反面：例外清單不得留下已經不存在的頁，否則清單會慢慢變成化石。
            foreach (var exempt in MobilePagesWithoutOwnManualPage.Keys)
                if (!File.Exists(Path.Combine(appDir, exempt.Replace('/', Path.DirectorySeparatorChar))))
                    violations.Add($"MobilePagesWithoutOwnManualPage 列著 {exempt}，但 wwwroot/mobile 下已經沒有這個檔——請移除該列");

            AssertRule("R31", violations);
        }

        // R32：側面板的「版面 id ↔ 腳本契約」不得混用（manual-test-plan §11 #12）。
        //
        //   全站兩套 CRUD 側面板契約並存：
        //     A. `#sidePanel` / `#sidePanelLabel` / `#sidePanelBody` ＋ 共用的 `crud-panel.js`
        //        （由 `_CrudPanelScripts` 載入）——57 個畫面；
        //     B. `#crudPanel` / `#crudPanelTitle` / `#crudPanelBody` ＋ 各 Index 自帶 inline script
        //        （BAS 舊式）——23 個畫面。
        //
        //   2026-09-10 的 R-F1 是這樣進來的：`ReportSchedule/Index.cshtml` 版面抄 B、
        //   腳本載 A，而 B 的 inline script 沒跟著抄——**兩邊都不屬於**。
        //   症狀是點「新增排程」直接 `Cannot set properties of null`，面板永遠開不起來；
        //   連帶表單上的 `data-ajax` / `data-dismiss-panel` 在那一頁沒有任何腳本會讀，
        //   所以存檔與取消也是死的。
        //
        //   **為什麼既有守門一條都攔不到**：R5b／CSP／R14／Cx8 掃的都是單檔，而
        //   單看版面合法、單看腳本也合法——**錯的是兩者的組合**。這條規則做的就是那個配對。
        //
        //   三件事各自對應一種真實的失敗方式：
        //     1. 同一個檔同時出現 B 的 id 與 A 的腳本 → 就是 R-F1 本身；
        //     2. A 的三個 id 只出現一部分 → `openPanel` 會在缺的那一個上拋 null
        //        （R-F1 缺的正是 `sidePanelLabel`）；
        //     3. 走 A 的畫面，其資料夾內的 partial 不得留 `data-ajax` / `data-dismiss-panel`。
        //        ⚠️ 範圍**只到這些資料夾**，不是全站：那兩個屬性在 B 的 23 個畫面是活的
        //        （inline script 真的在讀 `#crudPanelBody form[data-ajax="true"]`），
        //        全庫仍有 49／41 處，寫成全站禁用會一次誤判掉整個 BAS。
        //
        //   partial 用「同資料夾」而不是解析 `PartialAsync` 的引數：R-F1 那支表單
        //   根本不在 Index 裡被 render——它是 controller 以 `PartialView("_ReportScheduleForm")`
        //   回傳、由 `openPanel` fetch 進面板的。靜態追 render 樹會漏掉正是要抓的那一種。
        [Fact]
        public void R32_PanelMarkup_MatchesItsScriptContract()
        {
            var violations = new List<string>();
            var probed = 0;

            foreach (var file in ConventionScan.Files("Views", "*.cshtml")
                         .Concat(ConventionScan.Files("Modules", "*.cshtml")))
            {
                var rel = ConventionScan.Rel(file);

                // `_CrudPanelScripts` 自己就是那份腳本，不是它的使用者。
                if (rel.EndsWith("_CrudPanelScripts.cshtml", StringComparison.Ordinal)) continue;

                var text = File.ReadAllText(file);
                var loadsSharedScripts = text.Contains("_CrudPanelScripts", StringComparison.Ordinal);
                var hasSidePanel       = text.Contains("id=\"sidePanel\"", StringComparison.Ordinal);

                if (!loadsSharedScripts && !hasSidePanel) continue;
                probed++;

                // ① 兩套契約混用。
                if (loadsSharedScripts)
                {
                    foreach (var legacyId in new[] { "crudPanel", "crudPanelTitle", "crudPanelBody" })
                        if (text.Contains($"id=\"{legacyId}\"", StringComparison.Ordinal))
                            violations.Add(
                                $"{rel}：載入了 `_CrudPanelScripts`（契約是 #sidePanel／#sidePanelLabel／#sidePanelBody），"
                              + $"版面卻用 BAS 舊式的 `id=\"{legacyId}\"`。兩邊都不屬於——面板會在 openPanel 當場拋 null。"
                              + "請二選一：版面改成 #sidePanel 三件套，或改抄 BAS 的 inline script 並移除 `_CrudPanelScripts`");
                }

                // ② 三個 id 是一組的，缺一個就等於全缺。
                if (hasSidePanel)
                {
                    foreach (var needed in new[] { "sidePanelLabel", "sidePanelBody" })
                        if (!text.Contains($"id=\"{needed}\"", StringComparison.Ordinal))
                            violations.Add(
                                $"{rel}：有 `id=\"sidePanel\"` 卻沒有 `id=\"{needed}\"`。"
                              + "crud-panel.js 三個都會取，缺任何一個 openPanel 就在那一行拋 null（R-F1 缺的正是 sidePanelLabel）");
                }

                // ③ 走共用契約的資料夾裡，partial 不得留下沒有人讀的屬性。
                if (!loadsSharedScripts) continue;

                var dir = Path.GetDirectoryName(file)!;
                foreach (var partial in Directory.EnumerateFiles(dir, "_*.cshtml"))
                {
                    var ptext = File.ReadAllText(partial);
                    foreach (var dead in new[] { "data-ajax", "data-dismiss-panel" })
                        if (ptext.Contains(dead, StringComparison.Ordinal))
                            violations.Add(
                                $"{ConventionScan.Rel(partial)}：同資料夾的 {Path.GetFileName(file)} 走共用面板契約，"
                              + $"這裡卻留著 `{dead}`——**只有 BAS 舊式的 inline script 會讀它**，"
                              + "在這條路徑上沒有任何腳本讀得到，按鈕按了不會有反應。"
                              + "請改用 `data-action-submit=\"submitPanel\"` 與 `data-action=\"closeSidePanel\"`");
                }
            }

            // 自我檢查：partial 改名或 View 搬家之後，這條規則要嘛紅要嘛掃不到東西，
            // 不可以安靜地永遠綠——那比沒有規則更糟，它會讓人以為有人在看。
            Assert.True(probed >= 50,
                $"R32 只掃到 {probed} 個面板畫面（預期 80 左右）——`_CrudPanelScripts` 或 #sidePanel 的寫法變了，規則形同關閉");

            AssertRule("R32", violations);
        }

        private static readonly Regex ManualMobilePath =
            new(@"路徑：\s*(/mobile/[A-Za-z0-9/_.-]+\.html)", RegexOptions.Compiled);

        private enum OpenItemState { Open, Closed, Deferred }

        private sealed record OpenItem(string Source, string Ref, string Id, OpenItemState State);

        private sealed record StatusBullet(string Text, bool Live);

        private static readonly Regex OpenItemsBegin =
            new(@"<!--\s*open-items\s+ref=""([^""]+)""\s*-->", RegexOptions.Compiled);

        /// <summary>
        /// 掃 `.claude/specifications/*.md` 裡所有 open-items 區塊，回傳其中的待決項。
        /// 項目來源有兩種排版：表格列（編號在第一欄）與有序清單（編號即序號）。
        /// </summary>
        private static List<OpenItem> OpenItemRegions()
        {
            var items = new List<OpenItem>();

            foreach (var file in ConventionScan.Files(".claude/specifications", "*.md"))
            {
                var rel = ConventionScan.Rel(file);
                string? currentRef = null;

                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();

                    var begin = OpenItemsBegin.Match(line);
                    if (begin.Success) { currentRef = begin.Groups[1].Value; continue; }
                    if (line.StartsWith("<!-- /open-items", StringComparison.Ordinal))
                    { currentRef = null; continue; }
                    if (currentRef == null) continue;

                    // 表格列取第一欄；有序清單取序號。兩者都保留原始文字以判斷 ~~ 與 ⏸。
                    string cell;
                    if (line.StartsWith('|'))
                    {
                        var end = line.IndexOf('|', 1);
                        if (end < 0) continue;
                        cell = line[1..end];
                    }
                    else
                    {
                        var ordered = Regex.Match(line, @"^(\d+)\.\s");
                        if (!ordered.Success) continue;
                        // 有序清單的結案標記寫在標題上（`3. ~~**…**~~〔已裁決〕`），不在序號上
                        cell = ordered.Groups[1].Value + (line.Contains("~~") ? "~~" : "")
                                                       + (line.Contains('⏸') ? "⏸" : "");
                    }

                    var id = cell.Replace("~~", "").Replace("*", "").Replace("⏸", "")
                                 .Replace("#", "").Replace("`", "").Trim();
                    // 表頭（`#`／`---`）與分隔列在去掉裝飾後為空或全是短劃線
                    if (id.Length == 0 || id.All(c => c == '-' || c == ':')) continue;
                    if (!Regex.IsMatch(id, @"^[A-Za-z]?[0-9]+$")) continue;

                    var state = cell.Contains("~~") ? OpenItemState.Closed
                              : cell.Contains('⏸') ? OpenItemState.Deferred
                              : OpenItemState.Open;

                    items.Add(new OpenItem(rel, currentRef, id, state));
                }
            }

            return items;
        }

        /// <summary>
        /// STATUS.md〈真正未完成〉章節裡的項目符號。巢狀項目也算獨立一條——
        /// 否則被劃掉的子項（例如 G2）會被當成外層那條還活著的項目的內文，反而漏報。
        /// </summary>
        private static List<StatusBullet> StatusOutstandingBullets()
        {
            var path = Path.Combine(ConventionScan.RepoRoot(),
                ".claude", "specifications", "STATUS.md");
            var bullets = new List<StatusBullet>();
            if (!File.Exists(path)) return bullets;

            var inSection = false;
            var current = new StringBuilder();
            var live = false;

            void Flush()
            {
                if (current.Length > 0) bullets.Add(new StatusBullet(current.ToString(), live));
                current.Clear();
            }

            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (inSection) { Flush(); break; }
                    inSection = line.Contains("真正未完成", StringComparison.Ordinal);
                    continue;
                }
                if (!inSection) continue;

                var bullet = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
                if (bullet.Success)
                {
                    Flush();
                    var body = bullet.Groups[1].Value;
                    live = !body.TrimStart().StartsWith("~~", StringComparison.Ordinal);
                    current.Append(body);
                }
                else current.Append(' ').Append(line.Trim());
            }
            Flush();

            return bullets;
        }

        /// <summary>
        /// 編號比對。數字編號要求前面帶 `#`（否則 §8.3 之類的章節號會誤命中），
        /// 且後面不得再接數字——`#1` 不可以命中 `#11`。
        /// </summary>
        private static bool MentionsItemId(string text, string id)
        {
            var token = id.All(char.IsDigit) ? "#" + id : id;
            return Regex.IsMatch(text, Regex.Escape(token) + @"(?![0-9])");
        }

        // R30：查詢 StorageLocations 必須明示廠別範圍。
        //   StorageLocation 的主鍵是 (WhCode, LocationCode)，本身沒有 PlantId、也沒有實作
        //   IPlantEntity——ApplicationDbContext 那組「PlantId == CurrentPlantId」的全域查詢
        //   過濾器對它完全不生效。於是 `_db.StorageLocations.Where(l => l.IsActive)` 這種
        //   看起來再正常不過的寫法，拿到的是**全公司所有廠**的儲位。
        //   人工驗收 W4-F11 由跨廠調撥的來源儲位下拉先踩到（撥出端倉庫選單出現 FG-P1／
        //   RM-P1／QC-P2…），事後全庫掃描發現同一寫法散在 15 處：庫存異動／調整／報廢／
        //   移轉／備料／盤點的儲位下拉全部跨廠，行動端 /api/warehouse/location/{code} 掃碼
        //   驗證與揀貨過帳更是直接放行別廠儲位——那不是選錯而已，是真的會過帳到別廠。
        //   共通症狀是**沒有錯誤訊息**：選了別廠儲位，伺服端拿本廠去查可用量必然是 0，
        //   使用者只看到一句與原因毫無關聯的「可用量不足」。
        //   合格寫法四選一（都在同一個運算式鏈內）：
        //     ① .ForCurrentPlant(db)      收斂到目前廠（StorageLocationQueryExtensions）
        //     ② .IgnoreQueryFilters()     明示要跨廠（跨廠調撥目的端、主檔刪除守門）
        //     ③ .Join(… Warehouses …)     由倉庫帶入廠別過濾
        //     ④ 條件含 WhCode ==          倉庫代碼全域唯一，指定倉庫等同指定廠
        //   寫入（Add/Remove/Update）與主鍵查詢（FindAsync(whCode, locationCode)）不在範圍內。
        [Fact]
        public void R30_StorageLocationQueries_ArePlantScoped()
        {
            var files = ConventionScan.Files("Modules", "*.cs")
                .Concat(ConventionScan.Files("Controllers", "*.cs"))
                .Concat(ConventionScan.Files("Services", "*.cs"))
                .Concat(ConventionScan.Files("Data", "*.cs"))
                .Where(f => !f.EndsWith("StorageLocationQueryExtensions.cs", StringComparison.Ordinal));

            var usage = new Regex(@"\b\w+\.(?:StorageLocations\b|Set<StorageLocation>\(\))",
                RegexOptions.Compiled);

            var violations = new List<string>();
            var probed = 0;

            foreach (var file in files)
            {
                var text = ConventionScan.StripComments(File.ReadAllText(file));
                foreach (Match m in usage.Matches(text))
                {
                    // 取「這一句」：自命中處到下一個分號為止（LINQ 鏈一律以分號收尾）。
                    var end = text.IndexOf(';', m.Index);
                    var stmt = end < 0 ? text[m.Index..] : text[m.Index..end];

                    // 寫入與主鍵查詢：本來就指定了 WhCode，不在規則範圍。
                    if (stmt.Contains(".Add(", StringComparison.Ordinal) ||
                        stmt.Contains(".AddRange(", StringComparison.Ordinal) ||
                        stmt.Contains(".Remove(", StringComparison.Ordinal) ||
                        stmt.Contains(".RemoveRange(", StringComparison.Ordinal) ||
                        stmt.Contains(".Update(", StringComparison.Ordinal) ||
                        stmt.Contains(".FindAsync(", StringComparison.Ordinal) ||
                        stmt.Contains(".Find(", StringComparison.Ordinal))
                        continue;

                    probed++;

                    if (stmt.Contains(".ForCurrentPlant(", StringComparison.Ordinal)) continue;
                    if (stmt.Contains(".IgnoreQueryFilters(", StringComparison.Ordinal)) continue;
                    if (stmt.Contains(".Join(", StringComparison.Ordinal) &&
                        stmt.Contains("Warehouses", StringComparison.Ordinal)) continue;
                    if (Regex.IsMatch(stmt, @"WhCode\s*==")) continue;

                    var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    violations.Add($"{ConventionScan.Rel(file)}:{line}: 查 StorageLocations 未表態廠別範圍");
                }
            }

            Assert.True(probed > 0,
                "R30 沒有探測到任何 StorageLocations 查詢——掃描條件已失效，規則形同關閉");

            AssertRule("R30", violations);
        }

        // R33：controller 的每頁筆數預設值，必須出現在 _PageSizeSelect 的選項清單裡。
        //
        // 為什麼要有這條：BAS/SpecialDay 的 controller 預設 31 筆（一個月），但下拉只有
        // {10,20,50,100}——**沒有任何 option 被選中**，於是瀏覽器顯示第一項「10」，
        // 而畫面實際上是 31 筆。不會報錯、不會拋例外，只是那個數字一直在騙人。
        // 選項清單直接從 partial 讀回來，規則與實作不會各說各話。
        [Fact]
        public void R33_ControllerPageSizeDefault_IsOfferedByTheSelect()
        {
            var partial = Path.Combine(ConventionScan.RepoRoot(), "Views", "Shared", "_PageSizeSelect.cshtml");
            Assert.True(File.Exists(partial), "找不到 _PageSizeSelect.cshtml——R33 沒有比對基準");

            var optionsDecl = Regex.Match(File.ReadAllText(partial), @"pageSizeOptions\s*=\s*new\[\]\s*\{([^}]*)\}");
            Assert.True(optionsDecl.Success, "_PageSizeSelect 的 pageSizeOptions 宣告改過了，R33 讀不到選項清單");
            var allowed = optionsDecl.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToHashSet();

            var defaults = new Regex(@"pageSize\s*\?\?\s*(\d+)|\bint\??\s+pageSize\s*=\s*(\d+)", RegexOptions.Compiled);
            var violations = new List<string>();
            var probed = 0;

            foreach (var file in ConventionScan.Files("Controllers", "*.cs")
                         .Concat(ConventionScan.Files("Modules", "*Controller.cs")))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in defaults.Matches(text))
                {
                    probed++;
                    var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (allowed.Contains(int.Parse(raw))) continue;
                    var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    violations.Add($"{ConventionScan.Rel(file)}:{line}: 每頁筆數預設 {raw} 不在下拉選項 " +
                                   $"[{string.Join(", ", allowed.OrderBy(x => x))}] 內——下拉會顯示錯的值");
                }
            }

            Assert.True(probed >= 50,
                $"R33 只探測到 {probed} 處每頁筆數預設值——掃描條件已失效，規則形同關閉");

            AssertRule("R33", violations);
        }

        // R34：清單操作欄的動作按鈕一律「圖示 ＋ 文字」。
        //
        // 對象＝表格 <td> 內、綁在單一列上的 <a>/<button>（有 asp-route-id／data-id／data-action）。
        // 明細編輯列的 removeXxx / lookupXxx / addXxx 是**例外**——那種按鈕每一列都有一個，
        // 加上文字會把版面洗掉；但它們必須有 title 或 aria-label，否則滑鼠停留沒有提示、
        // 讀螢幕器也讀不出來（2026-09-11 收口前有 53 個這樣的按鈕）。
        //
        // 統計背景：收口前 224 個列級動作裡，圖示+文字 123、只有圖示 71、只有文字 30，
        // 而且有 20 個畫面**自己內部就在混用**。
        [Fact]
        public void R34_RowActions_ShowBothIconAndLabel()
        {
            var tdBlock  = new Regex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);
            var actionEl = new Regex(@"<(a|button)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.Compiled);
            var rowScoped = new Regex(@"asp-route-id|asp-route-Id|data-id=|data-action(-click)?=", RegexOptions.Compiled);
            var lineEdit  = new Regex(@"data-action(?:-click)?=""(?:remove|lookup|add)", RegexOptions.Compiled);
            var fieldOnly = new Regex(@"^@[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled);

            var violations = new List<string>();
            var probed = 0;

            foreach (var view in AllViews())
            {
                var text = File.ReadAllText(view);
                var rel  = ConventionScan.Rel(view);
                foreach (Match td in tdBlock.Matches(text))
                {
                    foreach (Match m in actionEl.Matches(td.Groups[1].Value))
                    {
                        var el = m.Value;
                        var gt = el.IndexOf('>');
                        var close = el.LastIndexOf("</", StringComparison.Ordinal);
                        if (gt < 0 || close <= gt) continue;
                        var head = el[..(gt + 1)];
                        if (head.Contains("btn-close") || head.Contains("data-bs-dismiss")) continue;
                        if (!rowScoped.IsMatch(head)) continue;

                        var inner = el[(gt + 1)..close];
                        var hasIcon = Regex.IsMatch(inner, @"<i\b");
                        var label = Regex.Replace(inner, @"<[^>]*>", "").Replace("&nbsp;", "").Trim();
                        if (fieldOnly.IsMatch(label)) continue;   // 單號連結，不是動作
                        probed++;

                        var line = text.Take(td.Groups[1].Index + m.Index).Count(c => c == '\n') + 1;
                        if (lineEdit.IsMatch(head))
                        {
                            if (!head.Contains("title=") && !head.Contains("aria-label="))
                                violations.Add($"{rel}:{line}: 明細列的圖示按鈕缺 title/aria-label");
                            continue;
                        }
                        if (!hasIcon || label.Length == 0)
                            violations.Add($"{rel}:{line}: 操作欄動作必須「圖示 + 文字」" +
                                           $"（目前{(hasIcon ? "只有圖示" : "只有文字")}）");
                    }
                }
            }

            Assert.True(probed >= 150,
                $"R34 只探測到 {probed} 個列級動作——掃描條件已失效，規則形同關閉");

            AssertRule("R34", violations);
        }


        // R35：`<!-- mirrors -->` 圈起來的「手抄聚合節」，狀態必須與來源待決項一致。
        //
        //   為什麼在 R29 之外還要這一條：R29 對帳的是**兩份**文件——來源的
        //   `<!-- open-items -->` 與 `STATUS.md`〈真正未完成〉。但同一批待決項在本專案
        //   其實被抄了**四份**：§11（來源）、`STATUS.md`、`manual-test-plan.md` §12
        //   〈未完成事項總表〉，以及 §12 底下各分節自己的小表。R29 只守前兩份，
        //   §12 從一開始就在守門範圍之外。
        //
        //   它已經漂過，而且是自己寫下來的：§12.3 開頭那段自陳
        //   「三項在 §11 都已劃掉結案，但這張表沒跟著改——**R29 對不到這裡**」；
        //   §12.8 結尾也寫著「後三列一旦在 §11 劃掉，這裡不會自動跟著變」。
        //   兩處都正確診斷了問題，然後**繼續靠人記得**。這條規則就是把那句話變成測試。
        //
        //   三個方向，代價不一樣（與 R29 同一組理由）：
        //     - 漏收（§11 開著、§12 沒有）→ 缺口從總表上消失，**最嚴重**；
        //     - 過期（§11 結案、§12 還活著）→ 有人照著總表把做完的事再做一遍；
        //     - 早退（§11 開著、§12 已劃掉）→ 比過期更糟，缺口被「看起來已完成」蓋掉。
        //
        //   契約（同樣刻意不必改寫既有排版）：
        //     1. 聚合節用 `<!-- mirrors ref="…" -->` … `<!-- /mirrors -->` 圈出範圍，
        //        `ref` 與來源 `<!-- open-items ref="…" -->` 的值相同；
        //     2. 一列「講到」某個待決項的判準有兩種，對應本節實際存在的兩種排版：
        //        ① 第一欄就是編號（§12.3 的 `| ~~5~~ |`）；
        //        ② **第二欄**（出處欄）同時寫出章節記號與編號（§12.8 的 `| … | §11 #12 | … |`）。
        //        ⚠️ `#N` **只認第二欄**：內文裡到處都是「改寫了 §11 #8 的風險評估」這種
        //        交叉引用，那是在講它、不是在追蹤它。全列去抓會把敘述誤判成聚合列
        //        （實測 §12.7 的手動發票那列正是這樣被誤命中）。
        //     3. 「已結案」的訊號收 `~~` 與 `✅` 兩種：§12.3／§12.8 用刪節線，
        //        §12.1／§12.4 用 ✅ 前綴。硬性統一成一種等於為了守門去重排既有文件。
        //     4. 來源標 `⏸`（刻意延後）者**跳過不驗**：那種項目在 `STATUS.md` 是不列的
        //        （R29 的規定），在〈未完成事項總表〉卻該列——§12.5 整節就是那個桶子。
        //        兩邊語意相反，逼它們一致只會製造假違規。
        //     5. **守門範圍由聚合節自己宣告**：只有被某個 `<!-- mirrors ref="X" -->`
        //        指名的來源 X 才進對帳。沒有任何聚合節指向它的 `<!-- open-items -->`
        //        （`llm-qa-and-report-tools.md` §17.9、`user-manual-tutor.md` §9 等）**不在範圍內**
        //        ——它們本來就沒有第三份抄本，R29 對 `STATUS.md` 那條已經在守。
        //        少了這一條，規則會要求「全庫每一個待決項都要列進 manual-test-plan 的 §12」，
        //        那是另一份文件的總表，不是它的職責（初版實測誤報 5 筆，全出自這個缺漏）。
        [Fact]
        public void R35_MirroredOpenItems_MatchSourceState()
        {
            var source = OpenItemRegions();
            var mirrors = MirrorRows(out var scannedRows, out var declaredRefs);

            // 兩層自我檢查測的是不同的失效：`scannedRows` 是「標記還圈得到表格嗎」
            //（標記被刪或章節搬家 → 掉到 0），`matched` 是「編號還認得出來嗎」
            //（排版改了 → 圈得到表但一列都對不上）。只留後者會讓前者靜默通過。
            Assert.True(scannedRows >= 40,
                $"R35 只在 `<!-- mirrors -->` 區塊內掃到 {scannedRows} 列表格——標記已失效，規則形同關閉");

            // 只有被聚合節指名的來源才進對帳，理由見規則說明第 5 點。
            // 取自標記本身而非解析出來的列：一個 `ref` 底下所有列都認不出編號時，
            // 範圍要維持成立（讓 matched 的自我檢查去紅），不能靜靜縮成空集合。
            var gated = declaredRefs;

            var violations = new List<string>();
            var matched = 0;

            foreach (var item in source)
            {
                if (item.State == OpenItemState.Deferred) continue;
                if (!gated.Contains(item.Ref)) continue;

                var rows = mirrors
                    .Where(r => r.Ref == item.Ref && r.Ids.Contains(item.Id))
                    .ToList();
                matched += rows.Count;

                foreach (var row in rows)
                {
                    if (item.State == OpenItemState.Closed && !row.Closed)
                        violations.Add(
                            $"{row.Source}：{item.Ref} #{item.Id} 在來源已結案，但聚合節仍列為未完成" +
                            "——請在該列補 `~~` 或 ✅，理由留在來源不要搬過來");
                    else if (item.State == OpenItemState.Open && row.Closed)
                        violations.Add(
                            $"{row.Source}：{item.Ref} #{item.Id} 在聚合節已劃掉，但來源還開著" +
                            "——缺口會被「看起來已完成」蓋住；請先在來源結案再劃這裡");
                }

                if (item.State == OpenItemState.Open && rows.Count == 0)
                    violations.Add(
                        $"{item.Source}：{item.Ref} #{item.Id} 尚未結案，但〈未完成事項總表〉沒有列" +
                        "——請補一列（第一欄寫編號，或在出處欄寫 `" + SectionToken(item.Ref) + " #" + item.Id + "`）");
            }

            Assert.True(matched >= 8,
                $"R35 只對上 {matched} 列——編號比對的兩種排版都沒命中，規則形同關閉");

            AssertRule("R35", violations);
        }

        private sealed record MirrorRow(string Source, string Ref, HashSet<string> Ids, bool Closed);

        // ⚠️ 錨在行首（對 Trim 過的行）是必要的，不是保守：文件裡會**談到**這個標記
        // （§12.3 就寫著「整個 §12 已用 `<!-- mirrors ref=... -->` 圈起來」），
        // 不錨行首的話那句散文會被當成第二個開始標記，把後面的章節一起圈進來——
        // 實測會讓一次變異驗證從「0 列命中」變成「6 列命中」而看起來像規則還活著。
        // 結束標記本來就是 StartsWith，兩邊要對稱。
        private static readonly Regex MirrorsBegin =
            new(@"^<!--\s*mirrors\s+ref=""([^""]+)""\s*-->", RegexOptions.Compiled);

        /// <summary>`ref` 的章節記號（`manual-test-plan.md §11` → `§11`）。第二欄靠它認出處。</summary>
        private static string SectionToken(string reference)
        {
            var i = reference.IndexOf('§');
            return i < 0 ? reference : reference[i..].Trim();
        }

        /// <summary>
        /// 掃所有 `<!-- mirrors -->` 區塊裡的表格列，取出「這一列在講哪幾個待決項」與它的結案狀態。
        /// </summary>
        private static List<MirrorRow> MirrorRows(out int scannedRows, out HashSet<string> declaredRefs)
        {
            var rows = new List<MirrorRow>();
            var refs = new HashSet<string>(StringComparer.Ordinal);
            scannedRows = 0;

            foreach (var file in ConventionScan.Files(".claude/specifications", "*.md"))
            {
                var rel = ConventionScan.Rel(file);
                string? currentRef = null;

                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();

                    var begin = MirrorsBegin.Match(line);
                    if (begin.Success)
                    { currentRef = begin.Groups[1].Value; refs.Add(currentRef); continue; }
                    if (line.StartsWith("<!-- /mirrors", StringComparison.Ordinal))
                    { currentRef = null; continue; }
                    if (currentRef == null || !line.StartsWith('|')) continue;
                    scannedRows++;

                    var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.Length == 0) continue;

                    var ids = new HashSet<string>(StringComparer.Ordinal);

                    // ①第一欄就是編號
                    var first = Regex.Replace(cells[0], @"[~*⏸#`\s]", "");
                    if (Regex.IsMatch(first, @"^[A-Za-z]?[0-9]+$")) ids.Add(first);

                    // ②第二欄（出處欄）寫出章節記號＋編號。刻意不掃整列，理由見規則說明。
                    if (cells.Length > 1 && cells[1].Contains(SectionToken(currentRef), StringComparison.Ordinal))
                        foreach (Match m in Regex.Matches(cells[1], @"#([0-9]+)(?![0-9])"))
                            ids.Add(m.Groups[1].Value);

                    if (ids.Count == 0) continue;

                    rows.Add(new MirrorRow(rel, currentRef, ids,
                        line.Contains("~~", StringComparison.Ordinal)
                        || line.Contains('✅', StringComparison.Ordinal)));
                }
            }

            declaredRefs = refs;
            return rows;
        }

        // R36：排程時刻欄位不得被任何時區換算碰到。
        //   ScheduledStart／ScheduledEnd／PlannedStartDate／PlannedEndDate 的**數字本身就是
        //   廠別當地時間**，Kind=Utc 只是貼給 Npgsql 的標籤（mfg-domain.md §UTC 地雷；
        //   源頭見 MrpEngine 的 Today = SpecifyKind(_dt.Now.Date, Utc)）。對它們再做一次
        //   換算就是多加一個時差。
        //
        //   ⚠️ 為什麼這條規則跟「顯示不得用伺服器時區」那條不能合併：
        //   那一條抓的是 BCL 的 DateTime.ToLocalTime()，修法是改走 IDateTimeService。
        //   這些欄位**兩種都不對**——正確做法是不換算。把它們「修正」成
        //   DateTimeSvc.ToLocalTime(...) 會通過那一條，而症狀完全一樣（差一個時差），
        //   所以要有一條專門認得這些欄位的規則。
        [Fact]
        public void R36_SchedulingWallClock_IsNeverTimeZoneConverted()
        {
            // 這些欄位存的是廠別壁鐘。名字改了要同步改這裡——下方的自我檢查會提醒。
            var wallClockFields = new[]
            {
                "ScheduledStart", "ScheduledEnd", "PlannedStartDate", "PlannedEndDate",
            };
            var convert = new Regex(@"ToLocalTime|ToUtcTime", RegexOptions.Compiled);
            var razorComment = new Regex(@"@\*.*?\*@|<!--.*?-->",
                RegexOptions.Compiled | RegexOptions.Singleline);

            var violations = new List<string>();
            var fieldMentions = 0;

            foreach (var file in ConventionScan.Files("Modules", "*.cs")
                                               .Concat(ConventionScan.Files("Modules", "*.cshtml")))
            {
                var isRazor = file.EndsWith(".cshtml", StringComparison.Ordinal);
                var raw = File.ReadAllText(file);
                // 說明「為什麼不可以換算」的註解本來就得把欄位名寫出來，連註解一起掃會誤判。
                // .cshtml 不可套 StripComments（它是 C# 語法的，HTML 文字裡的單引號會吃掉後文）。
                var text = isRazor ? razorComment.Replace(raw, "") : ConventionScan.StripComments(raw);

                foreach (var line in text.Split('\n'))
                {
                    var hit = wallClockFields.FirstOrDefault(f => line.Contains(f, StringComparison.Ordinal));
                    if (hit == null) continue;
                    fieldMentions++;
                    if (!convert.IsMatch(line)) continue;

                    violations.Add(
                        $"{ConventionScan.Rel(file)}: {hit} 被時區換算（{line.Trim()}）" +
                        "——排程時刻的數字已經是廠別當地時間，不換算才是對的");
                }
            }

            // 自我檢查：欄位一改名，這條規則會靜默地永遠綠。撰寫時實測 200 餘處提及。
            Assert.True(fieldMentions >= 50,
                $"R36 只在 {fieldMentions} 行看到排程時刻欄位，遠少於預期——欄位可能已改名，" +
                "此時規則形同關閉。請先修 wallClockFields，不要放過這個訊號");

            AssertRule("R36", violations);
        }

        // R37：ToolKey 是內部識別字，不得當成顯示文字印在畫面上。
        //   報表的顯示名與選單共用同一組鍵（ToolKey ＝ MenuKey，由 ReportToolCatalogTests
        //   守住 1:1），所以顯示名一律是 L[ToolKey]（ProjectZSharedResource）。
        //
        //   ⚠️ 這條規則的由來：同一個缺陷修過一次又復發。_ReportScheduleForm 的下拉
        //   早就改成顯示名了（該檔註解還寫著理由），但 ReportSchedule/Index 的篩選下拉、
        //   以及三張列表的「報表」欄位都還印著 ToolKey——欄頭譯文是「報表／Report」，
        //   內容卻是 "InventoryValuation" 這種內部識別字。四處各自寫、沒有任何機制
        //   看得見它們不一致，所以修完必須留下規則，否則下一個新畫面會再漏一次。
        //
        //   屬性位置（value="@x.ToolKey"、asp-route-toolKey="@x"）是對的，不在管轄範圍：
        //   那是傳值，不是給人看的文字。本規則只認「元素內容位置」。
        [Fact]
        public void R37_ToolKey_IsNeverRenderedAsDisplayText()
        {
            // >@任意運算式.ToolKey< ——元素內容位置，也就是使用者眼睛看到的地方。
            var renderedAsText = new Regex(
                @">\s*@[A-Za-z_][A-Za-z0-9_.]*\.ToolKey\s*<", RegexOptions.Compiled);
            var razorComment = new Regex(@"@\*.*?\*@|<!--.*?-->",
                RegexOptions.Compiled | RegexOptions.Singleline);

            var violations = new List<string>();
            var toolKeyMentions = 0;

            foreach (var file in ConventionScan.Files("Modules", "*.cshtml"))
            {
                var text = razorComment.Replace(File.ReadAllText(file), "");

                foreach (var line in text.Split('\n'))
                {
                    if (!line.Contains("ToolKey", StringComparison.Ordinal)) continue;
                    toolKeyMentions++;
                    if (!renderedAsText.IsMatch(line)) continue;

                    violations.Add(
                        $"{ConventionScan.Rel(file)}: ToolKey 被當成顯示文字（{line.Trim()}）" +
                        "——顯示名請用 L[...ToolKey]（ProjectZSharedResource，鍵＝MenuKey）");
                }
            }

            // 自我檢查：欄位改名或 RPS 畫面重構後，這條規則會靜默地永遠綠。
            // 撰寫時實測 17 行提及（RPS 的五個 View）。
            Assert.True(toolKeyMentions >= 10,
                $"R37 只在 {toolKeyMentions} 行看到 ToolKey，遠少於預期——" +
                "欄位可能已改名或畫面已重構，此時規則形同關閉。請先修規則，不要放過這個訊號");

            AssertRule("R37", violations);
        }

        // R38：樣式**靜默不套用**的兩種形狀。
        //
        //   (a) .cshtml 的 inline style="…"。Program.cs 的 CSP 是 style-src 'self'，
        //       沒有 'unsafe-inline'，而 CSP 的 nonce／hash 對 style **屬性**無效
        //       （只對 <style> 元素有效）。所以寫了就是被瀏覽器丟掉——
        //       不報錯、不告警，只是版面看起來怪怪的。
        //
        //   (b) 用了 u-… class，但那一頁載得到的 stylesheet 裡沒有那一條。
        //       症狀一模一樣，差別只在「是誰把它丟掉」。
        //
        //   兩者都無訊號，這正是它們需要靜態守門的原因：2026-09-18 以 Playwright
        //   走訪 145 頁只量到 8 則 CSP console error，而靜態掃描找到 11 處——
        //   差額是「那一列剛好沒渲染出來」（資料為空、面板沒展開）。
        //   靠跑測試去撞它，會隨資料狀態忽紅忽綠。
        //
        //   ⚠️ (b) 必須**逐頁**判定，不能拿全站 stylesheet 的聯集：
        //   wwwroot/docs/user/mobile/approval.html 用的 u-width-30pct 在
        //   wwwroot/docs/user/index.page.css 裡有定義，但那一頁根本沒載入那支 css。
        //   用聯集掃的第一版正是這樣漏掉它的。
        //
        //   ⚠️ 動態值（style="width:@x%"）不是靠 class 解決，走 site.js 既有的
        //   data-pct-width：CSSOM 屬性設值（el.style.width=）不受 style-src 管制。
        //   本規則不認得那個替代方案好不好用，只認得 style= 有沒有出現。
        //
        //   2026-09-18 建立前先修掉 11 處 inline style ＋ 3 個未定義 class，
        //   故 baseline 是空的（零容忍）。
        [Fact]
        public void R38_Styles_AreNeverSilentlyDropped()
        {
            var violations = new List<string>();

            // ── (a) .cshtml 不得有 inline style 屬性 ──────────────────────────
            var inlineStyle = new Regex(@"\sstyle\s*=\s*""([^""]*)""", RegexOptions.Compiled);
            var razorComment = new Regex(@"@\*.*?\*@|<!--.*?-->",
                RegexOptions.Compiled | RegexOptions.Singleline);
            var scannedViews = 0;

            foreach (var file in AllViews())
            {
                scannedViews++;
                // 註解先剝掉：有些檔案的註解**正在說明這條規則本身**
                // （Views/Plant/_ProjectZPlantParams.cshtml 就寫著「連 style="" 都不可用」），
                // 不剝的話規則會把自己的說明文字判成違規。
                var text = razorComment.Replace(File.ReadAllText(file), "");

                foreach (Match m in inlineStyle.Matches(text))
                    violations.Add(
                        $"R38a {ConventionScan.Rel(file)}: inline style=\"{m.Groups[1].Value}\" "
                        + "會被 CSP（style-src 'self'）整條丟掉。固定值請改用 wwwroot/css/csp-utilities.css "
                        + "的 u-… class；百分比等動態值請用 data-pct-width（site.js 會以 CSSOM 套上去）。");
            }

            // 自我檢查：AllViews() 若因為目錄搬動而掃不到東西，(a) 會靜默地永遠綠。
            Assert.True(scannedViews >= 200,
                $"R38 只掃到 {scannedViews} 個 .cshtml，遠少於預期——"
                + "Views／Modules／Areas 的目錄結構可能已改變，此時規則形同關閉。");

            // ── (b) 每一頁用到的 u- class，那一頁自己載得到 ────────────────────
            var utilityClass = new Regex(@"\bu-[a-z0-9-]+", RegexOptions.Compiled);
            var cssRule = new Regex(@"\.(u-[a-z0-9-]+)\s*[{,:]", RegexOptions.Compiled);
            var linkHref = new Regex(@"<link[^>]+href=""([^""]+\.css)""", RegexOptions.Compiled);
            var root = ConventionScan.RepoRoot();
            var wwwroot = Path.Combine(root, "wwwroot");
            var scannedPages = 0;

            foreach (var page in Directory.EnumerateFiles(wwwroot, "*.html", SearchOption.AllDirectories))
            {
                if (page.Replace('\\', '/').Contains("/lib/", StringComparison.Ordinal)) continue;
                scannedPages++;

                var html = File.ReadAllText(page);
                var dir = Path.GetDirectoryName(page)!;

                var defined = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match link in linkHref.Matches(html))
                {
                    var href = link.Groups[1].Value;
                    if (href.StartsWith("http", StringComparison.Ordinal) ||
                        href.StartsWith("//", StringComparison.Ordinal)) continue;

                    // 站內絕對路徑（/mobile/css/mobile.css）以 wwwroot 為根，
                    // 不是以該頁所在目錄為根——第一版沒分這兩種，把 mobile 全部誤判成未定義。
                    var css = href.StartsWith('/')
                        ? Path.GetFullPath(Path.Combine(wwwroot, href.TrimStart('/')))
                        : Path.GetFullPath(Path.Combine(dir, href));

                    if (!File.Exists(css)) continue;
                    foreach (Match r in cssRule.Matches(File.ReadAllText(css)))
                        defined.Add(r.Groups[1].Value);
                }

                // 同名的 X.page.js 也會加 class 上去，一起算「這一頁用到的」。
                var used = new HashSet<string>(
                    utilityClass.Matches(html).Select(m => m.Value), StringComparer.Ordinal);
                var pageJs = page[..^5] + ".page.js";
                if (File.Exists(pageJs))
                    foreach (Match m in utilityClass.Matches(File.ReadAllText(pageJs)))
                        used.Add(m.Value);

                foreach (var cls in used.Except(defined).OrderBy(x => x, StringComparer.Ordinal))
                    violations.Add(
                        $"R38b {ConventionScan.Rel(page)}: 用了 .{cls}，但這一頁載入的 stylesheet 都沒有定義它"
                        + "——樣式會靜默不套用。請在該頁載得到的 css 補上，或改用已有的 class。");
            }

            Assert.True(scannedPages >= 150,
                $"R38 只掃到 {scannedPages} 個 .html，遠少於預期——wwwroot 的結構可能已改變。");

            // ── (c) .cshtml 用到的 u- class ────────────────────────────────────
            // 這一半對 .cshtml **刻意用聯集**（wwwroot/css/ 底下全部），與 (b) 的逐頁判定不同，
            // 因為兩邊的形狀不一樣：MVC 全站共用 Pages/Shared/_Layout.cshtml，
            // 各 View 額外 <link> 的也都落在同一個 wwwroot/css/ 目錄；
            // 而 partial 自己不宣告 <link>，逐檔判定會把「宿主頁載了、partial 沒宣告」
            // 全部誤判成違規。代價是偏寬鬆：class 定義在 panel-table.css、
            // 而該 View 沒 link 它的情況，這裡看不出來。抓得到的是**整個目錄都沒有**那一條，
            // 而那正是 u-max-height-180px 的形狀（用了五個月，從來沒有人定義過它）。
            var mvcCssDir = Path.Combine(wwwroot, "css");
            var mvcDefined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var css in Directory.EnumerateFiles(mvcCssDir, "*.css"))
                foreach (Match r in cssRule.Matches(File.ReadAllText(css)))
                    mvcDefined.Add(r.Groups[1].Value);

            Assert.True(mvcDefined.Count >= 100,
                $"R38 只從 wwwroot/css 讀到 {mvcDefined.Count} 個 u- class，遠少於預期——"
                + "csp-utilities.css 可能被搬走或清空，此時 (c) 會把每一個用法都判成違規或全部放行。");

            foreach (var file in AllViews())
            {
                var used = utilityClass.Matches(File.ReadAllText(file))
                    .Select(m => m.Value).Distinct(StringComparer.Ordinal);

                foreach (var cls in used.Except(mvcDefined).OrderBy(x => x, StringComparer.Ordinal))
                    violations.Add(
                        $"R38c {ConventionScan.Rel(file)}: 用了 .{cls}，但 wwwroot/css 底下沒有任何一支 css "
                        + "定義它——樣式會靜默不套用。請補進 csp-utilities.css（Core 檔，走 Core-first）。");
            }

            AssertRule("R38", violations);
        }

        // R39：**可授權的選單項（有 Controller 或 Page 的那些），MenuKey 不得重複登錄。**
        //   MenuService.GetFullMenu() 是 SelectMany，不去重；權限以 MenuKey 為鍵，
        //   權限矩陣的表單索引則是「MenuKey → 扁平清單位置」查出來的。
        //   同一個 MenuKey 登錄兩次的後果：
        //     ① 側邊欄出現兩筆一模一樣的連結；
        //     ② 權限矩陣出現兩列，而**兩列共用同一個 permissions[i]**——彼此不獨立，
        //        改一列另一列跟著變，管理員無從察覺哪一列才算數；
        //     ③ RolePermissionSeeder 的 ScreenInfo 清單跟著出現重複項。
        //   全部都是「畫面看起來正常」的形狀，所以非靜態守門不可。
        //
        //   ⚠️ **群組標題（沒有 Controller 也沒有 Page）刻意不在範圍內**。
        //   兩支 contributor 各自宣告同一個群組是既定做法：ProjectZ 的
        //   AdminManagement 是**刻意留空**的群組，用來給 QA 擷取物掛子項，
        //   而 CoreMenuContributor 也有一個同鍵群組。空群組會被
        //   SidebarMenuViewComponent.FilterMenu 丟掉，不會產生上面任何一種後果。
        //   把群組一起管的話，這條規則第一天就得替一個正確的設計開例外。
        //
        //   實例：OutsourceMaterial 在 2026-04-24 初版登錄於庫存群組，
        //   #62「委外管理選單重組」（2026-07-11）又把它加進 OspManagement，
        //   舊的那筆沒移掉，重複了兩個月。發現它的是瀏覽器層的
        //   PermissionMatrixUiTests，但那一層只在 nightly 跑——所以補這條。
        //   零容忍、baseline 為空：重複登錄沒有「既有違規」的正當性。
        [Fact]
        public void R39_GrantableMenuKeys_AreRegisteredOnce()
        {
            var menu = ConventionMenu.Build();

            var flattened = new List<MenuItem>();
            void Flatten(IEnumerable<MenuItem> items)
            {
                foreach (var item in items)
                {
                    flattened.Add(item);
                    if (item.SubItems.Count > 0) Flatten(item.SubItems);
                }
            }
            Flatten(menu);

            // 自我檢查：選單撈空的話，下面的分群永遠是空的，這條規則會永遠是綠的。
            Assert.True(flattened.Count >= 100,
                $"選單只攤平出 {flattened.Count} 項——contributor 沒跑起來，R39 沒有證據力。");

            var grantable = flattened
                .Where(m => !string.IsNullOrEmpty(m.Controller) || !string.IsNullOrEmpty(m.Page))
                .ToList();

            var violations = grantable
                .GroupBy(m => m.MenuKey, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}（登錄 {g.Count()} 次）")
                .ToList();

            AssertRule("R39", violations);
        }

        // R40：手冊搜尋的資產不得**靜默失聯**，兩種形狀合為一條。
        //   (a) 每一頁手冊都要載入 doc-search.css / doc-search.js，且那個相對路徑
        //       真的指得到檔案。手冊有 173 頁、四種目錄深度，注入是批次做的——
        //       路徑寫錯的那幾頁不會報錯，只是搜尋框不出現；而使用者從系統的「?」
        //       進來多半就落在內頁，首頁看得到搜尋框完全不代表內頁看得到。
        //   (b) doc-search.js 用到的 class 必須在 doc-search.css 定義得到。
        //       這是 R38b 的死角：R38b 掃的是 .html 裡的 class，而搜尋 UI 整個
        //       由 JS 動態建立，**沒有任何一個 class 出現在任何一份 .html 裡**。
        //       症狀與 R38b 完全相同（樣式被丟掉、版面怪怪的、不報錯）。
        [Fact]
        public void R40_ManualSearchAssets_AreWiredOnEveryPage()
        {
            var root = ConventionScan.RepoRoot();
            var manualRoot = Path.Combine(root, "wwwroot", "docs", "user");
            Assert.True(Directory.Exists(manualRoot),
                "R40 找不到 wwwroot/docs/user——手冊位置已變，規則形同關閉");

            var violations = new List<string>();
            var scannedPages = 0;

            foreach (var page in Directory.EnumerateFiles(manualRoot, "*.html", SearchOption.AllDirectories))
            {
                scannedPages++;
                var html = File.ReadAllText(page);
                var pageDir = Path.GetDirectoryName(page)!;

                foreach (var (asset, pattern) in new[]
                         {
                             ("doc-search.js", @"<script\s+src=""([^""]*doc-search\.js)"""),
                             ("doc-search.css", @"<link[^>]*href=""([^""]*doc-search\.css)"""),
                         })
                {
                    var m = Regex.Match(html, pattern);
                    if (!m.Success)
                    {
                        violations.Add($"{ConventionScan.Rel(page)}: 沒有載入 {asset}（這一頁不會有搜尋框）");
                        continue;
                    }

                    // 站內絕對路徑以 wwwroot 為根，相對路徑以該頁所在目錄為根——
                    // 不分這兩種的話，會把整批正確的頁面誤判成壞的（同 R38b 的教訓）。
                    var href = m.Groups[1].Value;
                    var resolved = href.StartsWith('/')
                        ? Path.Combine(root, "wwwroot", href.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))
                        : Path.GetFullPath(Path.Combine(pageDir, href.Replace('/', Path.DirectorySeparatorChar)));

                    if (!File.Exists(resolved))
                        violations.Add($"{ConventionScan.Rel(page)}: {asset} 的路徑 \"{href}\" 指不到檔案（靜默 404）");
                }
            }

            // 自我檢查：手冊搬家或副檔名改變時，上面的迴圈一頁都不跑而測試照樣綠。
            Assert.True(scannedPages >= 150,
                $"R40 只掃到 {scannedPages} 頁手冊，遠少於預期（2026-09-22 實測 173）——規則形同關閉");

            var js = Path.Combine(manualRoot, "doc-search.js");
            var css = Path.Combine(manualRoot, "doc-search.css");
            Assert.True(File.Exists(js) && File.Exists(css), "R40 找不到 doc-search.js／doc-search.css");

            var cssText = File.ReadAllText(css);

            // 抓 JS 裡所有 doc-search / doc-search-xxx 識別字，但跳過檔名本身（後面接 . 的）。
            var usedClasses = Regex.Matches(File.ReadAllText(js), @"(?<![\w-])doc-search(?:-[a-z]+)*(?![\w.-])")
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Assert.True(usedClasses.Count >= 8,
                $"R40 只從 doc-search.js 認出 {usedClasses.Count} 個 class——命名慣例已變，(b) 形同關閉");

            foreach (var cls in usedClasses)
                if (!Regex.IsMatch(cssText, @"\." + Regex.Escape(cls) + @"(?![\w-])"))
                    violations.Add($"doc-search.js 用了 .{cls}，但 doc-search.css 沒有定義它（樣式會被靜默丟掉）");

            AssertRule("R40", violations);
        }

        /// <summary>Views／Modules／Areas 底下所有 .cshtml。分頁與操作欄兩條規則共用。</summary>
        private static IEnumerable<string> AllViews() =>
            ConventionScan.Files("Views", "*.cshtml")
                .Concat(ConventionScan.Files("Modules", "*.cshtml"))
                .Concat(ConventionScan.Files("Areas", "*.cshtml"));

        /// <summary>
        /// R41：每個「參照主檔」的實體欄位，都必須在 <c>ReferenceRules</c> 登錄檢查規則或豁免（附理由）。
        ///
        /// 2026-10-02 盤點：110 個實體、300 個參照欄位，伺服器端幾乎都沒有確認下拉送來的值
        /// 「存在、屬本廠、仍啟用」，DB 也幾乎沒有外鍵——代號打錯、別廠的、已停用的都寫得進去，不報錯。
        /// 下拉本身有過濾，所以平常看不出來；缺口在送出的值不受下拉約束（手改表單、頁面停留太久、
        /// 手機 API、送出前主檔剛被停用）。統一機制見 <c>ReferenceValidator</c>，盤點見
        /// <c>.claude/reviews/reference-validation-inventory.md</c>。
        ///
        /// 掃 EF snapshot（同 R21）：只列真的對應到資料表的欄位，ViewModel／DTO 不會混進來。
        /// 欄位名不統一（<c>WhCode</c>／<c>FromWhCode</c>／<c>WhCodeTo</c>／<c>LocCodeFrom</c>…），
        /// 所以用名稱樣式而非精確清單；實體自己的主鍵欄不算參照。
        /// 反向也檢查：規則／豁免指到不存在的欄位（改名、刪欄）即紅，免得登錄表悄悄失效。
        /// baseline 是尚未分批處理的欄位，逐批刪減。
        /// </summary>
        [Fact]
        public void R41_ReferenceFields_AreRegisteredForValidation()
        {
            var refName = new Regex(
                @"^(\w*Product(Code)?|ProdCode(To|From)?|\w*Uom(Code)?|\w*Wh(Code)?(To|From)?|\w*WhCode|\w*WarehouseCode|" +
                @"\w*LocationCode|LocCode(To|From)|\w*CurrencyCode|\w*TaxCode|\w*SupplierCode|\w*CustomerCode|CompanyCode|" +
                @"WorkCenter(Code|Id)|MachineCode|ToolingCode|RoutingId|ShiftGroupCode|Shift\d?Code|AccountCode|" +
                @"Department(Code|Id)|OrganizationUnitId|Container(In|Out)?Code|CategoryCode|InstrumentCode|BankAccountId|" +
                @"SpecCode|DefectCode|PeriodId|ToPlantId|\w*ReasonCode|TradeTermCode|InvoiceTypeCode|SourceTypeCode|" +
                @"SalesRepCode|OperatorId|InspectorId|SalesPersonId|CustodianId)$");

            var snapshot = Path.Combine(ConventionScan.RepoRoot(), "Migrations", "ApplicationDbContextModelSnapshot.cs");
            Assert.True(File.Exists(snapshot), $"R41 找不到 EF snapshot：{snapshot}");

            var mapped = new HashSet<string>(StringComparer.Ordinal);
            var refs = new List<string>();
            foreach (var block in File.ReadAllText(snapshot).Split("modelBuilder.Entity(\"").Skip(1))
            {
                var entity = block[..block.IndexOf('"', StringComparison.Ordinal)];
                var keyMatch = Regex.Match(block, @"b\.HasKey\(([^)]*)\)");
                var keys = keyMatch.Success
                    ? Regex.Matches(keyMatch.Groups[1].Value, "\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet()
                    : new HashSet<string>();

                foreach (Match m in Regex.Matches(block, @"b\.Property<string>\(""(\w+)""\)"))
                {
                    var prop = m.Groups[1].Value;
                    mapped.Add($"{entity}.{prop}");
                    if (!keys.Contains(prop) && refName.IsMatch(prop)) refs.Add($"{entity}.{prop}");
                }
            }

            // 掃描器自我檢查：snapshot 格式或命名樣式一變，就會什麼都抓不到而永遠綠
            Assert.True(refs.Count >= 250, $"R41 只探測到 {refs.Count} 個參照欄位（預期 ≥250）——掃描條件可能已失效");

            static string Name(Type t, string p) => $"{t.FullName}.{p}";
            var ruleNames = SmartFactory.MES.Modules._Shared.ReferenceValidation.ReferenceRules.Rules.Select(r => Name(r.Entity, r.Property)).ToList();
            var exemptNames = SmartFactory.MES.Modules._Shared.ReferenceValidation.ReferenceRules.Exemptions.Select(e => Name(e.Entity, e.Property)).ToList();
            var registered = ruleNames.Concat(exemptNames).ToHashSet(StringComparer.Ordinal);

            var violations = refs.Where(r => !registered.Contains(r))
                .Select(r => r + "：參照欄位未在 ReferenceRules 登錄規則或豁免")
                .ToList();

            // 反向：登錄表不得指到不存在的欄位；同一欄不得既是規則又是豁免
            var stale = registered.Where(n => !mapped.Contains(n)).ToList();
            Assert.True(stale.Count == 0, "R41 ReferenceRules 指到不存在的欄位（改名或刪除？）：\n  " + string.Join("\n  ", stale));
            var both = ruleNames.Intersect(exemptNames).ToList();
            Assert.True(both.Count == 0, "R41 同一欄既是規則又是豁免：\n  " + string.Join("\n  ", both));
            Assert.True(SmartFactory.MES.Modules._Shared.ReferenceValidation.ReferenceRules.Exemptions.All(e => !string.IsNullOrWhiteSpace(e.Reason)),
                "R41 豁免必須寫理由");

            AssertRule("R41", violations);
        }

        private static void AssertRule(string ruleId, IEnumerable<string> violations)
        {
            var (ok, message) = ConventionScan.CheckAgainstBaseline(ruleId, violations);
            Assert.True(ok, message);
        }
    }
}
