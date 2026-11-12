using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 場次 W／X 端到端驗收測試的共用輔助：桌機改廠別參數、手機頁登入、手機 toast、委外工單種子。
    ///
    /// <para><b>廠別參數一律經桌機「廠別管理 → 編輯 → 廠別參數」改</b>，不直接寫 PlantParams：
    /// PlantParamService 把整張表快取 10 分鐘，直接改資料表時受測 app 看不到。走畫面同時也驗了參數設定畫面本身。</para>
    ///
    /// <para>手機頁用真的伺服器（不攔截 API），在手機尺寸的 context 以 admin 從 /mobile/index.html 登入。
    /// 真手機才有的部分（相機掃碼、觸控、安裝成 App、經 tunnel 連線）不在這裡，留給週六抽看。</para>
    /// </summary>
    public abstract class AcceptanceE2EBase
    {
        protected readonly UiTestFixture _fixture;
        protected AcceptanceE2EBase(UiTestFixture fixture) => _fixture = fixture;

        protected const string Plant = "P1";
        protected const string Supplier = "E2E-CO01";
        protected const string Uom = "E2E-EA";
        protected const string NormalLoc = "E2E-LFG";
        protected const string QuarantineLoc = "QC-P1-Q01";

        // ── 廠別參數（桌機畫面）──────────────────────────────────────────────

        /// <summary>value＝null 代表取消覆寫（回到繼承）。走畫面存檔：PlantParamService 有快取，直接改資料表不會生效。</summary>
        protected Task SetPlantParamsAsync(params (string Key, string? Value)[] kv) => PlantParamUi.SetAsync(_fixture, Plant, kv);

        protected Task ClearPlantParamsAsync(params string[] keys)
            => SetPlantParamsAsync(keys.Select(k => (k, (string?)null)).ToArray());

        // ── 手機頁 ───────────────────────────────────────────────────────────

        protected Task<(IBrowserContext Context, IPage Page)> MobileAsync() => MobileUi.SignInAsync(_fixture);

        /// <summary>做一個動作，回傳它造成的下一個 toast（文字、是否為成功色）。</summary>
        protected static Task<(string Text, bool Success)> ToastAfterAsync(IPage page, Func<Task> act) => MobileUi.ToastAfterAsync(page, act);

        protected static Task FillChangeAsync(ILocator input, string value) => MobileUi.FillChangeAsync(input, value);

        /// <summary>手機委外收料：查單號、填每行（行號→數量／儲位／不良數）、送出，回傳 toast。</summary>
        protected async Task<(string Text, bool Success)> MobileReceiveOutsourceAsync(IPage page, string opo,
            decimal qty, string? loc = null, decimal? fail = null)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/outsource-receive.html");
            await page.FillAsync("#opoSearch", opo);
            await page.PressAsync("#opoSearch", "Enter");
            await page.WaitForSelectorAsync("#detailCard:not(.u-display-none) #lineRows input[data-field='qty']");
            await FillChangeAsync(page.Locator("#lineRows input[data-field='qty'][data-line='1']"), qty.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await FillChangeAsync(page.Locator("#lineRows input[data-field='lot'][data-line='1']"), "E2E-LOT");
            if (loc != null)
                await FillChangeAsync(page.Locator("#lineRows input[data-field='loc'][data-line='1']"), loc);
            if (fail != null)
                await FillChangeAsync(page.Locator("#lineRows input[data-field='fail'][data-line='1']"), fail.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return await ToastAfterAsync(page, () => page.ClickAsync("#btnReceive"));
        }

        /// <summary>手機入庫上架：挑某張單的待上架列，放到指定儲位。</summary>
        protected async Task<(string Text, bool Success)> MobilePutAwayAsync(IPage page, string docNo, string loc)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/putaway.html");
            var item = page.Locator(".pending-item", new PageLocatorOptions { HasText = docNo }).First;
            await item.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await item.ClickAsync();
            await page.FillAsync("#locationCode", loc);
            return await ToastAfterAsync(page, () => page.ClickAsync("#btnPutAway"));
        }

        /// <summary>手機進料檢驗：挑某張單、判合格（不良 0），回傳該列是否標「委外」與 toast。</summary>
        protected async Task<(bool OutsourceBadge, string Text, bool Success)> MobileIqcPassAsync(IPage page, string docNo)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/iqc.html");
            var item = page.Locator(".pending-item", new PageLocatorOptions { HasText = docNo }).First;
            await item.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            var badge = await item.Locator(".badge.bg-info").CountAsync() > 0;
            await item.ClickAsync();
            await page.FillAsync("#failQty", "0");
            var (text, ok) = await ToastAfterAsync(page, () => page.ClickAsync("#btnSubmit"));
            return (badge, text, ok);
        }

        // ── 種子 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 整品委外：品號 <paramref name="product"/>（非免驗）、工單 {id}-WO（已投產、IsOutsource）、
        /// 單一委外派工 {id}-WO-1、已確認的委外採購單 {id}-OPO（一行，單價 <paramref name="price"/>）。
        /// </summary>
        /// <para><paramref name="category"/> 不給時用 E2E-CAT（沒有預設倉儲）。手機非簡化模式收貨不填目的倉，
        /// 品檢放行的目的地退回品號類別預設倉儲——類別沒設時放行會靜默不動作，見 W-15。</para>
        protected static Task SeedWholeOutsourceAsync(string id, string product, decimal qty, decimal price,
            (string Code, string Wh, string Loc)? category = null) =>
            SeedFixture.ExecAsync($"""
                {CleanupSql(id)}
                {(category is { } c ? $"INSERT INTO \"ProductCategories\" (\"CategoryCode\",\"CategoryName\",\"DefaultWarehouseCode\",\"DefaultLocationCode\",\"IsActive\",\"CreatedAt\",\"UpdatedAt\") VALUES ('{c.Code}','E2E 類別 {c.Code}','{c.Wh}','{c.Loc}',true,now(),now()) ON CONFLICT (\"CategoryCode\") DO UPDATE SET \"DefaultWarehouseCode\"='{c.Wh}', \"DefaultLocationCode\"='{c.Loc}';" : "")}
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('{product}','E2E 委外收回品 {id}','AcceptanceE2E',NULL,'{Uom}','{Uom}','{Uom}',
                   'M',0,10,'M',1,1,0,false,false,true,0,'{category?.Code ?? "E2E-CAT"}',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true, "IqcExempt"=false, "IsLotControlled"=false, "CategoryCode"=EXCLUDED."CategoryCode";
                INSERT INTO "ProductionBatches" ("BatchId","MrpRunId","MrpRunLineSeq","ProductCode","Quantity","Priority","PlannedEndDate","Status","IsPredictedLate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-B','E2E-RUN',1,'{product}',{qty},50,now() + interval '7 day',1,false,'{Plant}','1',now(),now());
                INSERT INTO "WorkOrders" ("WorkOrderId","ProductionBatchId","ProductCode","RoutingId","Quantity","Status","ApprovalStatus","IsOutsource",
                   "PlannedStartDate","PlannedEndDate","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-WO','{id}-B','{product}','',{qty},1,2,true,now(),now() + interval '7 day','{Plant}','1',now(),now());
                INSERT INTO "WorkOrderOperations" ("WorkOrderId","OperationSeq","WorkCenterId","SetupTime","UnitTime","BatchMode","BatchQty","IsOutsource","RequiresInspection")
                VALUES ('{id}-WO',1,'',0,0,0,1,true,false);
                INSERT INTO "Dispatches" ("DispatchId","WorkOrderId","OperationSeq","Quantity","QtyDone","Status","IsOutsource","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-WO-1','{id}-WO',1,{qty},0,0,true,'{Plant}','1',now(),now());
                INSERT INTO "OutsourcePurchaseOrders" ("OutsourcePurchaseOrderId","SupplierCode","OrderDate","Status","PlantId","CreatedByRoleId","CreatedAt","UpdatedAt")
                VALUES ('{id}-OPO','{Supplier}',current_date,1,'{Plant}','1',now(),now());
                INSERT INTO "OutsourcePurchaseOrderLines" ("OutsourcePurchaseOrderId","LineNo","SourceWorkOrderId","OperationSeq","ProductCode","Qty","UomCode","UnitPrice")
                VALUES ('{id}-OPO',1,'{id}-WO',1,'{product}',{qty},'{Uom}',{price});
                """);

        /// <summary>
        /// 清掉某一組種子與它衍生的單據（收貨、應付、品檢、庫存）。庫存異動整批刪除——
        /// 只刪庫存列不刪異動的話，月結的 INV_ROLLFORWARD 稽核會擋下別支測試的關帳。
        /// </summary>
        protected static string CleanupSql(string id) => $"""
            DELETE FROM "APInvoiceLines" WHERE "APInvoiceId" IN (SELECT "APInvoiceId" FROM "APInvoices" WHERE "SourceDocId" IN (SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO'));
            DELETE FROM "APInvoices" WHERE "SourceDocId" IN (SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO');
            DELETE FROM "OutsourceReceiptLocationDetails" WHERE "OutsourceReceiptId" IN (SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO');
            DELETE FROM "OutsourceReceiptLines" WHERE "OutsourceReceiptId" IN (SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO');
            DELETE FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{id}-OPO';
            DELETE FROM "OutsourcePurchaseOrderLines" WHERE "OutsourcePurchaseOrderId"='{id}-OPO';
            DELETE FROM "OutsourcePurchaseOrders" WHERE "OutsourcePurchaseOrderId"='{id}-OPO';
            DELETE FROM "Dispatches" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrderOperations" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrderMaterials" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "WorkOrders" WHERE "WorkOrderId"='{id}-WO';
            DELETE FROM "ProductionBatches" WHERE "BatchId"='{id}-B';
            """;

        protected static string CleanupStockSql(string product) => $"""
            DELETE FROM "StockMovements" WHERE "ProductCode"='{product}';
            DELETE FROM "Inventories" WHERE "ProductCode"='{product}';
            """;

        protected static Task<string?> LatestReceiptAsync(string opo) => SeedFixture.ScalarAsync<string>(
            $"""SELECT "OutsourceReceiptId" FROM "OutsourceReceipts" WHERE "OutsourcePurchaseOrderId"='{opo}' ORDER BY "CreatedAt" DESC LIMIT 1""");

        protected static Task<decimal> StockAsync(string product, string where) => SeedFixture.ScalarAsync<decimal>(
            $"""SELECT COALESCE(SUM("QtyOnHand"),0) FROM "Inventories" WHERE "ProductCode"='{product}' AND {where}""");
    }
}
