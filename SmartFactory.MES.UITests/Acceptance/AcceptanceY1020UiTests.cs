using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SmartFactory.MES.UITests.Flows;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 人工驗收清單場次 Y（2026-10-20 生產主線修正）以畫面操作代測：工單作廢收斂殘留單據、委外採購單作廢、
    /// 投產送審閘（排程與桌機／手機開工）、駁回與撤回退排程、強制送審參數、沒有簽核流程時的提醒、英文介面。
    ///
    /// <para><b>為什麼要做</b>：這幾條規則在服務層都有單元／整合測試，但驗收清單問的是「使用者在畫面上看到什麼」——
    /// 對話框寫不寫出單號、按鈕在不在、訊息是不是英文。那一層只有走畫面驗得到。</para>
    ///
    /// <para>以 E2E-WO0001 為主角（有 BOM 兩料、一道工序、派工 E2E-DP0001）：每支開頭重跑種子，把它改回「已規劃」，
    /// 按投產時系統自動建草稿發料單。簽核設定、廠別參數都在 finally 還原；廠別參數走畫面存檔（有快取）。</para>
    ///
    /// <para><b>不在這裡驗的</b>：簡化模式下 MRP 自動投產送審（Y-7 第 3 條，MrpEngine 單元測試已驗各組合）、
    /// 委外單作廢後轉委外清單重新出現建議（Y-3 第 3 條後半，整合測試已驗）、料在廠商端被擋（Y-3 第 5 條後半，同上）。</para>
    /// </summary>
    public class AcceptanceY1020UiTests : MaterialSlipFlowTestBase
    {
        private const string Plant = "P1";
        private const string Wo = "E2E-WO0001";
        private const string Dispatch = "E2E-DP0001";
        private const string Machine = "E2E-MC03";
        private const string RawWh = "E2E-WRAW", RawLoc = "E2E-LRAW";
        private const string TemplateId = "E2E-APR-WO-TPL", StepId = "E2E-APR-WO-S1", ConfigId = "E2E-APR-CFG-WO";
        private const string ThresholdKey = "mfg_wo_approval_threshold", ReworkKey = "mfg_wo_approval_rework";

        private static readonly Regex Cjk = new(@"[一-鿿]", RegexOptions.Compiled);

        public AcceptanceY1020UiTests(UiTestFixture fixture) : base(fixture) { }

        // ════════════════════════════════════════════════════════════════════
        // Y-1 只有草稿發料單：作廢工單時發料單自動取消、派工取消、庫存不動
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y1_只有草稿發料單_作廢工單_發料單與派工一併取消_庫存不動()
        {
            await PrepareAsync();
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                var slip = await SingleSlipAsync();
                Assert.Equal(0, await SlipStatusAsync(slip));   // 投產自動建的草稿
                var rm = await OnHandAsync("E2E-RM01");

                await VoidWorkOrderAsync(page);
                await WaitForAsync(async () => await WoStatusAsync() == 5, "工單沒有作廢" + DescribeDialogs());
                Assert.Equal(7, await SlipStatusAsync(slip));   // MaterialSlipStatus.Cancelled
                Assert.Equal(8, await IntAsync($"""SELECT "Status"::text FROM "Dispatches" WHERE "DispatchId"='{Dispatch}'"""));   // Cancelled
                Assert.Equal(rm, await OnHandAsync("E2E-RM01"));
                Assert.Empty(JsErrors);
            }
            finally { await CleanupAsync(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-2 倉庫已確認揀料：擋作廢並說明；發料單取消提醒放回原儲位；取消後可作廢
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y2_已確認揀料的發料單_擋作廢並列出單號_倉庫取消後工單可作廢()
        {
            await PrepareAsync();
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                var slip = await SingleSlipAsync();
                await OpenSlipPanelAsync(page, slip, "InternalIssue");
                await SetAllLinesLocationAsync(page, RawWh, RawLoc);
                Assert.Empty(await SubmitPanelAsync(page));
                await RunSlipAsync(page, slip, "InternalIssue", "Approve", "MarkPicking", "Confirm");
                Assert.Equal(4, await SlipStatusAsync(slip));   // Confirmed
                var rm = await OnHandAsync("E2E-RM01");

                DialogMessages.Clear();
                await VoidWorkOrderAsync(page);
                var blocked = await WaitForDialogAsync(m => m.Contains(slip));
                Assert.Contains("放回原儲位", blocked);
                Assert.Contains("退料", blocked);
                Assert.Equal(1, await WoStatusAsync());

                DialogMessages.Clear();
                await OpenSlipPanelAsync(page, slip, "InternalIssue");
                await page.Locator($"#sidePanelBody form[action*='/Cancel/{slip}'] button[type='submit']").ClickAsync();
                await WaitForAsync(async () => await SlipStatusAsync(slip) == 7, "發料單取消沒有成功" + DescribeDialogs());
                Assert.Contains(DialogMessages, m => m.Contains("放回原儲位"));
                Assert.Equal(rm, await OnHandAsync("E2E-RM01"));

                await VoidWorkOrderAsync(page);
                await WaitForAsync(async () => await WoStatusAsync() == 5, "取消發料單後工單仍無法作廢" + DescribeDialogs());
                Assert.Empty(JsErrors);
            }
            finally { await CleanupAsync(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-3 委外採購單作廢解開工單；已有收貨單的委外單擋作廢
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y3_已確認委外單擋工單作廢_作廢委外單後工單可作廢_有收貨單的委外單被擋()
        {
            const string opo = "E2E-Y3OPO1", opo2 = "E2E-Y3OPO2", osr = "E2E-Y3OSR1";
            await PrepareAsync();
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                await SeedOpoAsync(opo, 1);
                await SeedOpoAsync(opo2, 1);
                await SeedFixture.ExecAsync($"""
                    INSERT INTO "OutsourceReceipts" ("OutsourceReceiptId","OutsourcePurchaseOrderId","ReceiptDate","CreatedBy","SupplierCode","TypeCode","TypeCategory","Status","ExchangeRate","TotalAmount","ApprovalStatus","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId")
                    VALUES ('{osr}','{opo2}',current_date,'admin','E2E-CO01','OI','Purchase',0,1,0,0,now(),now(),'{Plant}','1');
                    """);

                // ① 工單作廢被擋，訊息列出委外單號（兩張都列）
                DialogMessages.Clear();
                await VoidWorkOrderAsync(page);
                var blocked = await WaitForDialogAsync(m => m.Contains(opo));
                Assert.Contains("委外", blocked);
                Assert.Equal(1, await WoStatusAsync());

                // ② 已確認列有作廢鈕；有收貨單的那張按了被擋並列出收貨單號
                await GotoAsync(page, "/OutsourcePurchaseOrder?searched=true&searchText=E2E-Y3OPO");
                DialogMessages.Clear();
                await page.Locator($"[data-action='confirmPostAction'][data-url*='/Void/{opo2}']").ClickAsync();
                var receiptBlocked = await WaitForDialogAsync(m => m.Contains(osr));
                Assert.Contains("收貨", receiptBlocked);
                Assert.Equal(1, await OpoStatusAsync(opo2));

                // ③ 刪掉收貨單草稿後兩張都作廢 → 工單可作廢
                await SeedFixture.ExecAsync($"""DELETE FROM "OutsourceReceipts" WHERE "OutsourceReceiptId"='{osr}'""");
                foreach (var id in new[] { opo, opo2 })
                {
                    await GotoAsync(page, "/OutsourcePurchaseOrder?searched=true&searchText=E2E-Y3OPO");
                    await page.Locator($"[data-action='confirmPostAction'][data-url*='/Void/{id}']").ClickAsync();
                    await WaitForAsync(async () => await OpoStatusAsync(id) == 2, $"委外單 {id} 沒有作廢" + DescribeDialogs());
                }

                await VoidWorkOrderAsync(page);
                await WaitForAsync(async () => await WoStatusAsync() == 5, "委外單作廢後工單仍無法作廢" + DescribeDialogs());
                Assert.Empty(JsErrors);
            }
            finally
            {
                await SeedFixture.ExecAsync($"""
                    DELETE FROM "OutsourceReceipts" WHERE "OutsourceReceiptId"='{osr}';
                    DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" IN ('{opo}','{opo2}');
                    DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId" IN ('{opo}','{opo2}');
                    """);
                await CleanupAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-4 送審中：排程不排、桌機與手機開工都被擋並說明原因
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y4_投產送審中_排程不排_桌機與手機開工被擋並顯示原因()
        {
            await PrepareAsync();
            await ArrangeWoApprovalAsync(required: true);
            await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, "1"));
            string? calendarMark = null;
            // 種子的 E2E-WC01 底下沒有機台，派工本來就排不上；改到有機台的 E2E-WC02，「不排」才是送審閘造成的
            await SeedFixture.ExecAsync($"""UPDATE "WorkOrderOperations" SET "WorkCenterId"='E2E-WC02' WHERE "WorkOrderId"='{Wo}'""");
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                Assert.Equal(1, await WoApprovalAsync());   // PendingApproval

                // 排程：送審中工單的派工仍是待排程。排程本身必須成功——整次失敗時派工當然也沒動，那是假綠。
                // 排程只讀物化後的日段（WorkCalendarSegments），種子沒有；補全廠層級 120 天 08:00–17:00
                calendarMark = await SeedFixture.ScalarAsync<string>("""SELECT coalesce(max("Id"),0)::text FROM "WorkCalendarSegments" """);
                await SeedFixture.ExecAsync($"""
                    INSERT INTO "WorkCalendarSegments" ("PlantId","WorkCenterId","MachineCode","Date","Seq","StartMinute","EndMinute")
                    SELECT '{Plant}', NULL, NULL, d::date, 1, 480, 1020 FROM generate_series(current_date - 7, current_date + 120, interval '1 day') d;
                    """);
                var alert = await RunScheduleAsync(page);
                Assert.True(alert.Success, "排程沒有成功：" + alert.Text);
                Assert.Equal(0, await DispatchStatusAsync());

                // 若先前已排程（模擬）：桌機、手機開工都被擋，訊息一致
                await SeedFixture.ExecAsync($"""UPDATE "Dispatches" SET "Status"=1, "MachineCode"='{Machine}' WHERE "DispatchId"='{Dispatch}'""");
                Assert.Contains("尚未核准，無法開工", await DesktopStartAsync(page));
                var (text, success) = await MobileStartAsync(culture: null);
                Assert.False(success);
                Assert.Contains("尚未核准，無法開工", text);
                Assert.Equal(0, await SessionsAsync());

                // 對照：同一張改成已核准、派工回待排程，再排一次就要排上——證明上面的「不排」是送審閘造成的
                await SeedFixture.ExecAsync($"""
                    UPDATE "WorkOrders" SET "ApprovalStatus"=2 WHERE "WorkOrderId"='{Wo}';
                    UPDATE "Dispatches" SET "Status"=0, "MachineCode"=NULL WHERE "DispatchId"='{Dispatch}';
                    """);
                var again = await RunScheduleAsync(page);
                Assert.True(again.Success, "對照排程沒有成功：" + again.Text);
                Assert.Equal(1, await DispatchStatusAsync());
            }
            finally
            {
                if (calendarMark != null)
                    await SeedFixture.ExecAsync($"""DELETE FROM "WorkCalendarSegments" WHERE "Id" > {calendarMark}""");
                await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, null));
                await CleanupAsync();
            }
        }

        /// <summary>
        /// 沒有物化的工作日曆（種子即如此）、又有凍結中的派工（E2E-DP0021～23 暫停中）時，排程排不出時段。
        /// 原本回裸 500、畫面沒有 catch——按了「執行排程」毫無反應。現在要看到紅色訊息並指出日曆。
        /// </summary>
        [Fact]
        public async Task 排程_沒有工作日曆_執行排程顯示日曆錯誤訊息而非毫無反應()
        {
            await ResetFixtureAsync();
            Assert.Equal("0", await SeedFixture.ScalarAsync<string>("""SELECT count(*)::text FROM "WorkCalendarSegments" WHERE "PlantId"='P1'"""));
            var page = await NewFlowPageAsync();
            var alert = await RunScheduleAsync(page);
            Assert.False(alert.Success);
            Assert.Contains("工作日曆", alert.Text);
            Assert.Empty(JsErrors);
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-5 駁回：工單回已規劃、派工回待排程，可再投產重新送審
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y5_駁回_工單回已規劃派工回待排程_可再投產重新送審()
        {
            await PrepareAsync();
            await ArrangeWoApprovalAsync(required: true);
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                Assert.Equal(1, await WoApprovalAsync());
                await SeedFixture.ExecAsync($"""UPDATE "Dispatches" SET "Status"=1, "MachineCode"='{Machine}' WHERE "DispatchId"='{Dispatch}'""");

                var reqId = await LatestRequestAsync();
                await GotoAsync(page, "/Approval/PendingIndex");
                await page.Locator($"button[data-action='openPanel'][data-url*='/Approval/Process/{reqId}']").ClickAsync();
                var form = page.Locator("#crudPanelBody form[data-ajax='true']");
                await Assertions.Expect(form).ToBeVisibleAsync(new() { Timeout = 10_000 });
                await form.Locator("textarea[name='comment']").FillAsync("E2E Y-5 駁回");
                await form.Locator("button[name='approvalAction'][value='2']").ClickAsync();

                await WaitForAsync(async () => await WoStatusAsync() == 0 && await WoApprovalAsync() == 3,
                    $"駁回後工單狀態 {await WoStatusAsync()}／簽核 {await WoApprovalAsync()}" + DescribeDialogs());
                Assert.Equal(0, await DispatchStatusAsync());

                // 可以再按投產，重新送審
                await ReleaseAsync(page);
                Assert.Equal(1, await WoApprovalAsync());
                Assert.NotEqual(reqId, await LatestRequestAsync());
                Assert.Empty(JsErrors);
            }
            finally { await CleanupAsync(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-6 撤回：工單回已規劃、派工回待排程；殘留可開工的派工被擋並說明
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y6_撤回_工單回已規劃派工回待排程_開工被擋顯示不是已投產()
        {
            await PrepareAsync();
            await ArrangeWoApprovalAsync(required: true);
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                await SeedFixture.ExecAsync($"""UPDATE "Dispatches" SET "Status"=1, "MachineCode"='{Machine}' WHERE "DispatchId"='{Dispatch}'""");

                await OpenWorkOrderAsync(page);
                await page.ClickAsync("#sidePanelBody [data-action='withdrawWorkOrder']");
                await WaitForAsync(async () => await WoStatusAsync() == 0, "撤回後工單不是已規劃" + DescribeDialogs());
                Assert.Equal(0, await DispatchStatusAsync());
                Assert.Equal(3, await IntAsync($"""SELECT "Status"::text FROM "ApprovalRequests" WHERE "Id"='{await LatestRequestAsync()}'"""));

                // 殘留可開工狀態的派工（模擬）：桌機、手機都被擋
                await SeedFixture.ExecAsync($"""UPDATE "Dispatches" SET "Status"=1 WHERE "DispatchId"='{Dispatch}'""");
                Assert.Contains("不是「已投產」或「生產中」", await DesktopStartAsync(page));
                var (text, success) = await MobileStartAsync(culture: null);
                Assert.False(success);
                Assert.Contains("不是「已投產」或「生產中」", text);
                Assert.Equal(0, await SessionsAsync());
            }
            finally { await CleanupAsync(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-7 強制送審：門檻很高時一般工單直接核准、重工工單仍送審；參數一覽看得到三個新參數
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y7_門檻很高_一般工單直接核准_重工工單強制送審_參數一覽列出三個新參數()
        {
            await PrepareAsync();
            await ArrangeWoApprovalAsync(required: true);
            await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, "99999999"), (ReworkKey, "true"));
            try
            {
                var page = await NewFlowPageAsync();
                await ReleaseAsync(page);
                Assert.Equal(2, await WoApprovalAsync());   // 低於門檻：直接核准

                await GotoAsync(page, "/PlantParamOverview");
                var overview = await page.Locator("body").InnerTextAsync();
                foreach (var key in new[] { "mfg_wo_approval_outsource", ReworkKey, "mfg_wo_approval_on_delay" })
                    Assert.Contains(key, overview);

                await PrepareAsync(type: 1);   // 同一張改成重工工單
                await ReleaseAsync(page);
                Assert.Equal(1, await WoApprovalAsync());   // 不管門檻，一律送審
                Assert.Empty(JsErrors);
            }
            finally
            {
                await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, null), (ReworkKey, null));
                await CleanupAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-8 沒有簽核流程卻超過門檻：直接核准並提醒（單張、整批兩個入口）
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y8_沒有簽核流程卻超過門檻_單張投產_跳出提醒並直接核准()
        {
            await PrepareAsync();
            await ArrangeWoApprovalAsync(required: false);
            await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, "1"));
            try
            {
                var page = await NewFlowPageAsync();
                DialogMessages.Clear();
                await ReleaseAsync(page);
                Assert.Equal(2, await WoApprovalAsync());
                var warn = await WaitForDialogAsync(m => m.Contains(Wo) && m.Contains("簽核設定"));
                Assert.Contains("已直接核准", warn);
            }
            finally
            {
                await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, null));
                await CleanupAsync();
            }
        }

        /// <summary>整批投產有兩個入口（生產批明細、批次投產畫面），兩個都要附上提醒。</summary>
        [Theory]
        [InlineData("/ProductionBatch?searched=true&searchText=E2E-BATCH01", "/ProductionBatch/Details/E2E-BATCH01")]
        [InlineData("/BatchRelease?searched=true&searchText=E2E-BATCH01", "/BatchRelease/Details/E2E-BATCH01")]
        public async Task Y8_沒有簽核流程卻超過門檻_整批投產_成功訊息附提醒(string list, string details)
        {
            await PrepareAsync();
            await ArrangeWoApprovalAsync(required: false);
            await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, "1"));
            try
            {
                var page = await NewFlowPageAsync();
                await GotoAsync(page, list);
                await page.Locator($"[data-url*='{details}']").First.ClickAsync();
                var btn = page.Locator("#sidePanelBody [data-action='releaseBatch']");
                await Assertions.Expect(btn).ToBeVisibleAsync(new() { Timeout = 10_000 });
                DialogMessages.Clear();
                await btn.ClickAsync();

                await WaitForAsync(async () => await WoStatusAsync() == 1, "整批投產後工單沒有投產" + DescribeDialogs());
                Assert.Equal(2, await WoApprovalAsync());
                var warn = await WaitForDialogAsync(m => m.Contains(Wo) && m.Contains("簽核設定"));
                Assert.Contains("已直接核准", warn);
            }
            finally
            {
                await PlantParamUi.SetAsync(_fixture, Plant, (ThresholdKey, null));
                await CleanupAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Y-9 英文介面：作廢被擋、委外單作廢確認框、手機開工被擋——沒有中文、沒有原始鍵名
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task Y9_英文介面_作廢被擋_委外單作廢確認_手機開工被擋_都是英文()
        {
            const string slip = "E2E-Y9MS1", opo = "E2E-Y9OPO1";
            await PrepareAsync();
            var page = await NewFlowPageAsync();
            await SetCultureAsync(page.Context, "en-US");
            try
            {
                await ReleaseAsync(page);
                await SeedFixture.ExecAsync($"""
                    DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}');
                    DELETE FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}';
                    INSERT INTO "MaterialSlips" ("SlipId","SlipNo","Category","Status","SlipDate","SourceDocType","SourceDocId","PlantId","RequestedBy","CreatedAt","UpdatedAt")
                    VALUES ('{slip}','{slip}',0,4,current_date,'WorkOrder','{Wo}','{Plant}','admin',now(),now());
                    INSERT INTO "MaterialSlipLines" ("SlipId","LineNo","ProductCode","RequestedQty","ConfirmedQty","UomCode","ReturnedQty")
                    VALUES ('{slip}',1,'E2E-RM01',1,1,'E2E-EA',0);
                    """);

                DialogMessages.Clear();
                await VoidWorkOrderAsync(page);
                AssertEnglish(await WaitForDialogAsync(m => m.Contains(slip)), "工單作廢被擋（已確認發料單）");

                await SeedFixture.ExecAsync($"""DELETE FROM "MaterialSlipLines" WHERE "SlipId"='{slip}'; DELETE FROM "MaterialSlips" WHERE "SlipId"='{slip}';""");
                await SeedOpoAsync(opo, 1);
                DialogMessages.Clear();
                await VoidWorkOrderAsync(page);
                AssertEnglish(await WaitForDialogAsync(m => m.Contains(opo)), "工單作廢被擋（委外單）");

                await GotoAsync(page, $"/OutsourcePurchaseOrder?searched=true&searchText={opo}");
                DialogMessages.Clear();
                await page.Locator($"[data-action='confirmPostAction'][data-url*='/Void/{opo}']").ClickAsync();
                await WaitForAsync(async () => await OpoStatusAsync(opo) == 2, "委外單沒有作廢" + DescribeDialogs());
                Assert.NotEmpty(DialogMessages);
                foreach (var m in DialogMessages) AssertEnglish(m, "委外單作廢確認框");

                // 手機：送審中工單開工被擋
                await SeedFixture.ExecAsync($"""
                    UPDATE "WorkOrders" SET "ApprovalStatus"=1 WHERE "WorkOrderId"='{Wo}';
                    UPDATE "Dispatches" SET "Status"=1, "MachineCode"='{Machine}' WHERE "DispatchId"='{Dispatch}';
                    """);
                var (text, success) = await MobileStartAsync(culture: "en-US");
                Assert.False(success);
                AssertEnglish(text, "手機開工被擋");
                Assert.Contains("approv", text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await SetCultureAsync(page.Context, "zh-TW");
                await SeedFixture.ExecAsync($"""
                    DELETE FROM "MaterialSlipLines" WHERE "SlipId"='{slip}'; DELETE FROM "MaterialSlips" WHERE "SlipId"='{slip}';
                    DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId"='{opo}';
                    DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId"='{opo}';
                    """);
                await CleanupAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // 準備與還原
        // ════════════════════════════════════════════════════════════════════

        /// <summary>重跑種子，把 E2E-WO0001 改回「已規劃、草稿」、不做投產重算、沒有發料單，派工回待排程。</summary>
        private static async Task PrepareAsync(int type = 0)
        {
            await ResetFixtureAsync();
            await SeedFixture.ExecAsync($"""
                DELETE FROM "ApprovalLogs" WHERE "RequestId" IN (SELECT "Id" FROM "ApprovalRequests" WHERE "DocumentType"='WorkOrder' AND "DocumentId"='{Wo}');
                DELETE FROM "ApprovalRequests" WHERE "DocumentType"='WorkOrder' AND "DocumentId"='{Wo}';
                DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}');
                DELETE FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}';
                DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}';
                UPDATE "WorkOrders" SET "Status"=0, "ApprovalStatus"=0, "ApprovalRequestId"=NULL, "GrossDemandQty"=0, "Type"={type} WHERE "WorkOrderId"='{Wo}';
                UPDATE "Dispatches" SET "Status"=0, "MachineCode"=NULL WHERE "DispatchId"='{Dispatch}';
                -- 種子的 E2E-OPO01～03 掛在這張工單上，會（正確地）擋下作廢；本場次自建的委外單另外處理
                UPDATE "OutsourcePurchaseOrders" SET "Status"=2
                WHERE "OutsourcePurchaseOrderId" NOT LIKE 'E2E-Y%'
                  AND "OutsourcePurchaseOrderId" IN (SELECT "OutsourcePurchaseOrderId" FROM "OutsourcePurchaseOrderLines" WHERE "SourceWorkOrderId"='{Wo}');
                """);
        }

        private static async Task CleanupAsync()
        {
            await SeedFixture.ExecAsync($"""
                DELETE FROM "DocumentApprovalConfigs" WHERE "Id"='{ConfigId}';
                DELETE FROM "ApprovalLogs" WHERE "RequestId" IN (SELECT "Id" FROM "ApprovalRequests" WHERE "DocumentType"='WorkOrder' AND "DocumentId"='{Wo}');
                DELETE FROM "ApprovalRequests" WHERE "DocumentType"='WorkOrder' AND "DocumentId"='{Wo}';
                DELETE FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}';
                UPDATE "WorkOrders" SET "Type"=0 WHERE "WorkOrderId"='{Wo}';
                """);
            await ResetFixtureAsync();
        }

        /// <summary>工單簽核設定：角色 1（Admin）一關；required=false 即「作業類型簽核設定把工單關掉」。</summary>
        private static Task ArrangeWoApprovalAsync(bool required) => SeedFixture.ExecAsync($"""
            INSERT INTO "ApprovalTemplates" ("Id","Name","TargetDocumentType","SkipSameApprover","PlantId","CreatedByRoleId")
            VALUES ('{TemplateId}','E2E 工單簽核','WorkOrder',false,'{Plant}','1')
            ON CONFLICT ("Id") DO UPDATE SET "SkipSameApprover"=false, "PlantId"='{Plant}';
            INSERT INTO "ApprovalSteps" ("Id","TemplateId","Sequence","ApproverType","TargetRoleId")
            VALUES ('{StepId}','{TemplateId}',1,2,'1')
            ON CONFLICT ("Id") DO UPDATE SET "TemplateId"='{TemplateId}', "Sequence"=1, "ApproverType"=2, "TargetRoleId"='1';
            DELETE FROM "DocumentApprovalConfigs" WHERE "DocumentType"='WorkOrder' AND "PlantId"='{Plant}';
            INSERT INTO "DocumentApprovalConfigs" ("Id","DocumentType","DocumentTypeName","IsApprovalRequired","ApprovalTemplateId","PlantId")
            VALUES ('{ConfigId}','WorkOrder','工單',{(required ? "true" : "false")},'{TemplateId}','{Plant}');
            """);

        private static Task SeedOpoAsync(string id, int status) => SeedFixture.ExecAsync($"""
            DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId"='{id}';
            DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId"='{id}';
            INSERT INTO "OutsourcePurchaseOrders" ("OutsourcePurchaseOrderId","SupplierCode","OrderDate","Status","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
            VALUES ('{id}','E2E-CO01',current_date,{status},'{Plant}','1',now(),now());
            INSERT INTO "OutsourcePurchaseOrderLines" ("OutsourcePurchaseOrderId","LineNo","SourceWorkOrderId","OperationSeq","ProductCode","Qty")
            SELECT '{id}', 1, w."WorkOrderId", 10, w."ProductCode", 1 FROM "WorkOrders" w WHERE w."WorkOrderId"='{Wo}';
            """);

        private async Task SetCultureAsync(IBrowserContext ctx, string culture) =>
            await ctx.AddCookiesAsync(new[]
            {
                new Cookie { Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = _fixture.BaseUrl },
            });

        // ════════════════════════════════════════════════════════════════════
        // 畫面操作
        // ════════════════════════════════════════════════════════════════════

        private async Task OpenWorkOrderAsync(IPage page)
        {
            await GotoAsync(page, $"/WorkOrder?searched=true&searchText={Wo}&pageSize=50");
            await page.Locator($"[data-url$='/WorkOrder/Details/{Wo}']").First.ClickAsync();
            await page.WaitForSelectorAsync("#sidePanelBody .modal-footer");
        }

        /// <summary>按投產（確認框自動接受），等工單變「已投產」。</summary>
        private async Task ReleaseAsync(IPage page)
        {
            await OpenWorkOrderAsync(page);
            await page.ClickAsync("#sidePanelBody [data-action='releaseWorkOrder']");
            await WaitForAsync(async () => await WoStatusAsync() == 1, "投產後工單不是已投產" + DescribeDialogs());
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        /// <summary>按作廢（確認框自動接受），回傳後端回應本文——失敗訊息附在斷言裡，不必只靠 alert。</summary>
        private async Task<string> VoidWorkOrderAsync(IPage page)
        {
            await OpenWorkOrderAsync(page);
            var resp = page.WaitForResponseAsync(r => r.Url.Contains($"/WorkOrder/Void/{Wo}") && r.Request.Method == "POST");
            await page.ClickAsync("#sidePanelBody [data-action='voidWorkOrder']");
            var body = await (await resp).TextAsync();
            DialogMessages.Add("[Void 回應] " + body);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            return body;
        }

        /// <summary>排程畫面按「執行排程」（挪動既有派工的確認框自動接受），回傳結果列的文字與是否為成功色。</summary>
        private async Task<(string Text, bool Success)> RunScheduleAsync(IPage page)
        {
            await GotoAsync(page, "/Schedule");
            await page.ClickAsync("#btnRun");
            var alert = page.Locator("#runResultAlert");
            await Assertions.Expect(alert).Not.ToHaveClassAsync(new Regex(@"\bd-none\b"), new() { Timeout = 60_000 });
            var cls = await alert.GetAttributeAsync("class") ?? "";
            return (await alert.InnerTextAsync(), cls.Contains("alert-success"));
        }

        /// <summary>桌機派工「開工」：回傳對話框與畫面上的文字（被擋時原因出現在其中之一）。</summary>
        private async Task<string> DesktopStartAsync(IPage page)
        {
            DialogMessages.Clear();
            await GotoAsync(page, $"/Dispatch?searched=true&searchText={Dispatch}&pageSize=50");
            var row = page.Locator("table tbody tr", new() { HasText = Dispatch }).First;
            await row.Locator("[data-url*='SessionForm']").First.ClickAsync();
            await page.WaitForSelectorAsync("#panelForm", new() { Timeout = 10_000 });
            await page.Locator("#panelForm button[type='submit']").First.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
            return string.Join(" / ", DialogMessages) + " " + await page.Locator("body").InnerTextAsync();
        }

        private async Task<(string Text, bool Success)> MobileStartAsync(string? culture)
        {
            var (ctx, page) = await MobileUi.SignInAsync(_fixture);
            try
            {
                if (culture != null)
                {
                    await SetCultureAsync(ctx, culture);
                    await ctx.AddInitScriptAsync($"try {{ localStorage.setItem('mobileLang', '{culture}'); }} catch (e) {{}}");
                }
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/sessions.html?dispatchId={Dispatch}");
                var start = page.Locator("#btnStart");
                await Assertions.Expect(start).ToBeVisibleAsync(new() { Timeout = 15_000 });
                return await MobileUi.ToastAfterAsync(page, () => start.ClickAsync());
            }
            finally { await ctx.CloseAsync(); }
        }

        private async Task<string> WaitForDialogAsync(Func<string, bool> match)
        {
            for (var i = 0; i < 60; i++)
            {
                var hit = DialogMessages.LastOrDefault(m => !m.StartsWith("[Void 回應]") && match(m));
                if (hit != null) return hit;
                await Task.Delay(250);
            }
            Assert.Fail("等不到預期的對話框" + DescribeDialogs());
            return "";
        }

        private static void AssertEnglish(string text, string where)
        {
            Assert.False(Cjk.IsMatch(text), $"{where}：英文介面出現中文——{text}");
            Assert.DoesNotMatch(@"\b[A-Z][a-z]+[A-Z][A-Za-z]*(Fmt|Slip|Po|Void|Start)\b", text);   // 原始資源鍵名
        }

        // ════════════════════════════════════════════════════════════════════
        // 資料庫讀取
        // ════════════════════════════════════════════════════════════════════

        private static async Task<int?> IntAsync(string sql) =>
            int.TryParse(await SeedFixture.ScalarAsync<string>(sql), out var v) ? v : null;

        private static Task<int?> WoStatusAsync() => IntAsync($"""SELECT "Status"::text FROM "WorkOrders" WHERE "WorkOrderId"='{Wo}'""");
        private static Task<int?> WoApprovalAsync() => IntAsync($"""SELECT "ApprovalStatus"::text FROM "WorkOrders" WHERE "WorkOrderId"='{Wo}'""");
        private static Task<int?> DispatchStatusAsync() => IntAsync($"""SELECT "Status"::text FROM "Dispatches" WHERE "DispatchId"='{Dispatch}'""");
        private static Task<int?> OpoStatusAsync(string id) => IntAsync($"""SELECT "Status"::text FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId"='{id}'""");
        private static Task<int?> SessionsAsync() => IntAsync($"""SELECT count(*)::text FROM "ProductionSessions" WHERE "DispatchId"='{Dispatch}'""");

        private static async Task<string> LatestRequestAsync()
        {
            var id = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "Id" FROM "ApprovalRequests" WHERE "DocumentType"='WorkOrder' AND "DocumentId"='{Wo}' ORDER BY "CreatedAt" DESC LIMIT 1""");
            Assert.False(string.IsNullOrEmpty(id), "投產送審後沒有簽核單");
            return id!;
        }

        private static async Task<string> SingleSlipAsync()
        {
            await WaitForAsync(async () => await IntAsync(
                $"""SELECT count(*)::text FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}' AND "Category"=0""") == 1, "投產後沒有自動產生發料單");
            return (await SeedFixture.ScalarAsync<string>(
                $"""SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{Wo}' AND "Category"=0"""))!;
        }

        private static async Task<decimal> OnHandAsync(string product) =>
            decimal.Parse(await SeedFixture.ScalarAsync<string>(
                $"""SELECT coalesce(sum("QtyOnHand"),0)::text FROM "Inventories" WHERE "ProductCode"='{product}'""") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
    }
}
