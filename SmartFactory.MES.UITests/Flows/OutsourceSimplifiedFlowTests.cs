using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Flows
{
    /// <summary>
    /// 委外簡化（mfg-domain.md §19.7）在桌機上的進入點：在「廠別」畫面開參數 → 桌機作業的行為跟著變。
    ///
    /// <para>服務層各有整合／單元測試（MobileMaterialFlowTests、OutsourceAutoPoTests），但都是直接塞參數、
    /// 直接呼叫服務；這三條參數預設關閉，nightly 從來沒有在「開著」的狀態下走過畫面。這裡守的是：</para>
    /// <list type="bullet">
    ///   <item>B1 收貨自動扣送外料：桌機確認委外收貨 → 系統開委外發料單過帳、從品號類別預設倉儲扣料。</item>
    ///   <item>B3 投產自動建委外採購單：桌機釋出整品委外工單 → 唯一有效報價的廠商自動得到一張已確認的委外採購單。</item>
    ///   <item>C 工序委外（不看參數）：來源是廠內工單的委外採購單，收回確認即過帳、不入庫、不進上架清單，
    ///         扣該工序投料、加工費累計到工單委外成本。</item>
    /// </list>
    ///
    /// <para><b>參數一定要從畫面開、也要從畫面關</b>：廠別參數有 10 分鐘記憶體快取（PlantParamService），
    /// 直接改資料庫 app 看不到；只有「廠別」存檔會清快取。每支測試在 finally 把覆寫取消，
    /// 否則後面所有共用這個庫的測試都在參數開著的狀態下跑。</para>
    ///
    /// <para>資料：scripts/seed-e2e-fixture.sql ⑮⑰。</para>
    /// </summary>
    public class OutsourceSimplifiedFlowTests : FlowTestBase
    {
        private const string IssueOnReceipt = "mfg_outsource_issue_on_receipt";
        private const string AutoPo         = "mfg_outsource_auto_po";

        public OutsourceSimplifiedFlowTests(UiTestFixture fixture) : base(fixture) { }

        [Fact]
        public async Task B1_收貨自動扣送外料開啟_桌機確認委外收貨_依收回比例自動開委外發料單從預設倉扣料()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            try
            {
                await SetPlantFlagAsync(page, IssueOnReceipt, true);

                // E2E-WO0011 量 10、用料 RM03 共 20 → 收回 4 應扣 8
                await CreateAndConfirmOutsourceReceiptAsync(page, "E2E-OPO02", 4m, "步驟①");

                Assert.Equal(1L, await SeedFixture.ScalarAsync<long>(
                    "SELECT count(*) FROM \"MaterialSlips\" WHERE \"SourceDocId\" = 'E2E-WO0011' AND \"Category\" = 3"));
                Assert.Equal(92m, await OnHandAsync("E2E-RM03", "E2E-LRAW"));
                Assert.Equal(8m, await SeedFixture.ScalarAsync<decimal>(
                    "SELECT \"IssuedQty\" FROM \"WorkOrderMaterials\" WHERE \"WorkOrderId\" = 'E2E-WO0011' AND \"ProductCode\" = 'E2E-RM03'"));
            }
            finally
            {
                await SetPlantFlagAsync(page, IssueOnReceipt, null);
            }
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task B1_參數關閉時_確認委外收貨不自動扣料()
        {
            // 對照組：同一張單、同一個量，參數關著就不能動到送外料——否則「預設關」是假的。
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            Assert.Null(await SeedFixture.ScalarAsync<string>(
                $"SELECT \"Value\" FROM \"PlantParams\" WHERE \"PlantId\" = 'P1' AND \"Key\" = '{IssueOnReceipt}'"));

            await CreateAndConfirmOutsourceReceiptAsync(page, "E2E-OPO02", 4m, "步驟①");

            Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                "SELECT count(*) FROM \"MaterialSlips\" WHERE \"SourceDocId\" = 'E2E-WO0011'"));
            Assert.Equal(100m, await OnHandAsync("E2E-RM03", "E2E-LRAW"));
        }

        [Fact]
        public async Task B3_投產自動建委外採購單開啟_桌機釋出整品委外工單_唯一報價廠商得到已確認委外採購單()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            try
            {
                await SetPlantFlagAsync(page, AutoPo, true);

                await GotoAsync(page, "/WorkOrder?searched=true&searchText=E2E-WO0012&pageSize=50");
                await OpenRowPanelAsync(page, "E2E-WO0012", "/WorkOrder/Details", "#sidePanelBody [data-action='releaseWorkOrder']");
                var response = await page.RunAndWaitForResponseAsync(
                    () => page.ClickAsync("#sidePanelBody [data-action='releaseWorkOrder']"),
                    r => r.Url.Contains("/WorkOrder/Release", StringComparison.OrdinalIgnoreCase) && r.Request.Method == "POST",
                    new PageRunAndWaitForResponseOptions { Timeout = 15_000 });
                var body = await response.TextAsync();
                Assert.Contains("\"success\":true", body.Replace(" ", ""));
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1200);

                Assert.Equal(1, await SeedFixture.ScalarAsync<int>(
                    "SELECT \"Status\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0012'"));

                var opo = await SeedFixture.ScalarAsync<string>(
                    "SELECT o.\"OutsourcePurchaseOrderId\" || '|' || o.\"Status\" || '|' || o.\"SupplierCode\" || '|' || l.\"Qty\"::numeric(18,0) || '|' || l.\"UnitPrice\"::numeric(18,0) "
                    + "FROM \"OutsourcePurchaseOrders\" o JOIN \"OutsourcePurchaseOrderLines\" l USING (\"OutsourcePurchaseOrderId\") "
                    + "WHERE l.\"SourceWorkOrderId\" = 'E2E-WO0012'");
                Assert.False(string.IsNullOrEmpty(opo),
                    $"釋出 E2E-WO0012 後沒有自動建委外採購單（E2E-OS01 只有一家有效報價 E2E-OQ01）。回應：{body}{DescribeDialogs()}");
                var p = opo!.Split('|');
                Assert.Equal("1", p[1]);           // 已確認：手機／桌機可直接收料
                Assert.Equal("E2E-CO01", p[2]);
                Assert.Equal("5", p[3]);           // 委外派工量
                Assert.Equal("7", p[4]);           // 報價單價

                // 畫面上找得到：委外收貨的委外採購單下拉只列已確認的單
                await GotoAsync(page, "/OutsourceReceipt?searched=true");
                await page.ClickAsync("button[data-url$='/OutsourceReceipt/Create']");
                await page.WaitForSelectorAsync("#sidePanelBody #opoSelect", new PageWaitForSelectorOptions { Timeout = 10_000 });
                var options = await page.Locator("#sidePanelBody #opoSelect option")
                    .EvaluateAllAsync<string[]>("els => els.map(e => e.value)");
                Assert.Contains(p[0], options);
            }
            finally
            {
                await SetPlantFlagAsync(page, AutoPo, null);
            }
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        [Fact]
        public async Task C_工序委外收回_確認即過帳_不入庫不進上架清單_扣該工序投料_加工費計入工單委外成本()
        {
            await ResetFixtureAsync();
            var page = await NewFlowPageAsync();
            var fgBefore = await OnHandAsync("E2E-FG01", "E2E-LFG");

            // E2E-OPO03 指向廠內工單 E2E-WO0031（量 10、工序 10 用料 RM03 共 10）→ 收回 4
            var receiptId = await CreateAndConfirmOutsourceReceiptAsync(page, "E2E-OPO03", 4m, "步驟①", expectedStatus: 3);

            // 收回的是在製品，回到廠內下一道工序：成品庫存不動、倉庫作業的待入庫清單上沒有它
            Assert.Equal(fgBefore, await OnHandAsync("E2E-FG01", "E2E-LFG"));
            await GotoAsync(page, "/WarehouseOps");
            await page.FillAsync("#inboundSearch", receiptId);
            await ClickActionAsync(page, "loadPendingInbound", 800);
            Assert.Equal(0, await page.Locator("#inboundList tr", new PageLocatorOptions { HasText = receiptId }).CountAsync());

            // 扣該工序投料：4 ÷ 10 × 10 = 4（不看 B1 參數，工序委外一律扣）
            Assert.Equal(96m, await OnHandAsync("E2E-RM03", "E2E-LRAW"));
            Assert.Equal(4m, await SeedFixture.ScalarAsync<decimal>(
                "SELECT \"IssuedQty\" FROM \"WorkOrderMaterials\" WHERE \"WorkOrderId\" = 'E2E-WO0031' AND \"ProductCode\" = 'E2E-RM03'"));

            // 加工費 4 × 5 計入工單委外成本（E2E-CUR 匯率 1）
            Assert.Equal(20m, await SeedFixture.ScalarAsync<decimal>(
                "SELECT \"ActualOutsourceCost\" FROM \"WorkOrders\" WHERE \"WorkOrderId\" = 'E2E-WO0031'"));
            Assert.True(JsErrors.Count == 0, $"出現 JS 錯誤：\n{string.Join("\n", JsErrors)}");
        }

        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 在「廠別」編輯頁設定 P1 的布林參數覆寫：true／false＝覆寫成開／關，null＝取消覆寫（回到全域值）。
        /// 欄位可能收在未展開的頁籤或折疊區裡，勾選用 JS 直接設；存檔仍按真正的送出鈕，最後以資料庫確認寫入。
        /// </summary>
        private async Task SetPlantFlagAsync(IPage page, string key, bool? on)
        {
            await GotoAsync(page, "/Plant/Edit/P1");
            var found = await page.EvaluateAsync<bool>(@"([key, on]) => {
                const ovr = document.getElementById('ovr_' + key), val = document.getElementById('val_' + key);
                if (!ovr || !val) return false;
                ovr.checked = on !== null;
                if (on !== null) val.checked = on;
                return true;
            }", new object?[] { key, on });
            Assert.True(found, $"廠別編輯頁找不到參數 {key} 的欄位（#ovr_{key}／#val_{key}）。");

            await page.Locator("#panelForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(800);

            var stored = await SeedFixture.ScalarAsync<string>(
                $"SELECT \"Value\" FROM \"PlantParams\" WHERE \"PlantId\" = 'P1' AND \"Key\" = '{key}'");
            if (on is null)
                Assert.True(stored is null, $"取消 {key} 的覆寫後 PlantParams 仍有值 {stored}——後面的測試會在參數開著的狀態下跑。");
            else
                Assert.True(string.Equals(stored, on.Value ? "true" : "false", StringComparison.OrdinalIgnoreCase),
                    $"在廠別畫面設定 {key}={on} 後，PlantParams 存的是 {stored ?? "(無)"}。{DescribeDialogs()}");
        }

        private static async Task<decimal> OnHandAsync(string product, string location)
            => await SeedFixture.ScalarAsync<decimal>(
                $"SELECT COALESCE(SUM(\"QtyOnHand\"),0) FROM \"Inventories\" WHERE \"ProductCode\" = '{product}' AND \"LocationCode\" = '{location}'");
    }
}
