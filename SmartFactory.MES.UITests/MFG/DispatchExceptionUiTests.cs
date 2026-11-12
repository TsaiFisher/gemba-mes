using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 派工的例外操作與工單撤回（畫面層）。
    ///
    /// <para><b>為什麼要有這一支</b>：派工的正常路徑（備料→換模→上下工→報工）有
    /// <see cref="Flows.StagingToCompletionFlowTests"/> 走過，但 #230／#231 加進來的例外操作——
    /// 暫停處理（恢復／換機續做／剩餘量結案）、排程側板（拆分／轉委外）——之前沒有任何 UI 測試點過。
    /// 這些是現場出事時才會按的按鈕，平常沒人按，壞了也最晚被發現。</para>
    ///
    /// <para>工單「撤回」就是這樣壞掉的：<c>_Details</c> 寫 <c>typeof postAction === 'function'</c>
    /// 才呼叫，但 crud-panel.js 的 postAction 從沒掛到 window，按下去什麼都不會發生、也沒有錯誤。
    /// <see cref="工單待審核_明細面板按撤回_退回已規劃與草稿"/> 守這個。</para>
    ///
    /// <para>資料：scripts/seed-e2e-fixture.sql ⑯——工作中心 E2E-WC02 與機台 MC01（正常）／MC02（正常）／
    /// MC03（故障），工單 E2E-WO0021 下的派工 DP0021～DP0025，待審核工單 E2E-WO0022。
    /// 每支測試開頭重跑種子，子派工（-s1…）也會被清掉。</para>
    /// </summary>
    public class DispatchExceptionUiTests : FlowTestBase
    {
        // DispatchStatus（Modules/MFG/Models/Dispatch.cs）
        private const int Pending = 0, InProgress = 4, Closed = 5, OnHold = 6, Cancelled = 8;

        public DispatchExceptionUiTests(UiTestFixture fixture) : base(fixture) { }

        // ── 暫停處理 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 巡檢不合格暫停_按恢復_回到暫停前的生產中()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await OpenHoldFormAsync(page, "E2E-DP0021");
            await SubmitAndExpectSuccessAsync(page, "#resumeForm", "/Dispatch/Resume", "恢復");

            Assert.Equal(InProgress, await StatusAsync("E2E-DP0021"));
            Assert.Null(await SeedFixture.ScalarAsync<int?>(
                "SELECT \"HoldReason\" FROM \"Dispatches\" WHERE \"DispatchId\" = 'E2E-DP0021'"));
            AssertNoJsErrors();
        }

        [Fact]
        public async Task 機台故障暫停_不可恢復_換機續做_原單以已完成量結案並開續做派工鎖定新機台()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await OpenHoldFormAsync(page, "E2E-DP0022");

            // 機台仍故障：恢復表單不該出現（後端同樣會擋，這裡守的是畫面不給一顆注定失敗的按鈕）
            Assert.Equal(0, await page.Locator("#sidePanelBody #resumeForm").CountAsync());
            var options = await page.Locator("#sidePanelBody #switchForm select[name='newMachineCode'] option")
                .EvaluateAllAsync<string[]>("els => els.map(e => e.value)");
            Assert.Contains("E2E-MC02", options);
            Assert.DoesNotContain("E2E-MC03", options);   // 故障中的機台與原機台不可選

            await page.SelectOptionAsync("#sidePanelBody #switchForm select[name='newMachineCode']", "E2E-MC02");
            await SubmitAndExpectSuccessAsync(page, "#switchForm", "/Dispatch/SwitchMachine", "換機續做");

            Assert.Equal(Closed, await StatusAsync("E2E-DP0022"));
            Assert.Equal(4m, await SeedFixture.ScalarAsync<decimal>(
                "SELECT \"Quantity\" FROM \"Dispatches\" WHERE \"DispatchId\" = 'E2E-DP0022'"));

            var continuation = await SeedFixture.ScalarAsync<string>(
                "SELECT \"DispatchId\" || '|' || \"Quantity\"::numeric(18,0) || '|' || \"MachineCode\" || '|' || \"IsMachineLocked\" || '|' || \"Status\" "
                + "FROM \"Dispatches\" WHERE \"DispatchId\" LIKE 'E2E-DP0022-%'");
            Assert.False(string.IsNullOrEmpty(continuation), "換機續做後應有一張續做派工（E2E-DP0022-…），實際沒有。");
            var parts = continuation!.Split('|');
            Assert.Equal("6", parts[1]);              // 剩餘量 10 − 4
            Assert.Equal("E2E-MC02", parts[2]);
            Assert.Equal("true", parts[3]);           // 人指定的機台，重排不得改掉
            Assert.Equal(Pending.ToString(), parts[4]);
            AssertNoJsErrors();
        }

        [Fact]
        public async Task 沒有產出的暫停派工_剩餘量結案_轉為取消()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await OpenHoldFormAsync(page, "E2E-DP0023");
            await SubmitAndExpectSuccessAsync(page, "#shortCloseForm", "/Dispatch/ShortClose", "剩餘量結案");

            Assert.Equal(Cancelled, await StatusAsync("E2E-DP0023"));
            // 工單還有其他未結束的派工，不得被帶著完工（種子為已釋出 1）
            Assert.Equal(1, await SeedFixture.ScalarAsync<int>(
                "SELECT \"Status\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0021'"));
            AssertNoJsErrors();
        }

        // ── 排程側板 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 排程側板_已排定派工拆成兩張_數量加總等於原量()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await OpenSchedulePanelAsync(page, "E2E-DP0024");

            var qtys = page.Locator("#dispatchPanelBody #splitRows .split-qty");
            await qtys.Nth(0).FillAsync("6");
            await qtys.Nth(1).FillAsync("4");
            var json = await ClickAndCaptureJsonAsync(page, "#dispatchPanelBody [data-action='panelSplit']", "/Dispatch/Split");
            AssertSuccess(json, "拆分");

            Assert.Equal(6m, await SeedFixture.ScalarAsync<decimal>(
                "SELECT \"Quantity\" FROM \"Dispatches\" WHERE \"DispatchId\" = 'E2E-DP0024'"));
            Assert.Equal(4m, await SeedFixture.ScalarAsync<decimal>(
                "SELECT COALESCE(SUM(\"Quantity\"),0) FROM \"Dispatches\" WHERE \"DispatchId\" LIKE 'E2E-DP0024-s%'"));
            Assert.Equal(1, await SeedFixture.ScalarAsync<int>(
                "SELECT count(*) FROM \"Dispatches\" WHERE \"DispatchId\" LIKE 'E2E-DP0024-s%' AND \"SplitGroupId\" = 'E2E-DP0024'"));
            AssertNoJsErrors();
        }

        [Fact]
        public async Task 排程側板_已排定派工轉委外_釋放機台時段()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await OpenSchedulePanelAsync(page, "E2E-DP0025");

            var json = await ClickAndCaptureJsonAsync(page, "#dispatchPanelBody [data-action='panelToOutsource']", "/Dispatch/ToOutsource");
            AssertSuccess(json, "轉委外");

            Assert.True(await SeedFixture.ScalarAsync<bool>(
                "SELECT \"IsOutsource\" FROM \"Dispatches\" WHERE \"DispatchId\" = 'E2E-DP0025'"),
                "轉委外成功後派工的 IsOutsource 應為 true。");
            Assert.Equal(Pending, await StatusAsync("E2E-DP0025"));
            Assert.Null(await SeedFixture.ScalarAsync<string>(
                "SELECT \"MachineCode\" FROM \"Dispatches\" WHERE \"DispatchId\" = 'E2E-DP0025'"));
            AssertNoJsErrors();
        }

        // ── 工單撤回 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 工單待審核_明細面板按撤回_退回已規劃與草稿()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await GotoAsync(page, "/WorkOrder?searched=true&searchText=E2E-WO0022&pageSize=50");
            await OpenRowPanelAsync(page, "E2E-WO0022", "/WorkOrder/Details", "#sidePanelBody [data-action='withdrawWorkOrder']");

            var json = await ClickAndCaptureJsonAsync(page,
                "#sidePanelBody [data-action='withdrawWorkOrder']", "/WorkOrder/Withdraw",
                "按下撤回後沒有發出 POST /WorkOrder/Withdraw——按鈕沒有任何作用"
                + "（曾因 typeof postAction 守衛恆為 false 而靜默失效）");
            AssertSuccess(json, "撤回");

            Assert.Equal(0, await SeedFixture.ScalarAsync<int>(
                "SELECT \"ApprovalStatus\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0022'"));
            Assert.Equal(0, await SeedFixture.ScalarAsync<int>(
                "SELECT \"Status\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0022'"));
            AssertNoJsErrors();
        }

        // ── 工單作廢 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 工單沒有產出_明細面板按作廢_工單作廢()
        {
            // 原本工單畫面沒有任何作廢入口（端點在、手冊叫人「改按作廢」），2026-10-15 補按鈕。
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await GotoAsync(page, "/WorkOrder?searched=true&searchText=E2E-WO0022&pageSize=50");
            await OpenRowPanelAsync(page, "E2E-WO0022", "/WorkOrder/Details", "#sidePanelBody [data-action='voidWorkOrder']");
            var json = await ClickAndCaptureJsonAsync(page, "#sidePanelBody [data-action='voidWorkOrder']", "/WorkOrder/Void");
            AssertSuccess(json, "作廢");

            Assert.Equal(5, await SeedFixture.ScalarAsync<int>(
                "SELECT \"Status\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0022'"));
            AssertNoJsErrors();
        }

        [Fact]
        public async Task 工單已有產出_按作廢被擋並說明_狀態不變()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            // E2E-WO0021 的派工 DP0021／DP0022 已有完成量
            await GotoAsync(page, "/WorkOrder?searched=true&searchText=E2E-WO0021&pageSize=50");
            await OpenRowPanelAsync(page, "E2E-WO0021", "/WorkOrder/Details", "#sidePanelBody [data-action='voidWorkOrder']");
            var json = await ClickAndCaptureJsonAsync(page, "#sidePanelBody [data-action='voidWorkOrder']", "/WorkOrder/Void");

            Assert.False(json.GetProperty("success").GetBoolean(), $"已有產出的工單不得作廢，後端卻回成功：{json}");
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()), "擋下時要說明原因。");
            Assert.Contains(DialogMessages, m => m == json.GetProperty("message").GetString());   // 畫面有把原因秀出來
            Assert.Equal(1, await SeedFixture.ScalarAsync<int>(
                "SELECT \"Status\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0021'"));
            AssertNoJsErrors();
        }

        // ════════════════════════════════════════════════════════════════════

        private async Task OpenHoldFormAsync(IPage page, string dispatchId)
        {
            Assert.Equal(OnHold, await StatusAsync(dispatchId));
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={dispatchId}&pageSize=50");
            await OpenRowPanelAsync(page, dispatchId, "HoldForm", "#sidePanelBody #shortCloseForm");
        }

        /// <summary>
        /// 開排程頁的派工側板：點甘特圖上該派工的色塊（onSegmentClick → openDispatchPanel）。
        ///
        /// <para>色塊位置隨排程結果與時間軸縮放而變，點座標會讓測試跟著資料漂；改以 d3 綁在色塊上的資料
        /// （<c>__data__.data.ww</c> ＝ 派工號，與 segExtras 讀的是同一份）找到那一塊，再對它送 click 事件——
        /// 走的仍是正式的點擊處理器，不是繞過它直接呼叫側板函式（那個函式也不在全域）。</para>
        /// </summary>
        private async Task OpenSchedulePanelAsync(IPage page, string dispatchId)
        {
            await GotoAsync(page, "/Schedule");

            string[] seen = Array.Empty<string>();
            var clicked = false;
            for (var i = 0; i < 20 && !clicked; i++)   // 甘特由 fetch 非同步載入，最多等 10 秒
            {
                var result = await page.EvaluateAsync<JsonElement>(@"id => {
                    const segs = [...document.querySelectorAll('svg rect')].filter(r => r.__data__ && r.__data__.data);
                    const el = segs.find(r => r.__data__.data.ww === id);
                    if (el) el.dispatchEvent(new MouseEvent('click', { bubbles: true }));
                    return { clicked: !!el, seen: segs.map(r => String(r.__data__.data.ww)) };
                }", dispatchId);
                clicked = result.GetProperty("clicked").GetBoolean();
                seen = result.GetProperty("seen").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                if (!clicked) await page.WaitForTimeoutAsync(500);
            }
            Assert.True(clicked,
                $"甘特圖上找不到派工 {dispatchId} 的色塊（目前有 {seen.Length} 塊：{string.Join(", ", seen.Distinct().Take(20))}）。");


            await page.WaitForSelectorAsync("#dispatchPanelBody [data-action='panelToOutsource']",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
        }

        /// <summary>暫停處理側板的三張表單各自以 submitPanel 送出；攔截回應斷言成功。</summary>
        private async Task SubmitAndExpectSuccessAsync(IPage page, string formSelector, string urlFragment, string what)
        {
            var json = await ClickAndCaptureJsonAsync(page,
                $"#sidePanelBody {formSelector} button[type='submit']", urlFragment);
            AssertSuccess(json, what);
        }

        private async Task<JsonElement> ClickAndCaptureJsonAsync(
            IPage page, string selector, string urlFragment, string? noRequestMessage = null)
        {
            IResponse response;
            try
            {
                response = await page.RunAndWaitForResponseAsync(
                    () => page.ClickAsync(selector),
                    r => r.Url.Contains(urlFragment, StringComparison.OrdinalIgnoreCase) && r.Request.Method == "POST",
                    new PageRunAndWaitForResponseOptions { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                Assert.Fail((noRequestMessage ?? $"按下 {selector} 後沒有發出 POST {urlFragment}。") + DescribeDialogs());
                throw;
            }
            var body = await response.TextAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(800);
            try { return JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException)
            {
                Assert.Fail($"POST {urlFragment} 回應不是 JSON（HTTP {response.Status}）：{body[..Math.Min(body.Length, 500)]}");
                throw;
            }
        }

        private void AssertSuccess(JsonElement json, string what)
            => Assert.True(json.TryGetProperty("success", out var s) && s.GetBoolean(),
                $"{what}失敗：{json}{DescribeDialogs()}");

        private void AssertNoJsErrors()
            => Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");

        private static async Task<int> StatusAsync(string dispatchId)
            => await SeedFixture.ScalarAsync<int>(
                $"SELECT \"Status\" FROM \"Dispatches\" WHERE \"DispatchId\" = '{dispatchId}'");
    }
}
