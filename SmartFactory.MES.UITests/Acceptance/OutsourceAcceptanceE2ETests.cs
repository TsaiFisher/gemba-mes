using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 場次 W 的端到端版：W-14（超量參數）、W-15（委外收回品品檢閘門）。
    /// 桌機改參數 → 手機頁照真的流程操作（真伺服器） → 查資料庫與桌機畫面。
    /// 服務層的判定各有整合測試；這裡驗的是畫面、參數與整條鏈串起來的結果——
    /// 尤其 W-15 非簡化模式「上架待驗倉 → 品檢合格放行 → 派工累計 → 工單入庫完成」，整合測試只驗到進入待檢清單為止。
    /// </summary>
    [Collection("UI Tests")]
    public class OutsourceAcceptanceE2ETests : AcceptanceE2EBase
    {
        public OutsourceAcceptanceE2ETests(UiTestFixture fixture) : base(fixture) { }

        private const string Simplified = "inv_simplified_mode";
        private const string RptTol = "mfg_over_report_tolerance_pct";
        private const string RptAction = "mfg_over_report_action";
        private const string OsTol = "mfg_outsource_over_receipt_tolerance_pct";
        private const string OsAction = "mfg_outsource_over_receipt_action";
        private const string OsFee = "mfg_outsource_over_receipt_fee";

        // ── W-15 ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task W15_非簡化模式_委外收回品_待驗倉品檢合格才進一般倉並累計派工_收滿工單入庫完成_完工入庫被拒()
        {
            const string id = "E2E-W15", fg = "E2E-W15FG";
            await SeedFixture.ExecAsync(CleanupStockSql(fg));
            // 類別要有預設倉儲：手機非簡化收貨不填目的倉，品檢放行靠它（沒設時放行靜默不動作，2026-10-14 回報）
            await SeedWholeOutsourceAsync(id, fg, qty: 50, price: 7, category: ("E2E-W15CAT", "E2E-WFG", NormalLoc));
            await SetPlantParamsAsync((Simplified, "false"));
            var (ctx, page) = await MobileAsync();
            try
            {
                // ① 收一部分：還沒入庫，派工不累計
                var (recvText, recvOk) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 30);
                Assert.True(recvOk, "委外收料失敗：" + recvText);
                var rec1 = await LatestReceiptAsync($"{id}-OPO");
                Assert.NotNull(rec1);
                Assert.Equal(0m, await DispatchDoneAsync(id));

                // ② 上架：非免驗品放一般倉被擋，改放待驗倉
                var (normalText, normalOk) = await MobilePutAwayAsync(page, rec1!, NormalLoc);
                Assert.False(normalOk, "非免驗的委外收回品不得直接上架到一般倉：" + normalText);
                Assert.Equal(0m, await StockAsync(fg, "true"));
                var (qText, qOk) = await MobilePutAwayAsync(page, rec1!, QuarantineLoc);
                Assert.True(qOk, "上架待驗倉失敗：" + qText);
                Assert.Equal(30m, await StockAsync(fg, $"\"LocationCode\"='{QuarantineLoc}'"));
                Assert.Equal(0m, await DispatchDoneAsync(id));

                // ③ 品檢：清單標「委外」，判合格 → 待驗倉轉一般倉、派工完工量 +30
                var (badge, iqcText, iqcOk) = await MobileIqcPassAsync(page, rec1!);
                Assert.True(badge, "進料檢驗清單上委外收回品沒有標「委外」");
                Assert.True(iqcOk, "品檢送出失敗：" + iqcText);
                Assert.Equal(0m, await StockAsync(fg, $"\"LocationCode\"='{QuarantineLoc}'"));
                Assert.Equal(30m, await StockAsync(fg, $"\"LocationCode\"='{NormalLoc}'"));
                Assert.Equal(30m, await DispatchDoneAsync(id));
                Assert.Equal(2, await WoStatusAsync(id));   // 生產中

                // ④ 收滿：派工結案、工單直接入庫完成
                Assert.True((await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 20)).Success);
                var rec2 = await LatestReceiptAsync($"{id}-OPO");
                Assert.NotEqual(rec1, rec2);
                Assert.True((await MobilePutAwayAsync(page, rec2!, QuarantineLoc)).Success);
                Assert.True((await MobileIqcPassAsync(page, rec2!)).Success);
                Assert.Equal(50m, await DispatchDoneAsync(id));
                Assert.Equal(5, await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "Dispatches" WHERE "DispatchId"='{id}-WO-1'"""));
                Assert.Equal(4, await WoStatusAsync(id));   // 入庫完成（Closed），不是完工待入庫

                // ⑤ 桌機完工入庫：清單只列「完工、待入庫」的工單，這張已入庫完成，不在清單上也就無從再入庫
                var desk = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(desk);
                await desk.GotoAsync($"{_fixture.BaseUrl}/ProductionCompletion?searched=true&pageSize=200");
                await desk.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Equal(0, await desk.Locator($"[data-url*='{id}-WO']").CountAsync());
                Assert.Equal(4, await WoStatusAsync(id));
                Assert.Equal(50m, await StockAsync(fg, $"\"LocationCode\"<>'{QuarantineLoc}'"));
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync(Simplified);
                await SeedFixture.ExecAsync(CleanupSql(id) + CleanupStockSql(fg));
            }
        }

        [Fact]
        public async Task W15_非簡化模式_收回品類別沒有預設倉儲_手機收料擋下並點名品號()
        {
            // 原本照收：之後品檢判「合格」卻沒有地方放，料靜默留在待驗倉、派工不累計（2026-10-14 驗收發現）
            const string id = "E2E-W15N", fg = "E2E-W15NFG";
            await SeedFixture.ExecAsync(CleanupStockSql(fg));
            await SeedWholeOutsourceAsync(id, fg, qty: 50, price: 7);   // 類別 E2E-CAT 沒有預設倉儲
            await SetPlantParamsAsync((Simplified, "false"));
            var (ctx, page) = await MobileAsync();
            try
            {
                var (text, ok) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 30);
                Assert.False(ok, "類別沒有預設倉儲仍收下：" + text);
                Assert.Contains(fg, text);
                Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                    $"""SELECT count(*) FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO' AND "Status" NOT IN (0, 4)"""));
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync(Simplified);
                await SeedFixture.ExecAsync(CleanupSql(id) + CleanupStockSql(fg));
            }
        }

        // ── W-14 廠內超量 ─────────────────────────────────────────────────────

        [Fact]
        public async Task W14_廠內超量_容許率5趴_超過擋下且訊息寫出累計計畫上限容許率_容許內照收()
        {
            const string id = "E2E-W14R";
            await SeedInHouseDispatchAsync(id, quantity: 100, done: 10);
            await SetPlantParamsAsync((RptTol, "5"), (RptAction, "Block"));
            var (ctx, page) = await MobileAsync();
            try
            {
                await OpenReportAsync(page, id);
                var (overText, overOk) = await ReportAsync(page, 96);    // 累計 106 > 105
                Assert.False(overOk, "超過容許上限仍被收下：" + overText);
                foreach (var n in new[] { "106", "100", "105", "5" })
                    Assert.Contains(n, overText);
                Assert.Equal(10m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "QtyDone" FROM "Dispatches" WHERE "DispatchId"='{id}-D'"""));

                var (okText, ok) = await ReportAsync(page, 95);           // 累計 105＝上限
                Assert.True(ok, "容許率內被擋：" + okText);
                Assert.Equal(105m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "QtyDone" FROM "Dispatches" WHERE "DispatchId"='{id}-D'"""));
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync(RptTol, RptAction);
                await SeedFixture.ExecAsync(InHouseCleanupSql(id));
            }
        }

        [Fact]
        public async Task W14_廠內超量_需主管確認_沒有超量核准權限擋下_手機模組權限勾選後照收()
        {
            const string id = "E2E-W14S";
            await SeedInHouseDispatchAsync(id, quantity: 100, done: 10);
            await SetPlantParamsAsync((RptAction, "Supervisor"));
            await SetOverReportApproveAsync(false);
            var (ctx, page) = await MobileAsync();
            try
            {
                await OpenReportAsync(page, id);
                var (deniedText, deniedOk) = await ReportAsync(page, 100);   // 累計 110
                Assert.False(deniedOk, "沒有超量核准權限卻收下了：" + deniedText);
                Assert.Contains("110", deniedText);

                await SetOverReportApproveAsync(true);
                var (okText, ok) = await ReportAsync(page, 100);
                Assert.True(ok, "有超量核准權限仍被擋：" + okText);
                Assert.Equal(110m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "QtyDone" FROM "Dispatches" WHERE "DispatchId"='{id}-D'"""));
            }
            finally
            {
                await ctx.DisposeAsync();
                await SetOverReportApproveAsync(true);   // 還原 Admin 的手機權限（種子 ㉓ 預設全開）
                await ClearPlantParamsAsync(RptAction);
                await SeedFixture.ExecAsync(InHouseCleanupSql(id));
            }
        }

        // ── W-14 委外超收 ─────────────────────────────────────────────────────

        [Fact]
        public async Task W14_委外超收_容許10趴_超過拒收_容許內照收_設不付時應付只算到訂購量且備註記合約單價與計費量()
        {
            const string id = "E2E-W14O", fg = "E2E-W14OFG";
            await SeedFixture.ExecAsync(CleanupStockSql(fg));
            await SeedWholeOutsourceAsync(id, fg, qty: 50, price: 7);
            await SetPlantParamsAsync((Simplified, "true"), (OsTol, "10"), (OsAction, "Block"), (OsFee, "NoPay"));
            var (ctx, page) = await MobileAsync();
            try
            {
                var (blockText, blockOk) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 56, loc: NormalLoc);   // 上限 55
                Assert.False(blockOk, "超過容許上限仍收下：" + blockText);
                Assert.Contains("56", blockText);
                Assert.Contains("55", blockText);
                Assert.Null(await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO' AND "Status" <> 0"""));

                var (okText, ok) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 54, loc: NormalLoc);
                Assert.True(ok, "容許率內被擋：" + okText);
                var rec = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO' AND "Status" <> 0 ORDER BY "CreatedAt" DESC LIMIT 1""");
                Assert.NotNull(rec);

                // 應付只算訂購量 50 × 7 = 350（未稅），收貨明細備註寫出合約單價 7 與計費量 50
                var billed = await SeedFixture.ScalarAsync<decimal>($"""
                    SELECT COALESCE(SUM(l."LineAmount"),0) FROM "APInvoiceLines" l JOIN "APInvoices" h ON h."APInvoiceId"=l."APInvoiceId"
                    WHERE h."SourceDocId"='{rec}'
                    """);
                Assert.Equal(350m, Math.Round(billed, 2));
                var remark = await SeedFixture.ScalarAsync<string>($"""SELECT "Remarks" FROM "OutsourceReceiptLines" WHERE "OutsourceReceiptId"='{rec}' AND "LineNo"=1""") ?? "";
                Assert.Contains("合約單價 7", remark);
                Assert.Contains("計費 50", remark);
                Assert.Contains("實收 54", remark);

                // 桌機收貨單看得到明細備註（2026-10-14 補：原本畫面不顯示）
                var desk = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(desk);
                await desk.GotoAsync($"{_fixture.BaseUrl}/OutsourceReceipt?searched=true&searchOpo={id}-OPO");
                await desk.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await desk.Locator($"[data-url$='/OutsourceReceipt/Details/{rec}']").First.ClickAsync();
                await Assertions.Expect(desk.Locator("#sidePanelBody .line-remarks")).ToContainTextAsync("合約單價 7");
                await Assertions.Expect(desk.Locator("#sidePanelBody .line-remarks")).ToContainTextAsync("計費 50");
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync(Simplified, OsTol, OsAction, OsFee);
                await SeedFixture.ExecAsync(CleanupSql(id) + CleanupStockSql(fg));
            }
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static Task<decimal> DispatchDoneAsync(string id) =>
            SeedFixture.ScalarAsync<decimal>($"""SELECT "QtyDone" FROM "Dispatches" WHERE "DispatchId"='{id}-WO-1'""");

        private static Task<int> WoStatusAsync(string id) =>
            SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO'""");

        private static Task SetOverReportApproveAsync(bool allowed) => SeedFixture.ExecAsync($"""
            INSERT INTO "MobileRolePermissions" ("Id","RoleId","ModuleActionId","IsAllowed")
            VALUES ('e2e-mrp-ACT-MF-OVERQTY','1','ACT-MF-OVERQTY',{(allowed ? "true" : "false")})
            ON CONFLICT ("RoleId","ModuleActionId") DO UPDATE SET "IsAllowed"={(allowed ? "true" : "false")};
            """);

        /// <summary>廠內工單＋已上工的派工（{id}-D，量 quantity、已報 done），工作階段 {id}-S 屬於 admin。</summary>
        private static Task SeedInHouseDispatchAsync(string id, decimal quantity, decimal done) => SeedFixture.ExecAsync($"""
            {InHouseCleanupSql(id)}
            INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority","PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
            VALUES ('{id}-B','E2E-RUN',1,'E2E-FG01',{quantity},50,now() + interval '7 day',1,false,'{Plant}','1',now(),now());
            INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status","ApprovalStatus","IsOutsource",
               "PlannedStartDate","PlannedEndDate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
            VALUES ('{id}-WO','{id}-B','E2E-FG01','E2E-RT01',{quantity},2,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now());
            INSERT INTO "WorkOrderOperations" ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime","BatchMode","BatchQty","IsOutsource","RequiresInspection")
            VALUES ('{id}-WO',10,'E2E 加工','E2E-WC01',0,1,0,1,false,false);
            INSERT INTO "Dispatches" ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status","IsOutsource","MachineCode","ScheduledStart","ScheduledEnd","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
            VALUES ('{id}-D','{id}-WO',10,{quantity},{done},4,false,'E2E-M01',now(),now() + interval '2 hour','{Plant}','1',now(),now());
            INSERT INTO "ProductionSessions" ("SessionId","DispatchId","WorkOrderId","Status","StartAt","OperatorId","MachineCode","PlantId","CreatedAt","UpdatedAt")
            SELECT '{id}-S','{id}-D','{id}-WO',0,now() - interval '1 hour',u."Id",'E2E-M01','{Plant}',now(),now()
            FROM "AspNetUsers" u WHERE u."UserName"='admin';
            """);

        private static string InHouseCleanupSql(string id) => $"""
            DELETE FROM "ProductionReports" WHERE "DispatchId"='{id}-D';
            DELETE FROM "ProductionSessions" WHERE "DispatchId"='{id}-D';
            DELETE FROM "Dispatches" WHERE "DispatchId"='{id}-D';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "ProductionBatches" WHERE "BatchId"='{id}-B';
            """;

        private async Task OpenReportAsync(IPage page, string id)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/reports.html?dispatchId={id}-D&sessionId={id}-S");
            await page.WaitForSelectorAsync("#progressInfo .progress");
        }

        private static Task<(string Text, bool Success)> ReportAsync(IPage page, decimal qtyOk) =>
            ToastAfterAsync(page, async () =>
            {
                await page.FillAsync("#qtyOk", qtyOk.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await page.FillAsync("#qtyNg", "0");
                await page.ClickAsync("#btnSubmit");
            });
    }
}
