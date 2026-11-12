using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 重工的端到端流程（桌機畫面＋真資料庫）：
    /// <list type="number">
    /// <item>NCR 處置選「重工」→ 執行：自動開出連到原工單的重工工單、NCR 記下它。</item>
    /// <item>工單列表對已完工工單按「建立重工」→ 重工工單（複製工序、用料依重工數量等比換算、已發量從 0 起算）→
    /// 投產自動開重工發料（RI）→ 過帳 → 重工退料（RX）：庫存扣回、重工工單的已發／已退量累計，原工單不受影響。</item>
    /// <item>「建立重工」只勾部分工序：只帶那些工序與其下的用料；全不勾被擋。</item>
    /// </list>
    ///
    /// <para>種子 ㉕：已完工工單 E2E-WO0051（工序 10 加工用 E2E-RM01 ×20、工序 20 組裝用 E2E-RM02 ×10）、報工 E2E-PR0051（不良 2）。
    /// 原料庫存在 E2E-WRAW/E2E-LRAW（⑦）。每支開頭重跑種子，可重複執行。</para>
    /// </summary>
    public class ReworkFlowTests : MaterialSlipFlowTestBase
    {
        private const string OrigWo = "E2E-WO0051";
        private const string Report = "E2E-PR0051";
        private const string RawWh = "E2E-WRAW", RawLoc = "E2E-LRAW";
        private const string Rm1 = "E2E-RM01", Rm2 = "E2E-RM02";

        public ReworkFlowTests(UiTestFixture fixture) : base(fixture) { }

        // ── ① NCR 處置重工 ───────────────────────────────────────────────────

        [Fact]
        public async Task NCR處置選重工_執行後開出連到原工單的重工工單_複製工序與等比用料()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await GotoAsync(page, "/Ncr?searched=true");
            await page.ClickAsync("button[data-url$='/Ncr/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #sideForm input[name='ProductCode']");
            const string F = "#sidePanelBody #sideForm";
            await page.SelectOptionAsync($"{F} select[name='SourceType']", "ProductionReport");
            await page.FillAsync($"{F} input[name='SourceId']", Report);
            await page.FillAsync($"{F} input[name='ProductCode']", "E2E-FG01");
            await page.FillAsync($"{F} input[name='Qty']", "2");
            await page.FillAsync($"{F} input[name='UomCode']", "E2E-EA");
            await page.SelectOptionAsync($"{F} #dispositionSel", "Rework");
            await SavePanelAsync(page, $"{F} button[value='save']", "NCR 存檔");

            var ncr = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "NcrId" FROM "NonConformanceReports" WHERE "SourceId"='{Report}' ORDER BY "CreatedAt" DESC LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(ncr), $"NCR 存檔後資料庫沒有來源為 {Report} 的 NCR。{DescribeDialogs()}");

            await GotoAsync(page, $"/Ncr?searched=true&searchNcrId={ncr}");
            await OpenRowPanelAsync(page, ncr!, $"/Ncr/Details/{ncr}", "#sidePanelBody #sideForm");
            var execute = page.Locator("#sidePanelBody [data-action='executeNcr']");
            Assert.True(await execute.CountAsync() == 1,
                $"NCR {ncr}（未執行）的明細面板上沒有「執行處置」鈕。面板上的按鈕：{string.Join("、", await page.Locator("#sidePanelBody button").AllInnerTextsAsync())}");
            await execute.ClickAsync();
            await WaitForAsync(async () => await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "NcrStatus" FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'""") == "Executed",
                $"按了「執行處置」NCR {ncr} 沒有變成已執行。{DescribeDialogs()}");

            var rw = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "ReworkWorkOrderId" FROM "NonConformanceReports" WHERE "NcrId"='{ncr}'""");
            Assert.False(string.IsNullOrEmpty(rw),
                $"NCR 已執行，卻沒有記下重工工單——多半是找不到原工單（來源報工 {Report} → 工單）而只回了「請手動建立」。{DescribeDialogs()}");

            var wo = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Type"||'|'||"Status"||'|'||coalesce("ReworkFromWorkOrderId",'')||'|'||"ProductCode"||'|'||"Quantity"::numeric(18,2) FROM "WorkOrders" WHERE "WorkOrderId"='{rw}'""");
            Assert.Equal($"1|0|{OrigWo}|E2E-FG01|2.00", wo);   // 重工、未釋出、連原工單、NCR 的品號與不良數量
            // 與工單列表「建立重工」同一份：複製工序、用料依不良數量等比換算（原工單 10 個用 RM01 20／RM02 10 → 2 個）。
            // 2026-10-15 前只建表頭，投產後排不出派工、發不了料。
            Assert.Equal("10,20", await OperationsAsync(rw!));
            Assert.Equal("E2E-RM01:4.00|E2E-RM02:2.00", await MaterialsAsync(rw!, "RequiredQty"));

            // 開出來的重工工單要在工單畫面看得到（資料授權過濾沒有把它藏起來）。
            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={rw}&pageSize=50");
            Assert.True(await page.Locator($"[data-url$='/WorkOrder/Details/{rw}']").CountAsync() > 0,
                $"重工工單 {rw} 已寫入資料庫，工單列表卻查不到。");
            Assert.Empty(JsErrors);
        }

        // ── ② 工單列表手動重工 → 投產 → 重工發料 → 重工退料 ─────────────────

        [Fact]
        public async Task 已完工工單建立重工_用料等比換算_投產自動開重工發料_過帳再退料_原工單不動()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            var before = await OnHandAsync(Rm1, RawLoc);

            // 重工鈕只出現在已完工／已入庫結案的工單列上。
            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={OrigWo}&pageSize=50");
            await OpenRowPanelAsync(page, OrigWo, $"/WorkOrder/ReworkForm/{OrigWo}", "#sidePanelBody input[name='quantity']");
            // 工序預設全勾
            Assert.Equal(2, await page.Locator("#sidePanelBody #reworkOps input[name='operationSeqs']:checked").CountAsync());
            await page.FillAsync("#sidePanelBody input[name='quantity']", "3");
            await page.FillAsync("#sidePanelBody input[name='remarks']", "E2E 重工");
            var resp = await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("#sidePanelBody #panelForm button[type='submit']"),
                r => r.Request.Method == "POST" && r.Url.Contains($"/WorkOrder/CreateRework/{OrigWo}"));
            var body = await resp.TextAsync();
            Assert.True(body.Contains("\"success\":true"), $"建立重工工單被拒：{body}");

            var rw = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "WorkOrderId" FROM "WorkOrders" WHERE "ReworkFromWorkOrderId"='{OrigWo}' AND "Type"=1 ORDER BY "CreatedAt" DESC LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(rw), "建立重工工單回了成功，資料庫卻沒有連到原工單的重工工單");
            Assert.Equal("0|3.00", await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Status"||'|'||"Quantity"::numeric(18,2) FROM "WorkOrders" WHERE "WorkOrderId"='{rw}'"""));
            // 投產要有工序才排得出派工、要有用料才發得了料；用料依重工數量等比換算（10 個用 20／10 → 3 個用 6／3），已發量從 0 起算。
            Assert.Equal("10,20", await OperationsAsync(rw!));
            Assert.Equal("E2E-RM01:6.00|E2E-RM02:3.00", await MaterialsAsync(rw!, "RequiredQty"));
            Assert.Equal("E2E-RM01:0.00|E2E-RM02:0.00", await MaterialsAsync(rw!, "IssuedQty"));
            var before2 = await OnHandAsync(Rm2, RawLoc);

            // 投產：從工單明細按「投產」。Push 用料自動開一張發料單——重工工單開的是重工發料（RI），不是一般發料（MI）。
            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={rw}&pageSize=50");
            await OpenRowPanelAsync(page, rw!, $"/WorkOrder/Details/{rw}", "[data-action='releaseWorkOrder']");
            await page.ClickAsync("[data-action='releaseWorkOrder']");
            await WaitForAsync(async () => await SeedFixture.ScalarAsync<int>(
                    $"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{rw}'""") == 1,
                $"重工工單 {rw} 按了投產沒有變成已釋出。{DescribeDialogs()}");
            Assert.Equal("5:1", await SeedFixture.ScalarAsync<string>(
                $"""SELECT string_agg("Category"||':'||n, ',') FROM (SELECT "Category", count(*) n FROM "MaterialSlips" WHERE "SourceDocId"='{rw}' GROUP BY 1) x"""));
            var ri = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{rw}' AND "Category"=5""");
            Assert.Equal("E2E-RM01:6.00|E2E-RM02:3.00", await SeedFixture.ScalarAsync<string>(
                $"""SELECT string_agg("ProductCode"||':'||"RequestedQty"::numeric(18,2), '|' ORDER BY "ProductCode") FROM "MaterialSlipLines" WHERE "SlipId"='{ri}'"""));

            // 自動開的單沒有來源儲位：倉庫在草稿上指定後才進流程（同一般工單的 Push 發料單）
            await OpenSlipPanelAsync(page, ri!, "ReworkIssue");
            await SetAllLinesLocationAsync(page, RawWh, RawLoc);
            Assert.Empty(await SubmitPanelAsync(page));
            await RunSlipAsync(page, ri!, "ReworkIssue", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(5, await SlipStatusAsync(ri!));
            Assert.Equal((before - 6m, before2 - 3m), (await OnHandAsync(Rm1, RawLoc), await OnHandAsync(Rm2, RawLoc)));
            Assert.Equal("E2E-RM01:6.00|E2E-RM02:3.00", await MaterialsAsync(rw!, "IssuedQty"));
            Assert.Equal("RI", await MovementTypeAsync(ri!));

            // 重工退料 2
            Assert.Empty(await SaveNewSlipAsync(page, "ReworkReturn", rw!, (Rm1, 2m, RawWh, RawLoc)));
            var rx = await LatestSlipAsync(6);
            await RunSlipAsync(page, rx, "ReworkReturn", "Approve", "MarkPicking", "Confirm", "Issue");
            Assert.Equal(5, await SlipStatusAsync(rx));
            Assert.Equal(before - 4m, await OnHandAsync(Rm1, RawLoc));
            Assert.Equal(2m, await WoMaterialAsync(rw!, "ReturnedQty"));
            Assert.Equal("RR", await MovementTypeAsync(rx));

            // 退料的單位成本取自同一張重工工單先前發料凍結的成本，不是現在的庫存單價。
            Assert.Equal(await MovementUnitCostAsync(ri!), await MovementUnitCostAsync(rx));

            // 原工單的發退料紀錄不受影響
            Assert.Equal(0m, await WoMaterialAsync(OrigWo, "IssuedQty"));
            Assert.Equal(0m, await WoMaterialAsync(OrigWo, "ReturnedQty"));
            Assert.Empty(JsErrors);
        }

        // ── ②' 不良品已入庫：列為重工用料，投產時隨重工發料一起發 ────────────────

        /// <summary>
        /// 不良品已入庫（例如已繳庫後才判退）時勾「不良品已入庫」：重工工單另加一行不良品本身的用料，
        /// 投產自動開的重工發料單帶著它——完工時它的帳面值就是良品回存貨的原值（§16.4b）。
        /// 不勾的話完工多出一份良品、不良品仍留在庫存。發料與完工結算的帳見整合測試 ReworkCostFlowTests。
        /// </summary>
        [Fact]
        public async Task 勾選不良品已入庫_重工用料多一行不良品本身_投產自動開的重工發料一起帶出()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={OrigWo}&pageSize=50");
            await OpenRowPanelAsync(page, OrigWo, $"/WorkOrder/ReworkForm/{OrigWo}", "#sidePanelBody input[name='quantity']");
            // 原工單是「已完工」未繳庫：不良品不在帳上，預設不勾
            Assert.False(await page.IsCheckedAsync("#sidePanelBody #rwIncludeDefective"));
            await page.CheckAsync("#sidePanelBody #rwIncludeDefective");
            await page.FillAsync("#sidePanelBody input[name='quantity']", "2");
            var body = await SubmitReworkAsync(page);
            Assert.True(body.Contains("\"success\":true"), $"建立重工工單被拒：{body}");

            var rw = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "WorkOrderId" FROM "WorkOrders" WHERE "ReworkFromWorkOrderId"='{OrigWo}' AND "Type"=1""");
            Assert.Equal("E2E-FG01:2.00|E2E-RM01:4.00|E2E-RM02:2.00", await MaterialsAsync(rw!, "RequiredQty"));
            Assert.Equal("10|0|0", await SeedFixture.ScalarAsync<string>(
                $"""SELECT "OperationSeq"||'|'||"BomSeq"||'|'||"SourceMode" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{rw}' AND "ProductCode"='E2E-FG01'"""));

            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={rw}&pageSize=50");
            await OpenRowPanelAsync(page, rw!, $"/WorkOrder/Details/{rw}", "[data-action='releaseWorkOrder']");
            await page.ClickAsync("[data-action='releaseWorkOrder']");
            await WaitForAsync(async () => await SeedFixture.ScalarAsync<int>(
                    $"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{rw}'""") == 1,
                $"重工工單 {rw} 按了投產沒有變成已釋出。{DescribeDialogs()}");
            var ri = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{rw}' AND "Category"=5""");
            Assert.Equal("E2E-FG01:2.00|E2E-RM01:4.00|E2E-RM02:2.00", await SeedFixture.ScalarAsync<string>(
                $"""SELECT string_agg("ProductCode"||':'||"RequestedQty"::numeric(18,2), '|' ORDER BY "ProductCode") FROM "MaterialSlipLines" WHERE "SlipId"='{ri}'"""));
            Assert.Empty(JsErrors);
        }

        // ── ③ 只重做部分工序 ─────────────────────────────────────────────────

        [Fact]
        public async Task 建立重工只勾部分工序_只帶那些工序與其用料_全不勾被擋()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={OrigWo}&pageSize=50");
            await OpenRowPanelAsync(page, OrigWo, $"/WorkOrder/ReworkForm/{OrigWo}", "#sidePanelBody #reworkOps");
            await page.FillAsync("#sidePanelBody input[name='quantity']", "5");

            // 全不勾：擋下、不建單
            await page.UncheckAsync("#sidePanelBody #rwOp10");
            await page.UncheckAsync("#sidePanelBody #rwOp20");
            var body = await SubmitReworkAsync(page);
            Assert.Contains("\"success\":false", body);
            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                $"""SELECT count(*) FROM "WorkOrders" WHERE "ReworkFromWorkOrderId"='{OrigWo}'"""));

            // 只勾 20 組裝：只帶工序 20 與掛在它底下的 RM02（10 × 5/10 = 5），不帶 10 加工的 RM01
            await page.CheckAsync("#sidePanelBody #rwOp20");
            body = await SubmitReworkAsync(page);
            Assert.True(body.Contains("\"success\":true"), $"建立重工工單被拒：{body}");
            var rw = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "WorkOrderId" FROM "WorkOrders" WHERE "ReworkFromWorkOrderId"='{OrigWo}' AND "Type"=1""");
            Assert.Equal("20", await OperationsAsync(rw!));
            Assert.Equal("E2E-RM02:5.00", await MaterialsAsync(rw!, "RequiredQty"));
            Assert.Empty(JsErrors);
        }

        private static async Task<string> SubmitReworkAsync(IPage page)
        {
            var resp = await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("#sidePanelBody #panelForm button[type='submit']"),
                r => r.Request.Method == "POST" && r.Url.Contains($"/WorkOrder/CreateRework/{OrigWo}"));
            var body = await resp.TextAsync();
            await page.WaitForTimeoutAsync(500);
            return body;
        }

        private static async Task<string?> OperationsAsync(string wo) => await SeedFixture.ScalarAsync<string>(
            $"""SELECT string_agg("OperationSeq"::text, ',' ORDER BY "OperationSeq") FROM "WorkOrderOperations" WHERE "WorkOrderId"='{wo}'""");

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static Task<decimal> OnHandAsync(string product, string loc) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(SUM("QtyOnHand"),0) FROM "Inventories" WHERE "ProductCode"='{product}' AND "LocationCode"='{loc}'""");

        private static Task<decimal> WoMaterialAsync(string wo, string column) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT "{column}" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{wo}' AND "ProductCode"='{Rm1}'""");

        /// <summary>「品號:量|品號:量」，量取 WorkOrderMaterials 的指定欄位。</summary>
        private static async Task<string?> MaterialsAsync(string wo, string column) => await SeedFixture.ScalarAsync<string>(
            $"""SELECT string_agg("ProductCode"||':'||"{column}"::numeric(18,2), '|' ORDER BY "ProductCode") FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{wo}'""");

        private static async Task<string?> MovementTypeAsync(string slip) => await SeedFixture.ScalarAsync<string>(
            $"""SELECT string_agg(DISTINCT "TypeCode", ',') FROM "StockMovements" WHERE "DocNo"='{slip}'""");

        private static Task<decimal> MovementUnitCostAsync(string slip) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT "UnitCost" FROM "StockMovements" WHERE "DocNo"='{slip}' AND "ProductCode"='{Rm1}' LIMIT 1""");
    }
}
