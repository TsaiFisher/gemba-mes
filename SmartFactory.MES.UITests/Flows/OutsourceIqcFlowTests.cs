using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 委外收回 → IQC → 入庫（畫面層，mfg-domain.md §19.3a，2026-10-12）。
    ///
    /// <para>規則：委外收回品要先品檢；貨<b>進到一般倉</b>的那一刻才算合格入庫，回寫委外派工完工量，
    /// 整品委外工單收滿直接「入庫完成」。進一般倉有兩條路，兩條都要守：</para>
    /// <list type="bullet">
    ///   <item>先上架到待驗倉 → 桌機 IQC 合格 → 系統自動調撥到一般倉（IqcInspectionService 放行）。</item>
    ///   <item>先 IQC 合格 → 上架時閘門放行到一般倉（WarehouseApiController.Fulfillment）。</item>
    /// </list>
    /// <para>服務層的整合測試（IntegrationTests/Flows/MobileMaterialFlowTests）走手機 API；
    /// 桌機的收貨單 → 倉庫作業 → 待檢清單 → 檢驗面板這條接線之前沒有任何測試點過。</para>
    ///
    /// <para>資料：scripts/seed-e2e-fixture.sql ⑮——非免驗成品 E2E-OS01（分類預設倉 E2E-WFG/E2E-LFG）、
    /// 整品委外工單 E2E-WO0011、委外派工 E2E-DP0011（10）、委外採購單 E2E-OPO02。
    /// 種子每輪會清掉這張委外採購單上一輪的收貨單，否則確認收貨時會被超收規則擋下。</para>
    ///
    /// <para>每個測試是一條完整的流程（步驟互為前提），理由同 <see cref="ProcureToPayFlowTests"/>。</para>
    /// </summary>
    public class OutsourceIqcFlowTests : FlowTestBase
    {
        private const string Opo      = "E2E-OPO02";
        private const string Product  = "E2E-OS01";
        private const string Dispatch = "E2E-DP0011";
        private const string WorkOrder = "E2E-WO0011";
        private const string NormalWh  = "E2E-WFG";
        private const string NormalLoc = "E2E-LFG";

        // DispatchStatus / WorkOrderStatus 的資料庫值（Modules/MFG/Models）
        private const int DispatchPending = 0, DispatchInProgress = 4, DispatchClosed = 5;
        private const int WoReleased = 1, WoInProgress = 2, WoClosed = 4;

        public OutsourceIqcFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task 未品檢不得上架一般倉_上架待驗倉後品檢合格_系統轉入一般倉_派工結案_整品委外工單入庫完成()
        {
            await ResetFixtureAsync();
            var (qcWh, qcLoc) = await QuarantineLocationAsync();
            var page = await NewFlowPageAsync();

            var receiptId = await CreateAndConfirmOutsourceReceiptAsync(page, Opo, 10m, "步驟①");

            // ② 閘門：還沒品檢，一般倉上不去
            var (ok, message) = await TryPutAwayAsync(page, receiptId, NormalWh, NormalLoc, "步驟②：未品檢上架一般倉");
            Assert.False(ok,
                $"步驟②：{receiptId} 尚未 IQC，卻能上架到一般倉 {NormalWh}——品質隔離閘門失效，"
                + "未驗的委外收回品可直接被領用。（品號 E2E-OS01 必須是非免驗）");
            Assert.Equal(0m, await OnHandAsync(NormalLoc));
            Assert.Equal(0m, await DispatchQtyDoneAsync());

            // ③ 上架到待驗倉：量進待驗倉，但還不算完工
            await PutAwayAsync(page, receiptId, qcWh, qcLoc, "步驟③：上架待驗倉");
            Assert.Equal(10m, await OnHandAsync(qcLoc));
            Assert.Equal(0m, await DispatchQtyDoneAsync());

            // ④ 待檢清單看得到，而且標「委外」
            await GotoAsync(page, "/InspectionOrder/PendingIqc");
            var row = PendingRow(page, receiptId);
            Assert.True(await row.CountAsync() > 0,
                $"步驟④：IQC 待檢清單沒有 {receiptId}。委外收回品上架到待驗倉後應列入待檢"
                + $"（IqcInspectionService.GetPendingLinesAsync 的委外收貨分支）。{DescribeDialogs()}");
            Assert.True(await row.Locator(".badge.bg-info").CountAsync() > 0,
                $"步驟④：待檢清單上 {receiptId} 沒有「委外」標記。");

            // ⑤ 品檢合格 → 系統調撥待驗倉 → 一般倉，回寫派工，整品委外工單直接入庫完成
            var result = await SubmitIqcAsync(page, receiptId, failQty: 0, "步驟⑤");
            Assert.Equal("Pass", result);

            Assert.Equal(0m, await OnHandAsync(qcLoc));
            Assert.Equal(10m, await OnHandAsync(NormalLoc));
            Assert.Equal(10m, await DispatchQtyDoneAsync());
            Assert.Equal(DispatchClosed, await DispatchStatusAsync());
            Assert.Equal(WoClosed, await WorkOrderStatusAsync());

            // ⑥ 已檢過的不再出現在待檢清單
            await GotoAsync(page, "/InspectionOrder/PendingIqc");
            Assert.Equal(0, await PendingRow(page, receiptId).CountAsync());

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task 先品檢後上架_合格即可直接上架一般倉_部分收回派工與工單轉生產中()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();

            var receiptId = await CreateAndConfirmOutsourceReceiptAsync(page, Opo, 4m, "步驟①");

            // ② 已確認、尚未上架就能檢；合格時待驗倉沒有量，系統不調撥、也不回寫
            await GotoAsync(page, "/InspectionOrder/PendingIqc");
            Assert.True(await PendingRow(page, receiptId).CountAsync() > 0,
                $"步驟②：已確認未上架的委外收貨 {receiptId} 應列入 IQC 待檢。{DescribeDialogs()}");
            Assert.Equal("Pass", await SubmitIqcAsync(page, receiptId, failQty: 0, "步驟②"));
            Assert.Equal(0m, await DispatchQtyDoneAsync());

            // ③ 合格後上架一般倉：閘門放行，進一般倉＝合格入庫 → 回寫 4（派工 10，未滿）
            await PutAwayAsync(page, receiptId, NormalWh, NormalLoc, "步驟③：已合格上架一般倉");
            Assert.Equal(4m, await OnHandAsync(NormalLoc));
            Assert.Equal(4m, await DispatchQtyDoneAsync());
            Assert.Equal(DispatchInProgress, await DispatchStatusAsync());
            Assert.Equal(WoInProgress, await WorkOrderStatusAsync());

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task 品檢不合格_貨留在待驗倉_不回寫派工也不推進工單()
        {
            await ResetFixtureAsync();
            var (qcWh, qcLoc) = await QuarantineLocationAsync();
            var page = await NewFlowPageAsync();

            var receiptId = await CreateAndConfirmOutsourceReceiptAsync(page, Opo, 10m, "步驟①");
            await PutAwayAsync(page, receiptId, qcWh, qcLoc, "步驟②：上架待驗倉");

            // MIL-STD-1916 Ac=0：不良 1 件即整批拒收
            await GotoAsync(page, "/InspectionOrder/PendingIqc");
            Assert.Equal("Fail", await SubmitIqcAsync(page, receiptId, failQty: 1, "步驟③"));

            Assert.Equal(10m, await OnHandAsync(qcLoc));
            Assert.Equal(0m, await OnHandAsync(NormalLoc));
            Assert.Equal(0m, await DispatchQtyDoneAsync());
            Assert.Equal(DispatchPending, await DispatchStatusAsync());
            Assert.Equal(WoReleased, await WorkOrderStatusAsync());
            // 不合格品的去處是 NCR 處置（報廢／重工／退回），不是再上架——整批已在待驗倉，待入庫清單上沒有它。

            Assert.True(JsErrors.Count == 0, $"流程中出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        // ════════════════════════════════════════════════════════════════════

        /// <summary>從 IQC 待檢清單開該收貨單的檢驗面板，填不良數送出，回傳後端判定（Pass／Fail）。</summary>
        private async Task<string> SubmitIqcAsync(IPage page, string receiptId, int failQty, string step)
        {
            var row = PendingRow(page, receiptId);
            Assert.True(await row.CountAsync() > 0, $"{step}：IQC 待檢清單沒有 {receiptId}。{DescribeDialogs()}");
            await row.Locator("[data-url*='IqcForm']").ClickAsync();
            await page.WaitForSelectorAsync("#sidePanelBody #sideForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            // 抽樣數預填建議值；不合格情境要確保抽樣數 ≥ 不良數，否則被「不良數超過抽樣數」擋下。
            var sample = page.Locator("#sidePanelBody input[name='SampleQty']");
            if (int.TryParse(await sample.InputValueAsync(), out var n) && n < Math.Max(1, failQty))
                await sample.FillAsync(Math.Max(1, failQty).ToString());
            await page.Locator("#sidePanelBody input[name='FailQty']").FillAsync(failQty.ToString());

            var response = await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("#sidePanelBody #sideForm button[type='submit']"),
                r => r.Url.Contains("/InspectionOrder/SubmitIqc", StringComparison.OrdinalIgnoreCase)
                     && r.Request.Method == "POST",
                new PageRunAndWaitForResponseOptions { Timeout = 15_000 });

            var body = await response.TextAsync();
            JsonElement json;
            try { json = JsonDocument.Parse(body).RootElement; }
            catch (JsonException)
            {
                // 伺服端驗證擋下時回的是重繪的 _IqcForm（HTML），不是 JSON
                Assert.Fail($"{step}：IQC 送出被伺服端驗證擋下（回傳表單 HTML）。\n{body[..Math.Min(body.Length, 800)]}");
                throw;
            }
            Assert.True(json.TryGetProperty("success", out var s) && s.GetBoolean(),
                $"{step}：IQC 送出失敗：{body}");

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1000);

            // 判定結果在 data.result（JsonOk 包一層）；取不到時退回查資料庫，免得綁死回應形狀。
            if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("result", out var r1) && r1.ValueKind == JsonValueKind.String)
                return r1.GetString()!;
            return await SeedFixture.ScalarAsync<string>(
                $"SELECT \"Result\" FROM \"MobileIqcRecords\" WHERE \"GoodsReceiptId\" = '{receiptId}' ORDER BY \"Id\" DESC LIMIT 1")
                ?? "(無 IQC 紀錄)";
        }

        private static ILocator PendingRow(IPage page, string receiptId)
            => page.Locator("table tbody tr", new PageLocatorOptions { HasText = receiptId });

        /// <summary>本廠的待驗倉與其儲位。乾淨部署由 QuarantineWarehouseSeeder 建 QC-P1／QC-P1-Q01，但不寫死。</summary>
        private static async Task<(string Wh, string Loc)> QuarantineLocationAsync()
        {
            var wh = await SeedFixture.ScalarAsync<string>(
                "SELECT \"WhCode\" FROM \"Warehouses\" WHERE \"WarehouseType\" = 3 AND \"PlantId\" = 'P1' AND \"IsActive\" ORDER BY \"WhCode\" LIMIT 1");
            Assert.False(string.IsNullOrEmpty(wh), "P1 沒有待驗倉（WarehouseType=Quarantine），無法測品質隔離。");
            var loc = await SeedFixture.ScalarAsync<string>(
                $"SELECT \"LocationCode\" FROM \"StorageLocations\" WHERE \"WhCode\" = '{wh}' AND \"IsActive\" ORDER BY \"LocationCode\" LIMIT 1");
            Assert.False(string.IsNullOrEmpty(loc), $"待驗倉 {wh} 沒有可用儲位。");
            return (wh!, loc!);
        }

        private static async Task<decimal> OnHandAsync(string locationCode)
            => await SeedFixture.ScalarAsync<decimal>(
                $"SELECT COALESCE(SUM(\"QtyOnHand\"),0) FROM \"Inventories\" WHERE \"ProductCode\" = '{Product}' AND \"LocationCode\" = '{locationCode}'");

        private static async Task<decimal> DispatchQtyDoneAsync()
            => await SeedFixture.ScalarAsync<decimal>(
                $"SELECT \"QtyDone\" FROM \"Dispatches\" WHERE \"DispatchId\" = '{Dispatch}'");

        private static async Task<int> DispatchStatusAsync()
            => await SeedFixture.ScalarAsync<int>(
                $"SELECT \"Status\" FROM \"Dispatches\" WHERE \"DispatchId\" = '{Dispatch}'");

        private static async Task<int> WorkOrderStatusAsync()
            => await SeedFixture.ScalarAsync<int>(
                $"SELECT \"Status\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = '{WorkOrder}'");
    }
}
