using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using BAS = SmartFactory.MES.Modules.BAS.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 排程看板派工側板（Schedule/_DispatchPanel）伺服器端整合守門 —— CI 可無條件執行版。
    ///
    /// 背景：原 <c>ScheduleDispatchPanelInteractionTests</c>（UITests/Playwright）依賴「執行中的 app +
    /// 既有派工資料」，無資料時 self-skip，CI 形同無保護。本測試改走 WebApplicationFactory，
    /// 以 <see cref="TestAuthHandler"/> 自動驗證為種子 admin（廠別 P1 / 角色 1），並自帶完整派工鏈，
    /// 因此 **無需既有資料、無 skip、每次 CI 都實際執行**，守住：
    ///   ① 側板伺服器渲染狀態：data-action 接線齊全（addSplitRow/panelSplit/panelReassign/
    ///      panelUpdatePriority/panelToOutsource）、初始兩列拆分、機台選項 —— 對應 P2 CSP 回補。
    ///   ② 資料正確性：GanttData(/api/mfg/schedule) 回傳本廠別已排程派工。
    ///   ③ 操作正確性：真正的 /Dispatch/Split 後端（含 AntiForgery + 權限閘）—— 列數不足回驗證錯誤、
    ///      合法兩段拆分守恆數量並建立子派工。
    ///
    /// 瀏覽器端 JS DOM 行為（點 addSplitRow 真的長出一列）仍由 UITests 在本地當輔助網守。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ScheduleDispatchPanelIntegrationTests
    {
        private const string ReadDispatchId  = "UITEST-D-PANEL"; // 唯讀（側板/Gantt/驗證）共用，不被變更
        private const string SplitDispatchId = "UITEST-D-SPLIT"; // 拆分快樂路徑專用，每次重建
        private const string Plant   = "P1";
        private const string RoleId  = "1";
        private const string WcCode  = "UITEST-WC";
        private const string M1 = "UITEST-M1";
        private const string M2 = "UITEST-M2";

        private readonly AuthedScheduleFactory _factory;

        public ScheduleDispatchPanelIntegrationTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Seed();
        }

        // ── ① 側板伺服器渲染：data-action 接線 + 兩列拆分 + 機台選項 ────────────────
        [Fact]
        public async Task DispatchPanel_RendersAdjustWiring_AndTwoSplitRows()
        {
            var client = _factory.CreateClient();

            var resp = await client.GetAsync($"/Schedule/DispatchPanel/{ReadDispatchId}");
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();

            // CSP 回補後所有面板動作必須走 data-action 委派（不得退回 inline on*=）
            Assert.Contains("data-action=\"addSplitRow\"", html);
            Assert.Contains("data-action=\"panelSplit\"", html);
            Assert.Contains("data-action=\"panelReassign\"", html);
            Assert.Contains("data-action=\"panelUpdatePriority\"", html);
            Assert.Contains("data-action=\"panelToOutsource\"", html);

            // 初始兩列拆分（removeSplitRow 由前端動態列才注入，伺服器初始 HTML 不含 → 不在此斷言）
            Assert.Equal(2, Regex.Matches(html, "class=\"[^\"]*\\bsplit-row\\b").Count);

            // 同工作中心機台選項齊全
            Assert.Contains($">{M1}</option>", html);
            Assert.Contains($">{M2}</option>", html);
        }

        // ── ② 側板不存在 → 404 ───────────────────────────────────────────────────
        [Fact]
        public async Task DispatchPanel_NonExistentId_Returns404()
        {
            var client = _factory.CreateClient();
            var resp = await client.GetAsync("/Schedule/DispatchPanel/NO-SUCH-DISPATCH");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
        }

        // ── ③ GanttData 回傳本廠別已排程派工（資料正確性）────────────────────────
        [Fact]
        public async Task GanttData_IncludesSeededDispatch()
        {
            var client = _factory.CreateClient();
            var resp = await client.GetAsync($"/api/mfg/schedule?plantId={Plant}");
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

            var json = await resp.Content.ReadAsStringAsync();
            Assert.Contains(ReadDispatchId, json);
        }

        // ── ③a 甘特 payload 的著色契約：派工 bar 必須帶 cat（穩定代碼）與 batch ────
        //
        // 畫面的顏色查表是以 cat 為鍵、批號著色是以 batch 為鍵（Schedule/Index.cshtml 的
        // buildColorMap）。後端少送任一個，畫面不會拋錯——只會整片退回中性色、或切到批號
        // 著色時什麼都不變，是**靜默失效**，所以在伺服器端把欄位釘住。
        [Fact]
        public async Task GanttData_DispatchSegments_CarryColourCategoryAndBatch()
        {
            var client = _factory.CreateClient();
            var json = await client.GetStringAsync($"/api/mfg/schedule?plantId={Plant}");

            using var doc = JsonDocument.Parse(json);
            JsonElement? seg = null;
            foreach (var group in doc.RootElement.EnumerateArray())
            foreach (var series in group.GetProperty("data").EnumerateArray())
            foreach (var s in series.GetProperty("data").EnumerateArray())
                if (s.TryGetProperty("ww", out var ww) && ww.GetString() == ReadDispatchId)
                    seg = s.Clone();

            Assert.True(seg.HasValue, $"甘特資料中找不到派工 {ReadDispatchId}");

            Assert.True(seg!.Value.TryGetProperty("cat", out var cat),
                "派工 segment 缺 cat——畫面色表以 cat 為鍵，缺了會整片退回中性色（不拋錯）");
            Assert.False(string.IsNullOrWhiteSpace(cat.GetString()));

            Assert.True(seg!.Value.TryGetProperty("batch", out var batch),
                "派工 segment 缺 batch——「著色依據」切到批號時會無事發生（不拋錯）");
            Assert.False(string.IsNullOrWhiteSpace(batch.GetString()));

            // col 是舊的死欄位（TimelinesChart 只吃 zColorScale(val)，逐 segment 的 col 從未生效）。
            // 留著會讓人以為改它就能改色，已移除；這裡守住不要被改回來。
            Assert.False(seg!.Value.TryGetProperty("col", out _),
                "col 欄位又出現了——它不會影響顏色，留著只會誤導後人改錯地方");
        }

        // ── ③b 生產中跨日派工必須出現在甘特（重疊視窗，非僅起點在視窗內）─────────
        // 昨天開工、現在仍 InProgress 的派工 ScheduledStart < 今天：舊查詢只認
        // 「起點落在視窗內」會整條消失，正在佔用機台的工作在甘特上看不到。
        [Fact]
        public async Task GanttData_IncludesInProgressDispatchStartedBeforeToday()
        {
            const string runningId = "UITEST-D-RUNNING";
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == "UITEST-WO" && o.OperationSeq == 30))
                    db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                    {
                        WorkOrderId = "UITEST-WO", OperationSeq = 30, WorkCenterId = WcCode,
                        UnitTime = 1, BatchQty = 1, OperationName = "UITEST OP30",
                    });
                var stale = db.Dispatches.IgnoreQueryFilters()
                    .Where(d => d.DispatchId == runningId).ToList();
                if (stale.Count > 0) { db.Dispatches.RemoveRange(stale); db.SaveChanges(); }
                db.Dispatches.Add(new MfgM.Dispatch
                {
                    DispatchId = runningId, WorkOrderId = "UITEST-WO", OperationSeq = 30,
                    Quantity = 10, Status = MfgM.DispatchStatus.InProgress,
                    MachineCode = M1,
                    ScheduledStart = DateTime.UtcNow.AddDays(-1),   // 昨天開工
                    ScheduledEnd   = DateTime.UtcNow.AddHours(3),   // 尚未（該）結束
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.SaveChanges();
            }

            var client = _factory.CreateClient();
            var json = await client.GetStringAsync($"/api/mfg/schedule?plantId={Plant}");
            Assert.Contains(runningId, json);
        }

        // ── ④ Split 列數不足 → 回驗證錯誤（操作正確性，後端 SplitMinTwo 閘）────────
        [Fact]
        public async Task Split_InsufficientRows_ReturnsValidationFailure()
        {
            var client = _factory.CreateClient();
            var token = await HarvestAntiForgeryTokenAsync(client);

            // 只給一列 → 後端 splits.Count < 2 → success=false
            var res = await PostSplitAsync(client, ReadDispatchId, token,
                new[] { new { qty = 10m, machineCode = M1 } });

            Assert.False(res.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(res.GetProperty("message").GetString()));
        }

        // ── ⑤ Split 合法兩段：數量守恆 + 建立子派工（操作正確性，後端真實拆分）──────
        //
        // 守住 MFG 拆分缺陷修復：工序↔派工已改 1:N（migration MFG_DispatchOperationOneToMany 卸除
        // IX_Dispatches_WorkOrderId_OperationSeq 唯一性），同工序兄弟派工可共存。本案在真 PG 上驗證
        // 後端真實拆分：原派工降量轉 Pending、建立子派工、數量守恆。
        [Fact]
        public async Task Split_TwoWay_SplitsAndConservesQuantity()
        {
            var client = _factory.CreateClient();
            var token = await HarvestAntiForgeryTokenAsync(client);

            // 原派工量 10 → 拆 6 + 4（加總守恆）
            var res = await PostSplitAsync(client, SplitDispatchId, token,
                new[]
                {
                    new { qty = 6m, machineCode = M1 },
                    new { qty = 4m, machineCode = M2 },
                });

            Assert.True(res.GetProperty("success").GetBoolean(),
                $"拆分應成功，實得訊息：{res.GetProperty("message").GetString()}");

            // 驗證 DB：原派工降為 6（轉 Pending），子派工 -s1 = 4
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var original = await db.Dispatches.IgnoreQueryFilters()
                .FirstAsync(d => d.DispatchId == SplitDispatchId);
            var child = await db.Dispatches.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.DispatchId == $"{SplitDispatchId}-s1");

            Assert.Equal(6m, original.Quantity);
            Assert.Equal(MfgM.DispatchStatus.Pending, original.Status);
            Assert.NotNull(child);
            Assert.Equal(4m, child!.Quantity);
            Assert.Equal(SplitDispatchId, child.SplitGroupId);
        }

        // ── ⑥ Split 重複拆分同一原派工：子派工編號續編不撞主鍵（操作正確性）─────────
        //
        // 缺陷：子派工 id 為 {原派工Id}-s{i} 固定從 1 起編，但拆分後原派工仍為 Pending、可再次拆分，
        // 第二次又產生 -s1 → 撞 Dispatches 主鍵（真 PG 500）。修法：續編時跳過已存在序號。
        // 守門：拆 10→6+4 後，再拆原派工 6→3+3，群組數量恆為 10 且子派工 id 全不重複。
        [Fact]
        public async Task Split_Resplit_ContinuesChildNumbering_NoPkCollision()
        {
            var client = _factory.CreateClient();
            var token = await HarvestAntiForgeryTokenAsync(client);

            // 第一次：10 → 6 + 4（產生子派工 -s1）
            var r1 = await PostSplitAsync(client, SplitDispatchId, token,
                new[]
                {
                    new { qty = 6m, machineCode = M1 },
                    new { qty = 4m, machineCode = M2 },
                });
            Assert.True(r1.GetProperty("success").GetBoolean(),
                $"首次拆分應成功，實得：{r1.GetProperty("message").GetString()}");

            // 原派工現為 6（Pending）→ 再次拆分 6 → 3 + 3
            // 舊版會再產生 -s1 撞主鍵；修正後應續編 -s2。
            var r2 = await PostSplitAsync(client, SplitDispatchId, token,
                new[]
                {
                    new { qty = 3m, machineCode = M1 },
                    new { qty = 3m, machineCode = M2 },
                });
            Assert.True(r2.GetProperty("success").GetBoolean(),
                $"重複拆分應成功（子派工序號需續編避免撞主鍵），實得：{r2.GetProperty("message").GetString()}");

            // 驗證 DB：整個拆分群組數量守恆 = 原始 10，且子派工 id 全不重複
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var group = await db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.SplitGroupId == SplitDispatchId).ToListAsync();

            Assert.Equal(10m, group.Sum(d => d.Quantity));
            Assert.Equal(group.Count, group.Select(d => d.DispatchId).Distinct().Count());
            Assert.Equal(3, group.Count); // 原派工 + 兩個子派工（-s1 保留、-s2 續編）
        }

        // ── ⑦ 換機台：拒絕不存在/廢棄機台、接受合法機台（操作正確性，允許跨工作中心）──
        //
        // 缺陷：ReassignMachine 原本不驗證 newMachineCode，可寫入任意垃圾代碼汙染派工。
        // 業務規則允許跨工作中心調機，故只驗證「機台存在且非 Retired」。
        [Fact]
        public async Task ReassignMachine_RejectsUnknownMachine_AcceptsValid()
        {
            var client = _factory.CreateClient();
            var token = await HarvestAntiForgeryTokenAsync(client);

            // 不存在機台 → 拒絕，DB 機台不變
            var bad = await PostFormAsync(client, "/Dispatch/ReassignMachine", token,
                new() { ["id"] = ReadDispatchId, ["newMachineCode"] = "NO-SUCH-MACHINE" });
            Assert.False(bad.GetProperty("success").GetBoolean());

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var d = await db.Dispatches.IgnoreQueryFilters().FirstAsync(x => x.DispatchId == ReadDispatchId);
                Assert.Equal(M1, d.MachineCode); // 未被改成垃圾值
            }

            // 合法機台 M2 → 成功，DB 機台改為 M2、狀態轉 Pending 待重排
            var ok = await PostFormAsync(client, "/Dispatch/ReassignMachine", token,
                new() { ["id"] = ReadDispatchId, ["newMachineCode"] = M2 });
            Assert.True(ok.GetProperty("success").GetBoolean(),
                $"合法換機台應成功，實得：{ok.GetProperty("message").GetString()}");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var d = await db.Dispatches.IgnoreQueryFilters().FirstAsync(x => x.DispatchId == ReadDispatchId);
                Assert.Equal(M2, d.MachineCode);
                Assert.Equal(MfgM.DispatchStatus.Pending, d.Status);
            }
        }

        private static async Task<JsonElement> PostFormAsync(
            HttpClient client, string url, string token, Dictionary<string, string> form)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode,
                $"{url} 回應非 2xx（{(int)resp.StatusCode}）：{body.Substring(0, Math.Min(body.Length, 400))}");
            return JsonSerializer.Deserialize<JsonElement>(body);
        }

        // ── 共用：AntiForgery token 取得（GET /Schedule 渲染 @Html.AntiForgeryToken）──
        private static async Task<string> HarvestAntiForgeryTokenAsync(HttpClient client)
        {
            var html = await client.GetStringAsync("/Schedule");
            var m = Regex.Match(html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能從 /Schedule 取得 AntiForgery token");
            return m.Groups[1].Value;
        }

        private static async Task<JsonElement> PostSplitAsync(
            HttpClient client, string dispatchId, string token, object splits)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"/Dispatch/Split?id={Uri.EscapeDataString(dispatchId)}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(splits), System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("RequestVerificationToken", token);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode,
                $"/Dispatch/Split 回應非 2xx（{(int)resp.StatusCode}）：{body.Substring(0, Math.Min(body.Length, 400))}");
            return JsonSerializer.Deserialize<JsonElement>(body);
        }

        // ── 種子：自帶派工鏈（廠別 P1 / 角色 1）+ Dispatch.CanUpdate 權限 ─────────────
        //   讀取類用 ReadDispatchId（不變更）；拆分類用 SplitDispatchId（每次重建以利重跑）。
        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // bare scope 無 HttpContext → 全域過濾會隱藏 IDataAuthEntity；存在性檢查一律 IgnoreQueryFilters。
            // 角色 1（Admin，無 RolePath）→ DataAuth 過濾要求 CreatedByRoleId == '1'，故顯式賦值。
            var now = DateTime.UtcNow;

            // 種子 admin 的 MustChangePassword / TwoFactorEnabled 前置已上移至
            // AuthedScheduleFactory.BootstrapSeededAdmin()——本 collection 尚有 21 個 Postings
            // 測試類別共用同一工廠卻跑不到這裡，留在此處等於讓它們依賴類別執行順序。

            if (!db.WorkCenters.IgnoreQueryFilters().Any(w => w.WorkCenterCode == WcCode))
            {
                db.WorkCenters.Add(new BAS.WorkCenter
                {
                    WorkCenterCode = WcCode, WorkCenterName = "UITEST WC",
                    IsActive = true, PlantId = Plant,
                });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine
                { WorkCenterCode = WcCode, MachineCode = M1, MachineName = "UITEST 機台1" });
                db.WorkCenterMachines.Add(new BAS.WorkCenterMachine
                { WorkCenterCode = WcCode, MachineCode = M2, MachineName = "UITEST 機台2" });
            }

            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == "UITEST-B"))
            {
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = "UITEST-B", MrpRunId = "UITEST-MRP", MrpRunLineSeq = 1,
                    ProductCode = "UITEST-P", Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }
            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == "UITEST-WO"))
            {
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = "UITEST-WO", ProductionBatchId = "UITEST-B",
                    ProductCode = "UITEST-P", RoutingId = "UITEST-R", Quantity = 10,
                    Status = MfgM.WorkOrderStatus.InProgress,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });
            }

            // 兩個獨立工序（idempotent per-seq）：唯讀派工掛 OpSeq 10，拆分派工掛 OpSeq 20，
            // 避免兩派工撞 Dispatch↔Operation 1:1 唯一索引 (WorkOrderId, OperationSeq)。
            foreach (var seq in new[] { 10, 20 })
            {
                if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == "UITEST-WO" && o.OperationSeq == seq))
                    db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                    {
                        WorkOrderId = "UITEST-WO", OperationSeq = seq, WorkCenterId = WcCode,
                        UnitTime = 1, BatchQty = 1, OperationName = $"UITEST OP{seq}",
                    });
            }

            // Dispatch.CanUpdate 權限（CanUpdate 無 Admin 旁路，需顯式授權列）
            if (!db.MenuPermissions.Any(p => p.RoleId == RoleId && p.MenuKey == "Dispatch"))
            {
                db.MenuPermissions.Add(new MenuPermission
                {
                    RoleId = RoleId, MenuKey = "Dispatch",
                    CanRead = true, CanCreate = true, CanUpdate = true, CanDelete = true,
                });
            }

            db.SaveChanges();

            // 派工：每次清空本 WO 全部派工後重建，確保不受上輪殘留/拆分子派工干擾（可重跑）。
            var stale = db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.WorkOrderId == "UITEST-WO").ToList();
            if (stale.Count > 0) { db.Dispatches.RemoveRange(stale); db.SaveChanges(); }

            // 唯讀派工(OpSeq 10)：已排程（CanAdjust）、ScheduledStart 落在 Gantt 視窗（now..+14d）
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = ReadDispatchId, WorkOrderId = "UITEST-WO", OperationSeq = 10,
                Quantity = 10, Status = MfgM.DispatchStatus.Scheduled,
                MachineCode = M1, ScheduledStart = now.AddHours(1), ScheduledEnd = now.AddHours(2),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            // 拆分專用派工(OpSeq 20)
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = SplitDispatchId, WorkOrderId = "UITEST-WO", OperationSeq = 20,
                Quantity = 10, Status = MfgM.DispatchStatus.Scheduled,
                MachineCode = M1, ScheduledStart = now.AddHours(1), ScheduledEnd = now.AddHours(2),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }
    }

    /// <summary>
    /// 測試用驗證處理器：將每個請求驗證為種子 admin（NameIdentifier=admin.Id）。
    /// IUserContext 依此 claim 自 DB 解析 admin → 廠別 P1 / 角色 1，繞過登入 UI 與 MustChangePassword。
    /// </summary>
    public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestScheme";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userManager = Context.RequestServices
                .GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await userManager.FindByNameAsync("admin");
            if (admin == null) return AuthenticateResult.NoResult();

            // 角色 claim 取自 Identity 的實際授予，不是憑空塞一個字串——
            // `[Authorize(Roles = "…")]` 的端點（PlantController、SystemParamController）
            // 沒有這一段就一律 403，而且是「以錯的理由失敗」：看起來像守門生效，
            // 其實是測試身分根本沒帶角色。
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, admin.Id),
                new(ClaimTypes.Name, admin.UserName ?? "admin"),
            };
            foreach (var role in await userManager.GetRolesAsync(admin))
                claims.Add(new Claim(ClaimTypes.Role, role));
            var identity  = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket    = new AuthenticationTicket(principal, Scheme.Name);
            return AuthenticateResult.Success(ticket);
        }
    }

    /// <summary>
    /// 在共用整合 Host 上疊加 TestAuthHandler 為預設驗證 scheme（僅本 collection 使用，
    /// 不影響 "Integration" collection 的未登入授權守門測試）。
    ///
    /// 同時負責本 collection 的 **admin 登入前置**（見 <see cref="BootstrapSeededAdmin"/>）：
    /// 該前置原本寫在 <see cref="ScheduleDispatchPanelIntegrationTests"/> 的 Seed() 裡，
    /// 但 collection 內另有 21 個 Postings 測試類別共用這個工廠卻跑不到那段種子——
    /// 於是「旗標有沒有被清掉」取決於 xUnit 的類別執行順序。本機測試庫是重跑庫、
    /// 旗標早被前幾輪清掉並留在 DB，所以恆綠；CI 乾淨庫上只要 Postings 排在前面，
    /// 全部 POST 就被強制改密中介 302 走，實測 101 支失敗。
    /// 提到工廠層級後，此前置保證在 collection 任何測試類別建構之前只跑一次。
    /// </summary>
    public class AuthedScheduleFactory : TestWebApplicationFactory
    {
        /// <summary>
        /// 於 Host 建好（含 Program.cs 啟動種子已跑完）之後、任何測試取得 client 之前，
        /// 一次性解除種子 admin 的登入前置關卡。
        /// </summary>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            BootstrapSeededAdmin(host.Services);
            return host;
        }

        /// <summary>
        /// 解除種子 admin 的兩道登入前置關卡，否則本 collection 的請求根本到不了受測端點：
        ///   ① <c>MustChangePassword=true</c> → 強制改密中介把任何請求 302 導向變更密碼頁。
        ///   ② <c>TwoFactorEnabled=false</c> → M3 強制 2FA 中介把 Admin 導向 /TwoFactor/Setup。
        ///
        /// 只動測試 DB 的 admin 這一列，不影響生產種子的強制啟用政策。
        ///
        /// ⚠️ 這兩個旗標被中介攔截後回的是 200 + 變更密碼頁面，不是 4xx；
        /// 「預期被擋」型的測試會因此**以錯的理由通過**（擋它的是中介而非 Controller 守門）。
        /// 故驗證此類修正一律要用乾淨 DB，重跑庫看不出差別。
        /// </summary>
        internal static void BootstrapSeededAdmin(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var seededAdmin = db.Users.FirstOrDefault(u => u.UserName == "admin");
            if (seededAdmin != null && (seededAdmin.MustChangePassword || !seededAdmin.TwoFactorEnabled))
            {
                seededAdmin.MustChangePassword = false;
                seededAdmin.TwoFactorEnabled = true;
                db.SaveChanges();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });

                // PostConfigure 在 Identity 設定 application cookie 為預設 scheme 之後執行，
                // 強制把預設驗證/挑戰 scheme 改為 TestScheme（否則 [Authorize] 仍會 302 導向登入）。
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme    = TestAuthHandler.SchemeName;
                    o.DefaultScheme             = TestAuthHandler.SchemeName;
                });
            });
        }
    }

    [CollectionDefinition("ScheduleIntegration")]
    public class ScheduleIntegrationCollection : ICollectionFixture<AuthedScheduleFactory> { }
}
