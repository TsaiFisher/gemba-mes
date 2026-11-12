using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 場次 X 的端到端版：X-1（送外料收貨時自動扣）、X-2（手機委外收料當場驗收）、X-5（途程工序委外停用）。
    /// 參數經桌機廠別參數畫面設定，收料走手機頁＋真伺服器，結果查資料庫。
    /// </summary>
    [Collection("UI Tests")]
    public class OutsourceSimplifiedE2ETests : AcceptanceE2EBase
    {
        public OutsourceSimplifiedE2ETests(UiTestFixture fixture) : base(fixture) { }

        private const string Simplified = "inv_simplified_mode";
        private const string IssueOnReceipt = "mfg_outsource_issue_on_receipt";
        private const string InspectAtReceipt = "mfg_outsource_inspect_at_receipt";

        // ── X-1 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X1_送外料收貨時自動扣_依累計收回比例_手動發過的不重扣_類別沒預設倉儲時擋下並說明()
        {
            const string id = "E2E-X1", fg = "E2E-X1FG", mat = "E2E-X1MAT";
            const string idNo = "E2E-X1N", fgNo = "E2E-X1NFG", matNo = "E2E-X1NMAT";
            await SeedFixture.ExecAsync(CleanupStockSql(fg) + CleanupStockSql(mat) + CleanupStockSql(fgNo) + CleanupSlipsSql(id) + CleanupSlipsSql(idNo));
            await SeedWholeOutsourceAsync(id, fg, qty: 50, price: 7);
            await SeedWholeOutsourceAsync(idNo, fgNo, qty: 50, price: 7);
            await SeedFixture.ExecAsync($"""
                INSERT INTO "ProductCategories" ("CategoryCode","CategoryName","DefaultWarehouseCode","DefaultLocationCode","IsActive","CreatedAt","UpdatedAt")
                VALUES ('E2E-X1CAT','E2E 送外料類別','E2E-WRAW','E2E-LRAW',true,now(),now())
                ON CONFLICT ("CategoryCode") DO UPDATE SET "DefaultWarehouseCode"='E2E-WRAW', "DefaultLocationCode"='E2E-LRAW';
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('{mat}','E2E 送外料','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','P',0,10,'P',1,1,0,false,true,true,0,'E2E-X1CAT',now(),now()),
                       ('{matNo}','E2E 送外料（類別無預設倉）','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','P',0,10,'P',1,1,0,false,true,true,0,'E2E-CAT',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true, "CategoryCode"=EXCLUDED."CategoryCode";
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('{Plant}','{mat}','E2E-LRAW','',100,0,10,now());
                INSERT INTO "WorkOrderMaterials" ("WorkOrderId","OperationSeq","BomSeq","ProductCode","RequiredQty","UomCode","SourceMode","IssuedQty","UnitCost")
                VALUES ('{id}-WO',1,1,'{mat}',25,'{Uom}',0,4,10),
                       ('{idNo}-WO',1,1,'{matNo}',25,'{Uom}',0,0,10);
                """);
            await SetPlantParamsAsync((Simplified, "true"), (IssueOnReceipt, "true"));
            var (ctx, page) = await MobileAsync();
            try
            {
                // 收 20：應發 25×20/50＝10，已手動發 4 → 自動補 6
                var (t1, ok1) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 20, loc: NormalLoc);
                Assert.True(ok1, "收料失敗：" + t1);
                Assert.Equal(10m, await IssuedAsync(id));
                Assert.Equal(94m, await StockAsync(mat, "\"LocationCode\"='E2E-LRAW'"));
                Assert.Equal(6m, await AutoIssuedAsync(id));

                // 收滿 50：應發 25 → 再補 15
                var (t2, ok2) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 30, loc: NormalLoc);
                Assert.True(ok2, "收料失敗：" + t2);
                Assert.Equal(25m, await IssuedAsync(id));
                Assert.Equal(79m, await StockAsync(mat, "\"LocationCode\"='E2E-LRAW'"));
                Assert.Equal(21m, await AutoIssuedAsync(id));
                Assert.Equal(2L, await SeedFixture.ScalarAsync<long>(
                    $"""SELECT count(*) FROM "MaterialSlips" WHERE "SourceDocId"='{id}-WO' AND "Category"=3 AND "Status"=5"""));

                // 送外料的類別沒有預設倉儲：整張收貨擋下，訊息點名品號
                var (t3, ok3) = await MobileReceiveOutsourceAsync(page, $"{idNo}-OPO", 10, loc: NormalLoc);
                Assert.False(ok3, "類別沒有預設倉儲仍收下：" + t3);
                Assert.Contains(matNo, t3);
                Assert.Equal(0m, await StockAsync(fgNo, "true"));
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync(Simplified, IssueOnReceipt);
                await SeedFixture.ExecAsync(CleanupSlipsSql(id) + CleanupSlipsSql(idNo) + CleanupSql(id) + CleanupSql(idNo)
                    + CleanupStockSql(fg) + CleanupStockSql(mat) + CleanupStockSql(fgNo));
            }
        }

        // ── X-2 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X2_手機委外收料當場驗收_不良0直接入一般倉並累計派工_有不良整批入待驗倉不算完工()
        {
            const string id = "E2E-X2", fg = "E2E-X2FG";
            await SeedFixture.ExecAsync(CleanupStockSql(fg));
            await SeedWholeOutsourceAsync(id, fg, qty: 50, price: 7, category: ("E2E-X2CAT", "E2E-WFG", NormalLoc));
            await SetPlantParamsAsync((Simplified, "false"), (InspectAtReceipt, "true"));
            var (ctx, page) = await MobileAsync();
            try
            {
                // 畫面：每行多一欄不良數
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/outsource-receive.html");
                await page.FillAsync("#opoSearch", $"{id}-OPO");
                await page.PressAsync("#opoSearch", "Enter");
                await page.WaitForSelectorAsync("#lineRows input[data-field='fail'][data-line='1']");

                var (t1, ok1) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 30, fail: 0);
                Assert.True(ok1, "收料失敗：" + t1);
                Assert.Equal(30m, await StockAsync(fg, $"\"LocationCode\"='{NormalLoc}'"));
                Assert.Equal(30m, await DispatchDoneAsync(id));

                var (t2, ok2) = await MobileReceiveOutsourceAsync(page, $"{id}-OPO", 20, fail: 1);
                Assert.True(ok2, "收料失敗：" + t2);
                Assert.Equal(20m, await StockAsync(fg, $"\"LocationCode\"='{QuarantineLoc}'"));
                Assert.Equal(30m, await StockAsync(fg, $"\"LocationCode\"='{NormalLoc}'"));
                Assert.Equal(30m, await DispatchDoneAsync(id));   // 不合格那批不算完工

                // 合格那張不必再上架：待上架清單沒有它
                var rec1 = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO' ORDER BY "CreatedAt" LIMIT 1""");
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/putaway.html");
                await page.WaitForFunctionAsync("() => !document.querySelector('#pendingList .spinner-border')");
                Assert.Equal(0, await page.Locator(".pending-item", new PageLocatorOptions { HasText = rec1! }).CountAsync());
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync(Simplified, InspectAtReceipt);
                await SeedFixture.ExecAsync(CleanupSql(id) + CleanupStockSql(fg)
                    + $"""DELETE FROM "MobileIqcRecords" WHERE "ProductCode"='{fg}';""");
            }
        }

        // ── X-3 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X3_委外工單投產自動建委外採購單_唯一報價建已確認單取報價單價_手機看得到_兩家報價不建()
        {
            const string one = "E2E-X3A", two = "E2E-X3B";
            await SeedFixture.ExecAsync($"""
                {CleanupSql(one)}{CleanupSql(two)}
                DELETE FROM "OutsourceDemands" WHERE "WorkOrderId" IN ('{one}-WO','{two}-WO');
                DELETE FROM "OutsourceQuotations" WHERE "QuotationId" LIKE 'E2E-X3%';
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('{one}FG','E2E 委外品（一家報價）','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','M',0,10,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now()),
                       ('{two}FG','E2E 委外品（兩家報價）','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','M',0,10,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true;
                INSERT INTO "OutsourceQuotations" ("QuotationId","SupplierCode","ProductCode","UnitPrice","CurrencyCode","UomCode","LeadDays","EffectiveFrom","IsActive","CreatedAt","UpdatedAt")
                VALUES ('E2E-X3Q1','{Supplier}','{one}FG',9,'E2E-CUR','{Uom}',3,current_date - 30,true,now(),now()),
                       ('E2E-X3Q2','{Supplier}','{two}FG',9,'E2E-CUR','{Uom}',3,current_date - 30,true,now(),now()),
                       ('E2E-X3Q3','E2E-AGS','{two}FG',8,'E2E-CUR','{Uom}',3,current_date - 30,true,now(),now());
                """);
            foreach (var id in new[] { one, two })
                await SeedFixture.ExecAsync($"""
                    INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority","PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                    VALUES ('{id}-B','E2E-RUN',1,'{id}FG',40,50,now() + interval '7 day',0,false,'{Plant}','1',now(),now());
                    INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status","ApprovalStatus","IsOutsource",
                       "PlannedStartDate","PlannedEndDate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                    VALUES ('{id}-WO','{id}-B','{id}FG','',40,0,2,true,now(),now() + interval '7 day','{Plant}','1',now(),now());
                    """);
            await SetPlantParamsAsync(("mfg_outsource_auto_po", "true"));
            var (ctx, mobile) = await MobileAsync();
            try
            {
                await ReleaseOnDesktopAsync(one);
                var opo = await SeedFixture.ScalarAsync<string>($"""
                    SELECT h."OutsourcePurchaseOrderId" FROM "OutsourcePurchaseOrders" h JOIN "OutsourcePurchaseOrderLines" l ON l."OutsourcePurchaseOrderId"=h."OutsourcePurchaseOrderId"
                    WHERE l."SourceWorkOrderId"='{one}-WO' AND h."Status"=1 AND h."SupplierCode"='{Supplier}'
                    """);
                Assert.True(opo != null, "唯一報價的委外工單投產後沒有自動建已確認的委外採購單");
                Assert.Equal(9m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "UnitPrice" FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId"='{opo}'"""));
                Assert.Equal(40m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "Qty" FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId"='{opo}'"""));

                await mobile.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/outsource-receive.html");
                await Assertions.Expect(mobile.Locator($"#opoList [data-id='{opo}'], #detailTitle:text-is('{opo}')").First).ToBeAttachedAsync();

                await ReleaseOnDesktopAsync(two);
                Assert.Equal(0L, await SeedFixture.ScalarAsync<long>(
                    $"""SELECT count(*) FROM "OutsourcePurchaseOrderLines" WHERE "SourceWorkOrderId"='{two}-WO'"""));
            }
            finally
            {
                await ctx.DisposeAsync();
                await ClearPlantParamsAsync("mfg_outsource_auto_po");
                foreach (var id in new[] { one, two })
                    await SeedFixture.ExecAsync($"""
                        WITH ids AS (SELECT DISTINCT "OutsourcePurchaseOrderId" FROM "OutsourcePurchaseOrderLines" WHERE "SourceWorkOrderId"='{id}-WO'),
                             dl AS (DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" IN (SELECT * FROM ids))
                        DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId" IN (SELECT * FROM ids);
                        DELETE FROM "OutsourceDemands" WHERE "WorkOrderId"='{id}-WO';
                        {CleanupSql(id)}
                        """);
                await SeedFixture.ExecAsync("""DELETE FROM "OutsourceQuotations" WHERE "QuotationId" LIKE 'E2E-X3%';""");
            }
        }

        private async Task ReleaseOnDesktopAsync(string id)
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            var dialogs = new List<string>();
            page.Dialog += async (_, d) => { lock (dialogs) dialogs.Add(d.Message); try { await d.AcceptAsync(); } catch (PlaywrightException) { } };
            await page.GotoAsync($"{_fixture.BaseUrl}/WorkOrder?searched=true&searchText={id}-WO&pageSize=50");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator($"[data-url*='/WorkOrder/Details/{id}-WO']").First.ClickAsync();
            await page.Locator("#sidePanelBody [data-action='releaseWorkOrder']").ClickAsync();
            for (var i = 0; i < 50; i++)
            {
                if (await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO'""") == 1) break;
                await Task.Delay(200);
            }
            Assert.True(await SeedFixture.ScalarAsync<int>($"""SELECT "Status" FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO'""") == 1,
                $"{id}-WO 投產沒有成功：" + string.Join(" | ", dialogs));
            await page.CloseAsync();
        }

        // ── X-4 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X4_派工轉委外_轉單確認_手機收回不入庫_扣該工序投料_派工完工量累計_加工費進工單()
        {
            const string id = "E2E-X4", fg = "E2E-X4FG", mat = "E2E-X4MAT";
            await SeedFixture.ExecAsync($"""
                {X4CleanupSql(id)}
                {CleanupStockSql(fg)}{CleanupStockSql(mat)}
                INSERT INTO "ProductCategories" ("CategoryCode","CategoryName","DefaultWarehouseCode","DefaultLocationCode","IsActive","CreatedAt","UpdatedAt")
                VALUES ('E2E-X1CAT','E2E 送外料類別','E2E-WRAW','E2E-LRAW',true,now(),now())
                ON CONFLICT ("CategoryCode") DO UPDATE SET "DefaultWarehouseCode"='E2E-WRAW', "DefaultLocationCode"='E2E-LRAW';
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('{fg}','E2E 派工轉委外成品','AcceptanceE2E','E2E-RT01','{Uom}','{Uom}','{Uom}','M',0,100,'M',1,1,0,false,true,true,0,'E2E-CAT',now(),now()),
                       ('{mat}','E2E 派工轉委外投料','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}','P',0,10,'P',1,1,0,false,true,true,0,'E2E-X1CAT',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true, "CategoryCode"=EXCLUDED."CategoryCode";
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('{Plant}','{mat}','E2E-LRAW','',50,0,10,now());
                INSERT INTO "OutsourceQuotations" ("QuotationId","SupplierCode","ProductCode","UnitPrice","CurrencyCode","UomCode","LeadDays","EffectiveFrom","IsActive","CreatedAt","UpdatedAt")
                VALUES ('E2E-X4Q1','{Supplier}','{fg}',5,'E2E-CUR','{Uom}',3,current_date - 30,true,now(),now());
                INSERT INTO "CurrencyRates" ("CurrencyCode","RateDate","BuyRate","SellRate") VALUES ('E2E-CUR',current_date,1,1) ON CONFLICT DO NOTHING;
                INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority","PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-B','E2E-RUN',1,'{fg}',20,50,now() + interval '7 day',1,false,'{Plant}','1',now(),now());
                INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status","ApprovalStatus","IsOutsource",
                   "PlannedStartDate","PlannedEndDate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-WO','{id}-B','{fg}','E2E-RT01',20,1,2,false,now(),now() + interval '7 day','{Plant}','1',now(),now());
                INSERT INTO "WorkOrderOperations" ("WorkOrderId","OperationSeq","OperationName","WorkCenterId","SetupTime","UnitTime","BatchMode","BatchQty","IsOutsource","RequiresInspection")
                VALUES ('{id}-WO',10,'E2E 加工','E2E-WC01',0,1,0,1,false,false);
                INSERT INTO "WorkOrderMaterials" ("WorkOrderId","OperationSeq","BomSeq","ProductCode","RequiredQty","UomCode","SourceMode","IssuedQty","UnitCost")
                VALUES ('{id}-WO',10,1,'{mat}',10,'{Uom}',0,0,10);
                INSERT INTO "Dispatches" ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status","IsOutsource","MachineCode","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-D','{id}-WO',10,20,0,0,false,'E2E-M01','{Plant}','1',now(),now());
                """);
            var (ctx, mobile) = await MobileAsync();
            try
            {
                // ① 排程畫面：派工「轉為委外」（同一個 data-action 處理函式）
                var desk = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(desk);
                desk.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch (PlaywrightException) { } };
                await desk.GotoAsync($"{_fixture.BaseUrl}/Schedule");
                await desk.WaitForFunctionAsync("() => typeof window.panelToOutsource === 'function'");
                await desk.EvaluateAsync($"() => window.panelToOutsource(null, {{ dataset: {{ id: '{id}-D' }} }})");
                await WaitUntilAsync($"""SELECT CASE WHEN "IsOutsource" THEN 1 ELSE 0 END FROM "Dispatches" WHERE "DispatchId"='{id}-D'""", 1, "派工沒有轉成委外");

                // ② 委外採購單 → 待轉委外清單：勾這張派工、選廠商、轉單；再確認
                await desk.GotoAsync($"{_fixture.BaseUrl}/OutsourcePurchaseOrder?searched=true");
                await desk.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await desk.Locator("[data-action='openPanel'][data-url*='/OutsourcePurchaseOrder/Worklist']").First.ClickAsync();
                var chk = desk.Locator($"#sidePanelBody .os-chk[data-key$='|{id}-WO|10']");
                await chk.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
                await chk.CheckAsync();
                await desk.Locator("#sidePanelBody select[name='supplierCode']").SelectOptionAsync(Supplier);
                await desk.Locator("#convertForm button[type='submit']").ClickAsync();
                var opo = await WaitScalarAsync($"""
                    SELECT h."OutsourcePurchaseOrderId" FROM "OutsourcePurchaseOrders" h JOIN "OutsourcePurchaseOrderLines" l ON l."OutsourcePurchaseOrderId"=h."OutsourcePurchaseOrderId"
                    WHERE l."SourceWorkOrderId"='{id}-WO' AND l."OperationSeq"=10
                    """, "轉單沒有產生委外採購單");
                Assert.Equal(5m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "UnitPrice" FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId"='{opo}'"""));

                await desk.GotoAsync($"{_fixture.BaseUrl}/OutsourcePurchaseOrder?searched=true&searchText={opo}");
                await desk.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await desk.Locator($"[data-action='confirmPostAction'][data-url*='/Confirm/{opo}']").ClickAsync();
                await WaitUntilAsync($"""SELECT "Status" FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId"='{opo}'""", 1, "委外採購單沒有確認");

                // ③ 手機收回：工序委外提示、不需儲位
                await mobile.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/outsource-receive.html");
                await mobile.FillAsync("#opoSearch", opo!);
                await mobile.PressAsync("#opoSearch", "Enter");
                await mobile.WaitForSelectorAsync("#detailCard:not(.u-display-none) #lineRows input[data-field='qty']");
                Assert.True(await mobile.Locator("#opHint").IsVisibleAsync(), "工序委外收料沒有顯示「工序委外」提示");
                Assert.Equal(0, await mobile.Locator("#lineRows input[data-field='loc']").CountAsync());
                var (t, ok) = await MobileReceiveOutsourceAsync(mobile, opo!, 20);
                Assert.True(ok, "工序委外收料失敗：" + t);

                // ④ 不入庫、扣該工序投料、派工完工量累計、加工費記到工單
                Assert.Equal(0m, await StockAsync(fg, "true"));
                Assert.Equal(10m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "IssuedQty" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{id}-WO'"""));
                Assert.Equal(40m, await StockAsync(mat, "\"LocationCode\"='E2E-LRAW'"));
                Assert.Equal(20m, await SeedFixture.ScalarAsync<decimal>($"""SELECT "QtyDone" FROM "Dispatches" WHERE "DispatchId"='{id}-D'"""));
                Assert.Equal(20m * 5m + 10m * 10m, await SeedFixture.ScalarAsync<decimal>($"""SELECT COALESCE("ActualOutsourceCost",0) FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO'"""));
            }
            finally
            {
                await ctx.DisposeAsync();
                await SeedFixture.ExecAsync(X4CleanupSql(id) + CleanupStockSql(fg) + CleanupStockSql(mat));
            }
        }

        private static string X4CleanupSql(string id) => $"""
            {CleanupSlipsSql(id)}
            DELETE FROM "APInvoiceLines" WHERE "APInvoiceId" IN (SELECT "APInvoiceId" FROM "APInvoices" WHERE "SourceDocId" IN (SELECT r."OutsourceReceiptId" FROM "OutsourceReceipts" r JOIN "OutsourcePurchaseOrderLines" l ON l."OutsourcePurchaseOrderId"=r."OutsourcePurchaseOrderId" WHERE l."SourceWorkOrderId"='{id}-WO'));
            DELETE FROM "APInvoices" WHERE "SourceDocId" IN (SELECT r."OutsourceReceiptId" FROM "OutsourceReceipts" r JOIN "OutsourcePurchaseOrderLines" l ON l."OutsourcePurchaseOrderId"=r."OutsourcePurchaseOrderId" WHERE l."SourceWorkOrderId"='{id}-WO');
            DELETE FROM "OutsourceReceiptLocationDetails" WHERE "OutsourceReceiptId" IN (SELECT r."OutsourceReceiptId" FROM "OutsourceReceipts" r JOIN "OutsourcePurchaseOrderLines" l ON l."OutsourcePurchaseOrderId"=r."OutsourcePurchaseOrderId" WHERE l."SourceWorkOrderId"='{id}-WO');
            DELETE FROM "OutsourceReceiptLines" WHERE "OutsourceReceiptId" IN (SELECT r."OutsourceReceiptId" FROM "OutsourceReceipts" r JOIN "OutsourcePurchaseOrderLines" l ON l."OutsourcePurchaseOrderId"=r."OutsourcePurchaseOrderId" WHERE l."SourceWorkOrderId"='{id}-WO');
            DELETE FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId" IN (SELECT "OutsourcePurchaseOrderId" FROM "OutsourcePurchaseOrderLines" WHERE "SourceWorkOrderId"='{id}-WO');
            WITH ids AS (SELECT DISTINCT "OutsourcePurchaseOrderId" FROM "OutsourcePurchaseOrderLines" WHERE "SourceWorkOrderId"='{id}-WO'),
                 dl AS (DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId" IN (SELECT * FROM ids))
            DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId" IN (SELECT * FROM ids);
            DELETE FROM "OutsourceDemands" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "OutsourceQuotations" WHERE "QuotationId"='E2E-X4Q1';
            DELETE FROM "CurrencyRates" WHERE "CurrencyCode"='E2E-CUR' AND "RateDate"=current_date;
            DELETE FROM "Dispatches" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "ProductionBatches" WHERE "BatchId"='{id}-B';
            """;

        private static async Task WaitUntilAsync(string sql, int expected, string failMessage)
        {
            for (var i = 0; i < 50; i++)
            {
                if (await SeedFixture.ScalarAsync<int>(sql) == expected) return;
                await Task.Delay(200);
            }
            Assert.Fail(failMessage);
        }

        private static async Task<string?> WaitScalarAsync(string sql, string failMessage)
        {
            for (var i = 0; i < 50; i++)
            {
                var v = await SeedFixture.ScalarAsync<string>(sql);
                if (v != null) return v;
                await Task.Delay(200);
            }
            Assert.Fail(failMessage);
            return null;
        }

        // ── X-5 ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task X5_途程工序委外勾選灰掉_提示改用半成品或派工轉委外()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/Routing?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator("[data-url*='/Routing/Edit/E2E-RT01']").First.ClickAsync();
            // 工序在面板的「工序」頁籤
            await page.Locator("#sidePanelBody [data-bs-target='#rtTabOps']").ClickAsync();
            await page.Locator("#rtTabOps [data-action='showOpForm'][data-seq='10']").First.ClickAsync();

            var box = page.Locator("#opOutsource");
            await Assertions.Expect(box).ToBeVisibleAsync();
            Assert.True(await box.IsDisabledAsync(), "途程工序的「外包」勾選仍可勾");
            var hint = await box.GetAttributeAsync("title") ?? "";
            Assert.Contains("半成品", hint);
            Assert.Contains("轉委外", hint);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static Task<decimal> DispatchDoneAsync(string id) =>
            SeedFixture.ScalarAsync<decimal>($"""SELECT "QtyDone" FROM "Dispatches" WHERE "DispatchId"='{id}-WO-1'""");

        private static Task<decimal> IssuedAsync(string id) =>
            SeedFixture.ScalarAsync<decimal>($"""SELECT "IssuedQty" FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{id}-WO'""");

        private static Task<decimal> AutoIssuedAsync(string id) => SeedFixture.ScalarAsync<decimal>($"""
            SELECT COALESCE(SUM(l."ConfirmedQty"),0) FROM "MaterialSlipLines" l JOIN "MaterialSlips" s ON s."SlipId"=l."SlipId"
            WHERE s."SourceDocId"='{id}-WO' AND s."Category"=3 AND s."Status"=5
            """);

        private static string CleanupSlipsSql(string id) => $"""
            DELETE FROM "MaterialSlipLines" WHERE "SlipId" IN (SELECT "SlipId" FROM "MaterialSlips" WHERE "SourceDocId"='{id}-WO');
            DELETE FROM "MaterialSlips" WHERE "SourceDocId"='{id}-WO';
            """;
    }
}
