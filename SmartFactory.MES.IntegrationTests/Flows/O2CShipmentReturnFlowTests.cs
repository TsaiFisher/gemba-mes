using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §6.1 Wave 3 的 3-2（分批出貨）、3-4（部分收款）、3-6（銷貨退回）。
    ///
    /// 既有的 <see cref="O2CFlowTests"/> 只跑了一次全額出貨，而且是**手工組出貨單**、
    /// 手工把 <c>ShippedQty</c> 加上去（原始碼裡註明「Controller 同語意」）。
    /// 「同語意」是假設不是事實：真正的累計與上限守門住在
    /// <c>GoodsShipmentController.UpdateSalesOrderShippedQtyAsync</c>，
    /// 手工回寫等於把要驗的那段跳過去。本測試一律走真正的 Controller 入口。
    ///
    /// 另外兩件既有覆蓋完全沒問過的事：
    ///   ① 退回來的貨要用**什麼成本**入庫（3-6 說「庫存回補、AR 沖回、COGS 沖回三者都要發生」，
    ///      但沒說金額要一致——不一致的話三者都「發生」了，帳還是錯的）
    ///   ② 部分收款後發票狀態與未收餘額
    /// </summary>
    [Collection("Integration")]
    public class O2CShipmentReturnFlowTests : FlowTestBase
    {
        // 廠別與代碼刻意避開 O2CFlowTests 的 ZZO2C／FLOW-FG／FLOW-SO-1：
        // 那組共用名稱連品號都沒有前綴，兩邊撞上時是互相清場，症狀不會是斷言失敗。
        protected override string Plant => "ZZO2CR";

        private const string Fg  = "FLOW-O2CR-FG";
        private const string So  = "FLOW-O2CR-SO";
        private const string Cat = "FLOW-O2CR-CAT";

        private static readonly DateOnly StockDate = new(2099, 3, 2);
        private static readonly DateOnly ShipDate  = new(2099, 3, 10);
        private static readonly DateOnly RtnDate   = new(2099, 3, 20);

        private const decimal OpeningQty  = 100m;
        private const decimal OpeningCost = 120m;
        private const decimal Price       = 300m;
        private const decimal TaxRate     = 0.05m;

        public O2CShipmentReturnFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "出貨退回流程成品"));

            using var scope = NewScope(out var db);

            // 出貨單／銷退單的表單 VM **沒有倉庫與儲位欄位**（見 GoodsShipmentLineVm），
            // 兩張單的 BuildSimplifiedLinesAsync 因此一律回退到產品類別預設倉儲。
            // 也就是說：走真正的畫面時，類別沒設預設倉儲就一行都出不掉。
            // 這正是 Wave 0 前置檢查第 4 條要確認的設定。
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Cat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Cat, CategoryName = "出貨退回流程類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                    DefaultWarehouseCode = Wh, DefaultLocationCode = Loc,
                });
            await db.SaveChangesAsync();

            var p = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == Fg);
            p.CategoryCode = Cat;
            await db.SaveChangesAsync();
        }

        // ── 前置 ────────────────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync()
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = "FLOW-O2CR-OPEN", LineNo = 1,
                ProductCode = Fg, LocationCode = Loc, LotNo = "",
                Qty = OpeningQty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom,
                UnitPrice = OpeningCost,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedSalesOrderAsync(decimal orderQty)
        {
            using var scope = NewScope(out var db);
            var so = new SalesOrder
            {
                SalesOrderId = So, OrderDate = ShipDate, CompanyCode = Customer,
                CreatedBy = UserId, ApprovalStatus = DocumentApprovalStatus.Approved,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            so.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = So, LineNo = 1, ProductCode = Fg,
                OrderQty = orderQty, UomCode = Uom,
                StockQty = orderQty, StockUomCode = Uom,
                UnitPrice = Price, TaxRate = TaxRate,
            });
            db.SalesOrders.Add(so);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 銷退退錯了的人工更正路徑（2026-10-22 使用者裁決：不自動補回應收，由人開單處理）。
        /// 作廢銷退被擋下之後，另開一張出貨單把貨出回去——必須真的走得通，否則擋下訊息就是在誤導人。
        /// 銷退不回沖訂單已出量，所以訂單已全數出貨時，掛原訂單會撞超出訂購量；不掛訂單的出貨要能開。
        /// </summary>
        [Fact]
        public async Task WrongReturn_CannotBeVoided_AndANewShipmentPutsArAndRevenueBack()
        {
            const decimal shipQty = 40m, returnQty = 10m;
            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);
            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            using (var scope = NewScope(out var db))
            {
                var r = await NewReturnController(scope, db).Void(sr, "退錯了");
                Assert.IsType<BadRequestObjectResult>(r);
            }

            // 訂單已全數出貨：掛原訂單再出會被擋（銷退不釋放訂單量），這正是提示要人「不掛訂單」的原因
            (await CreateShipmentAsync(returnQty)).AssertRejected("掛原訂單補出貨");

            // 不掛訂單的出貨：貨出回去、開出新發票（以新增前後的差集找新單——CreatedAt 同一毫秒會排錯）
            HashSet<string> before;
            using (var scope = NewScope(out var db))
                before = (await db.GoodsShipments.IgnoreQueryFilters().AsNoTracking().Where(d => d.PlantId == Plant)
                    .Select(d => d.GoodsShipmentId).ToListAsync()).ToHashSet();
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewShipmentController(scope, db);
                var vm = new GoodsShipmentFormVm
                {
                    ShipDate = RtnDate, CompanyCode = Customer, CreatedBy = UserId,
                    Lines =
                    {
                        new GoodsShipmentLineVm
                        {
                            LineNo = 1, ProductCode = Fg, ShipQty = returnQty, UomCode = Uom, StockUomCode = Uom,
                            UnitPrice = Price, ExchangeRate = 1m, TaxRate = TaxRate,
                            LineAmount = returnQty * Price, TaxAmount = returnQty * Price * TaxRate,
                            TotalAmount = returnQty * Price * (1m + TaxRate),
                        },
                    },
                };
                ReadSave(await ctrl.Save(vm), ctrl.ModelState).AssertOk("不掛訂單補出貨");
            }
            string gs2;
            using (var scope = NewScope(out var db))
                gs2 = (await db.GoodsShipments.IgnoreQueryFilters().AsNoTracking().Where(d => d.PlantId == Plant)
                    .Select(d => d.GoodsShipmentId).ToListAsync()).Except(before).Single();
            Assert.NotEqual(gs, gs2);
            await ConfirmShipmentAsync(gs2);

            using (var scope = NewScope(out var db))
            {
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(-shipQty * Price, gl.GetValueOrDefault(StandardAccounts.SalesRevenueCode));   // 收入回到 40 件
                Assert.Equal(OpeningQty - shipQty, await QtyOnHandAsync(db, Plant, Fg));
                Assert.True(await db.ARInvoices.IgnoreQueryFilters().AnyAsync(i => i.SourceDocId == gs2 && i.Status != ARInvoiceStatus.Void));
            }
        }

        /// <summary>
        /// 完整倉儲模式：出貨確認當下就自動開應收發票，貨還沒揀。原本「取消確認」只把出貨單退回草稿，
        /// 發票留著——改了數量再確認會沿用舊發票（產生發票是「已存在就回傳」），刪掉草稿則變孤兒發票
        /// （2026-10-22）。現在：發票未作廢就不准取消確認；作廢發票後可以，再確認會重開一張對的。
        /// </summary>
        [Fact]
        public async Task FullMode_CancelConfirm_WithLiveInvoice_IsBlocked_AndReconfirmIssuesACorrectInvoice()
        {
            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(40m);
            (await CreateShipmentAsync(20m)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            using (var scope = NewScope(out var db))   // 完整模式（不開簡化）：確認後停在 Confirmed、發票已開
                Assert.IsType<OkObjectResult>(await NewShipmentController(scope, db).Confirm(gs));

            using (var scope = NewScope(out var db))
                Assert.IsType<BadRequestObjectResult>(await NewShipmentController(scope, db).CancelConfirm(gs));

            await VoidShipmentInvoiceAsync(gs);
            using (var scope = NewScope(out var db))
                Assert.IsType<OkObjectResult>(await NewShipmentController(scope, db).CancelConfirm(gs));

            string voidedId;
            using (var scope = NewScope(out var db))
                voidedId = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs).Select(i => i.ARInvoiceId).SingleAsync();
            using (var scope = NewScope(out var db))
                Assert.IsType<OkObjectResult>(await NewShipmentController(scope, db).Confirm(gs));

            // 重新確認開出新發票（原本產生發票會回傳那張作廢的，出貨從此沒有有效應收）
            using (var scope = NewScope(out var db))
            {
                var live = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs && i.Status != ARInvoiceStatus.Void);
                Assert.NotEqual(voidedId, live.ARInvoiceId);
                Assert.Equal(20m * Price * (1m + TaxRate), live.TotalAmount);
                Assert.Empty((await Svc<IDataAuditService>(scope).AuditShipmentInvoiceGapAsync(Plant)).Where(f => f.DocId == gs));
            }
        }

        /// <summary>作廢出貨／銷退前要先作廢來源出貨的應收發票（2026-10-22 守門）。</summary>
        private async Task VoidShipmentInvoiceAsync(string gs)
        {
            using var scope = NewScope(out var db);
            var inv = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
            await Svc<IARInvoiceService>(scope).VoidAsync(inv.ARInvoiceId);
        }

        // ── 真入口 ──────────────────────────────────────────────────────────

        private sealed record SaveOutcome(bool Ok, string? Message)
        {
            public void AssertOk(string what) => Assert.True(Ok, $"{what} 應成功，實際被擋：{Message}");
            public void AssertRejected(string what) => Assert.False(Ok, $"{what} 應被守門擋下，實際卻放行了");
        }

        private GoodsShipmentController NewShipmentController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new GoodsShipmentController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IARInvoiceService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IInventoryReservationService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }

        private SalesReturnController NewReturnController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new SalesReturnController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<ISamplingPlanService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IARInvoiceService>(),
                sp.GetRequiredService<IARCreditNoteService>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }

        /// <summary>Save 成功回 ContentResult；被守門擋下時回 _Form 的 PartialViewResult（錯誤在 ModelState）。</summary>
        private static SaveOutcome ReadSave(IActionResult result, ModelStateDictionary modelState)
        {
            if (result is ContentResult) return new SaveOutcome(true, null);
            var msg = string.Join("；", modelState.Values
                .SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return new SaveOutcome(false, msg);
        }

        private async Task<SaveOutcome> CreateShipmentAsync(decimal shipQty)
        {
            using var scope = NewScope(out var db);
            var ctrl = NewShipmentController(scope, db);
            var vm = new GoodsShipmentFormVm
            {
                ShipDate = ShipDate, CompanyCode = Customer, SalesOrderId = So, CreatedBy = UserId,
                Lines =
                {
                    new GoodsShipmentLineVm
                    {
                        LineNo = 1, SalesOrderId = So, SoLineNo = 1, ProductCode = Fg,
                        ShipQty = shipQty, UomCode = Uom, StockUomCode = Uom,
                        UnitPrice = Price, ExchangeRate = 1m, TaxRate = TaxRate,
                        LineAmount = shipQty * Price,
                        TaxAmount  = shipQty * Price * TaxRate,
                        TotalAmount = shipQty * Price * (1m + TaxRate),
                    },
                },
            };
            return ReadSave(await ctrl.Save(vm), ctrl.ModelState);
        }

        private async Task<string> LatestShipmentAsync()
        {
            using var scope = NewScope(out var db);
            return await db.GoodsShipments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == Plant)
                .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.GoodsShipmentId)
                .Select(d => d.GoodsShipmentId).FirstAsync();
        }

        private async Task ConfirmShipmentAsync(string gsId)
        {
            await WithSimplifiedModeAsync(async () =>
            {
                using var scope = NewScope(out var db);
                var ctrl = NewShipmentController(scope, db);
                var result = await ctrl.Confirm(gsId);
                Assert.True(result is OkObjectResult,
                    $"出貨確認應成功：{(result as BadRequestObjectResult)?.Value}");
            });
        }

        private async Task<SaveOutcome> CreateReturnAsync(string gsId, int gsLineNo, decimal returnQty)
        {
            using var scope = NewScope(out var db);
            var ctrl = NewReturnController(scope, db);
            var vm = new SalesReturnFormVm
            {
                ReturnDate = RtnDate, CompanyCode = Customer, GoodsShipmentId = gsId, CreatedBy = UserId,
                Lines =
                {
                    new SalesReturnLineVm
                    {
                        LineNo = 1, GoodsShipmentId = gsId, ShipmentLineNo = gsLineNo, ProductCode = Fg,
                        ReturnQty = returnQty, UomCode = Uom, StockUomCode = Uom,
                        // 畫面是從來源出貨行帶價過來的，故這裡放的是**售價**——
                        // 銷退單上根本沒有成本欄位，成本必須由系統自己找。
                        UnitPrice = Price, ExchangeRate = 1m, TaxRate = TaxRate,
                        LineAmount = returnQty * Price,
                        TaxAmount  = returnQty * Price * TaxRate,
                        TotalAmount = returnQty * Price * (1m + TaxRate),
                    },
                },
            };
            return ReadSave(await ctrl.Save(vm), ctrl.ModelState);
        }

        private async Task ConfirmReturnAsync(string srId)
        {
            await WithSimplifiedModeAsync(async () =>
            {
                using var scope = NewScope(out var db);
                var ctrl = NewReturnController(scope, db);
                var result = await ctrl.Confirm(srId);
                Assert.True(result is OkObjectResult,
                    $"銷退確認應成功：{(result as BadRequestObjectResult)?.Value}");
            });
        }

        /// <summary>簡化模式才會在確認當下過帳；不開的話只會把單據推到 Confirmed，什麼帳都沒有。</summary>
        private async Task WithSimplifiedModeAsync(Func<Task> body)
        {
            using (var scope = NewScope(out _))
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, "true");
            try { await body(); }
            finally
            {
                using var scope = NewScope(out _);
                await Svc<ISystemParamService>(scope)
                    .SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, null);
            }
        }

        // ── §24.2a 成品留給原需求：出貨消耗、作廢回鎖 ────────────────────────

        private const string MrpRun = "FLOW-O2CR-MRP";

        private async Task SeedDemandReservationAsync(decimal lockedQty)
        {
            using var scope = NewScope(out var db);
            db.InventoryReservations.RemoveRange(db.InventoryReservations.IgnoreQueryFilters().Where(r => r.SourceDocId == MrpRun));
            db.MrpRuns.RemoveRange(db.MrpRuns.IgnoreQueryFilters().Where(r => r.MrpRunId == MrpRun));
            await db.SaveChangesAsync();
            var run = new MrpRun { MrpRunId = MrpRun, PlantId = Plant, CreatedByRoleId = RoleId };
            run.Lines.Add(new MrpRunLine
            {
                MrpRunId = MrpRun, LineSeq = 1, SalesOrderId = So, SalesOrderLineSeq = 1,
                ProductCode = Fg, RequiredQty = lockedQty,
                RequiredDate = ShipDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            });
            db.MrpRuns.Add(run);
            await db.SaveChangesAsync();
            // 投產重算／完工時鎖給這張訂單的成品
            await Svc<IInventoryReservationService>(scope).ReserveAsync(Plant, Fg, lockedQty,
                ReservationSourceType.MrpRunLine, MrpRun, 1, run.Lines.First().RequiredDate);
        }

        private async Task<decimal> LockedForOrderAsync()
        {
            using var scope = NewScope(out var db);
            return await db.InventoryReservations.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.SourceType == ReservationSourceType.MrpRunLine && r.SourceDocId == MrpRun
                         && r.Status == ReservationStatus.Active)
                .SumAsync(r => r.ReservedQty);
        }

        [Fact]
        public async Task Shipment_ConsumesFinishedGoodsLockedForTheOrder_VoidLocksThemBack()
        {
            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(30m);
            await SeedDemandReservationAsync(30m);

            (await CreateShipmentAsync(20m)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            // 出了 20：鎖量同步少 20。沒消耗的話在手已出、鎖量仍是 30，廠級可用量被同一批貨扣兩次
            Assert.Equal(10m, await LockedForOrderAsync());
            using (var scope = NewScope(out var db))
                Assert.Equal(OpeningQty - 20m - 10m,
                    await Svc<IInventoryReservationService>(scope).GetPlantAvailableQtyAsync(Plant, Fg));

            await VoidShipmentInvoiceAsync(gs);   // 發票還活著不准作廢出貨
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewShipmentController(scope, db);
                var result = await ctrl.Void(gs, "出錯貨");
                Assert.True(result is OkObjectResult, $"作廢應成功：{(result as BadRequestObjectResult)?.Value}");
            }

            // 貨回到庫存、訂單又要等它：重新鎖回原訂單
            Assert.Equal(30m, await LockedForOrderAsync());
        }

        [Fact]
        public async Task Shipment_CannotTakeFinishedGoodsLockedForAnotherOrder()
        {
            // §24.2a：預留只在廠級、不落儲位。原本出貨只看儲位庫存，保留給別張訂單的成品照樣出得掉（2026-10-14 驗收發現）
            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(100m);
            using (var scope = NewScope(out var db))
            {
                db.InventoryReservations.RemoveRange(db.InventoryReservations.IgnoreQueryFilters().Where(r => r.SourceDocId == MrpRun));
                db.MrpRuns.RemoveRange(db.MrpRuns.IgnoreQueryFilters().Where(r => r.MrpRunId == MrpRun));
                await db.SaveChangesAsync();
                var run = new MrpRun { MrpRunId = MrpRun, PlantId = Plant, CreatedByRoleId = RoleId };
                run.Lines.Add(new MrpRunLine
                {
                    MrpRunId = MrpRun, LineSeq = 1, SalesOrderId = "FLOW-O2CR-OTHER", SalesOrderLineSeq = 1,
                    ProductCode = Fg, RequiredQty = 30m,
                    RequiredDate = ShipDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                });
                db.MrpRuns.Add(run);
                await db.SaveChangesAsync();
                await Svc<IInventoryReservationService>(scope).ReserveAsync(Plant, Fg, 30m,
                    ReservationSourceType.MrpRunLine, MrpRun, 1, run.Lines.First().RequiredDate);
            }

            try
            {
                // 在手 100、30 保留給別張訂單：出 80 被擋、庫存不動
                (await CreateShipmentAsync(80m)).AssertOk("建出貨單");
                var gs = await LatestShipmentAsync();
                await WithSimplifiedModeAsync(async () =>
                {
                    using var scope = NewScope(out var db);
                    var result = await NewShipmentController(scope, db).Confirm(gs);
                    var bad = Assert.IsType<BadRequestObjectResult>(result);
                    Assert.Contains(Fg, System.Text.Json.JsonSerializer.Serialize(bad.Value));
                });
                using (var scope = NewScope(out var db))
                    Assert.Equal(OpeningQty, await QtyOnHandAsync(db, Plant, Fg));
                Assert.Equal(30m, await LockedForOrderAsync());
            }
            finally
            {
                // 別張訂單的預留留著會擋掉同類別後面的出貨測試
                using var scope = NewScope(out var db);
                await db.InventoryReservations.IgnoreQueryFilters().Where(r => r.SourceDocId == MrpRun).ExecuteDeleteAsync();
            }
        }

        // ── 3-2：分批出貨 ────────────────────────────────────────────────────

        [Fact]
        public async Task PartialShipments_AccumulateOnOrderLine_AndOverShipmentIsRejected()
        {
            const decimal orderQty = 100m;
            const decimal first    = 60m;
            const decimal second   = 40m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(orderQty);

            (await CreateShipmentAsync(first)).AssertOk("第一批出貨");
            var gs1 = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs1);

            using (var scope = NewScope(out var db))
            {
                var soLine = await db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.SalesOrderId == So && l.LineNo == 1);
                Assert.Equal(first, soLine.ShippedQty);
                Assert.Equal(first, soLine.ShippedStockQty);
                Assert.Equal(OpeningQty - first, await QtyOnHandAsync(db, Plant, Fg));
            }

            (await CreateShipmentAsync(second)).AssertOk("第二批出貨");
            var gs2 = await LatestShipmentAsync();
            Assert.NotEqual(gs1, gs2);
            await ConfirmShipmentAsync(gs2);

            // 訂單已出滿，再出一個都不行——這是分批出貨唯一的守門。
            (await CreateShipmentAsync(1m)).AssertRejected("出滿之後再出一批");

            using (var scope = NewScope(out var db))
            {
                var soLine = await db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.SalesOrderId == So && l.LineNo == 1);
                Assert.Equal(orderQty, soLine.ShippedQty);
                Assert.Equal(orderQty, soLine.ShippedStockQty);
                Assert.Equal(OpeningQty - orderQty, await QtyOnHandAsync(db, Plant, Fg));

                // 被擋下的那一批不得留下任何痕跡（草稿、明細、或偷偷加上去的累計）。
                Assert.Equal(2, await db.GoodsShipments.IgnoreQueryFilters()
                    .CountAsync(d => d.PlantId == Plant));

                var gl = await GlNetByAccountAsync(db, Plant);
                await AssertEveryEntryBalancedAsync(db, Plant);
                Assert.Equal(orderQty * OpeningCost, Net(gl, StandardAccounts.CogsCode));
                Assert.Equal((OpeningQty - orderQty) * OpeningCost,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditSoOverShipmentAsync(Plant));
                Assert.Empty(await audit.AuditSoShippedQtyDriftAsync(Plant));
                Assert.Empty(await audit.AuditShipmentInvoiceGapAsync(Plant));
            }
        }

        // ── 3-6：銷貨退回 ────────────────────────────────────────────────────

        [Fact]
        public async Task SalesReturn_ComesBackAtShipmentCost_NotAtSellingPrice()
        {
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;

            const decimal shipCogs   = shipQty   * OpeningCost;   // 4,800
            const decimal returnCost = returnQty * OpeningCost;   // 1,200
            const decimal gross      = shipQty   * Price * (1m + TaxRate);  // 12,600
            const decimal returnGross = returnQty * Price * (1m + TaxRate); //  3,150

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);

            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            using (var scope = NewScope(out var db))
            {
                // ① 數量：回補到原儲位
                Assert.Equal(OpeningQty - shipQty + returnQty, await QtyOnHandAsync(db, Plant, Fg));

                // ② 成本：退回來的是**當初出去的那批貨**，不是一批用售價買進來的新貨。
                //    銷退單上沒有成本欄位，系統若直接拿售價當入庫單價，會一次做錯三件事：
                //    存貨被灌成售價、COGS 被沖過頭、而且移動平均從此被汙染，
                //    之後每一次出貨的成本都跟著錯——錯誤會一直擴散下去。
                var mv = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.DocNo == sr);
                Assert.Equal(OpeningCost, mv.UnitCost);

                var inv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.PlantId == Plant && i.ProductCode == Fg && i.LocationCode == Loc);
                Assert.Equal(OpeningCost, inv.UnitCost);

                // ③ 三方向沖回，而且**金額一致**
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                Assert.Equal(shipCogs - returnCost, Net(gl, StandardAccounts.CogsCode));
                Assert.Equal(OpeningQty * OpeningCost - shipCogs + returnCost,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));
                Assert.Equal(gross - returnGross, Net(gl, StandardAccounts.AccountsReceivableCode));

                var invoice = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                Assert.Equal(gross - returnGross, invoice.TotalAmount);

                var gsLine = await db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.GoodsShipmentId == gs && l.LineNo == 1);
                Assert.Equal(returnQty, gsLine.ReturnedQty);

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditSoOverShipmentAsync(Plant));
                Assert.Empty(await audit.AuditSalesReturnCostBasisAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            }
        }

        /// <summary>
        /// 計價來源修好之後新單不會再偏，但**既有資料**還在——已過帳的傳票不能改寫。
        /// 這裡直接把銷退異動的單位成本改成售價，重現修好之前留下的那種列，
        /// 確認 §3.13 稽核看得見（而借貸平衡與數量捲動都看不見）。
        /// </summary>
        [Fact]
        public async Task LegacySalesReturnPostedAtSellingPrice_IsOnlyVisibleToCostBasisAudit()
        {
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            using (var scope = NewScope(out var db))
            {
                var mv = await db.StockMovements.IgnoreQueryFilters().SingleAsync(m => m.DocNo == sr);
                mv.UnitCost  = Price;                 // 修好之前的行為：拿售價當入庫成本
                mv.TotalCost = Price * returnQty;
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var audit = Svc<IDataAuditService>(scope);

                // 既有的兩道防線一致地沉默：傳票各自平衡、數量完全正確。
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));

                var findings = await audit.AuditSalesReturnCostBasisAsync(Plant);
                var f = Assert.Single(findings);
                Assert.Equal("SALES_RETURN_COST_DRIFT", f.AuditType);
                Assert.Equal(sr, f.DocId);
                Assert.Equal((Price - OpeningCost) * returnQty, f.DifferenceAmount);
            }
        }

        /// <summary>
        /// 作廢之後，計價偏差稽核必須閉嘴。
        ///
        /// 作廢會補一筆反向異動，兩筆淨額歸零、傳票也已沖回——帳上沒有偏差了，
        /// 但原始那筆 +1 異動仍留在 `StockMovements` 裡。稽核不濾掉作廢單就會永遠報它，
        /// 而這條稽核是**擋月結**的（`RunAllAsync`），於是「發現計價錯誤 → 作廢重開」
        /// 這個唯一正確的處置方式，反而讓帳從此關不了。
        /// 2026-09-05 Wave 3 實測到（GR-2609-00001 作廢後仍被列 1473.30）。
        /// </summary>
        [Fact]
        public async Task VoidedSalesReturn_IsExcludedFromCostBasisAudit()
        {
            ExpectInvariantViolation("INV_GL_SUBLEDGER",
                "竄改異動單價模擬修正前的舊資料：傳票是成本價、作廢卻照售價沖回，明細帳因此比總帳少這一筆差價");
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            using (var scope = NewScope(out var db))
            {
                var mv = await db.StockMovements.IgnoreQueryFilters().SingleAsync(m => m.DocNo == sr);
                mv.UnitCost  = Price;                 // 修好之前的行為：拿售價當入庫成本
                mv.TotalCost = Price * returnQty;
                await db.SaveChangesAsync();
            }

            // 先確認稽核真的看得見（否則下面的 Empty 會是假綠）
            using (var scope = NewScope(out var db))
                Assert.Single(await Svc<IDataAuditService>(scope).AuditSalesReturnCostBasisAsync(Plant));

            await VoidShipmentInvoiceAsync(gs);   // 銷退已回沖這張發票，發票還活著不准作廢銷退
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewReturnController(scope, db);
                var result = await ctrl.Void(sr, "計價錯誤，作廢重開");
                Assert.True(result is OkObjectResult,
                    $"作廢應成功：{(result as BadRequestObjectResult)?.Value}");
            }

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(DocStatus.Void, (await db.SalesReturns.IgnoreQueryFilters()
                    .AsNoTracking().SingleAsync(d => d.SalesReturnId == sr)).Status);
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditSalesReturnCostBasisAsync(Plant));
            }
        }

        /// <summary>
        /// 同一出貨行有多筆單價不同的出庫列（完整模式分批揀貨、取消確認後再出貨都會發生）：
        /// 銷退入庫成本取該行出庫的**淨額加權平均**，稽核也用同一口徑。
        /// 原本兩邊都只取任一筆出庫列——退回成本看資料庫回傳順序，稽核把正確的銷退報成偏差（擋月結）。
        /// 這裡把一筆 40 @成本 拆成 20 @成本−5 ＋ 20 @成本+5（總價值不變）模擬分批揀貨。
        /// </summary>
        [Fact]
        public async Task ShipmentLineWithSeveralIssueRows_ReturnTakesNetAverageCost_AuditStaysQuiet()
        {
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            using (var scope = NewScope(out var db))
            {
                var mv = await db.StockMovements.IgnoreQueryFilters()
                    .SingleAsync(m => m.DocNo == gs && m.Direction == -1);
                var half = (StockMovement)db.Entry(mv).CurrentValues.Clone().ToObject();
                half.MovementId = Guid.NewGuid();
                half.Qty = half.StockQty = shipQty / 2;
                half.UnitCost = OpeningCost + 5m;
                half.TotalCost = half.UnitCost * half.StockQty;
                mv.Qty = mv.StockQty = shipQty / 2;
                mv.UnitCost = OpeningCost - 5m;
                mv.TotalCost = mv.UnitCost * mv.StockQty;
                db.StockMovements.Add(half);
                await db.SaveChangesAsync();
            }

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            using (var scope = NewScope(out var db))
            {
                var ret = await db.StockMovements.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.DocNo == sr);
                Assert.Equal(OpeningCost, ret.UnitCost);
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditSalesReturnCostBasisAsync(Plant));
            }
        }

        /// <summary>
        /// 發票已收款之後才退貨：`ReverseForReturnedQtyAsync` 刻意不動已結清的發票
        /// （改金額會讓憑證與稅務申報前後不一致），於是**應收與營收一動也沒有**——
        /// 貨退回來了、錢還掛在帳上，公司實際上欠客戶這筆錢。
        ///
        /// 這支測試釘住三件事：
        ///   ① 服務層回報缺口（`SettledInvoice` ＋ 含稅金額），不是無聲 return；
        ///   ② 既有的兩道防線一致地沉默——傳票張張平衡、數量捲動完全正確；
        ///   ③ 新稽核 `RETURN_SUBLEDGER_UNREVERSED` 看得見，且掛在 RunAllAsync 上
        ///      ＝這筆沒處理掉就關不了帳。
        ///
        /// 稽核刻意**看資料不看呼叫結果**（比對「發票明細現有量」與「出貨量 − 已退量」），
        /// 所以將來新增第五個退貨入口、忘了呼叫回沖服務時也一樣抓得到。
        /// </summary>
        [Fact]
        public async Task SalesReturn_AfterInvoiceSettled_LeavesArGap_OnlyVisibleToNewAudit()
        {
            ExpectInvariantViolation("RETURN_SUBLEDGER_UNREVERSED", "本測試故意在發票結清後銷退，製造只有新稽核看得到的應收缺口");
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            // 出貨開立的發票先收滿款——這正是 Wave 3 實機踩到的順序
            using (var scope = NewScope(out var db))
            {
                var inv = await db.ARInvoices.IgnoreQueryFilters()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                inv.ReceivedAmount = inv.TotalAmount;
                inv.Status = Modules.FIN.Models.ARInvoiceStatus.Received;
                await db.SaveChangesAsync();
            }

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            using (var scope = NewScope(out var db))
            {
                // 發票金額一毛未動——不回沖是刻意的
                var invoice = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                Assert.Equal(shipQty * Price * (1m + TaxRate), invoice.TotalAmount);

                var audit = Svc<IDataAuditService>(scope);

                // 既有防線一致地沉默
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
                Assert.Empty(await audit.AuditSalesReturnCostBasisAsync(Plant));

                // 只有新稽核看得見
                var findings = await audit.AuditUnreversedReturnSubledgerAsync(Plant);
                var f = Assert.Single(findings);
                Assert.Equal("RETURN_SUBLEDGER_UNREVERSED", f.AuditType);
                Assert.Equal(invoice.ARInvoiceId, f.DocId);
                Assert.Equal(returnQty * Price * (1m + TaxRate), f.DifferenceAmount);

                // Phase 2：折讓單草稿已經產生，但**草稿不會讓閘門放行**——
                // 草稿還沒沖任何東西，錢還掛在帳上。自動產生草稿若順手把稽核關掉，
                // 這個閘門就等於自己把自己拆了。
                var draft = Assert.Single(await db.ARCreditNotes.IgnoreQueryFilters().AsNoTracking()
                    .Where(n => n.SourceDocId == sr).ToListAsync());
                Assert.Equal(Modules.FIN.Models.CreditNoteStatus.Draft, draft.Status);
                Assert.Equal(returnQty * Price * (1m + TaxRate), draft.TotalAmount);
                Assert.Equal(0m, invoice.CreditedAmount);
            }
        }

        /// <summary>
        /// W3-F3 Phase 2：折讓單確認之後，這條路才真的走完。
        ///
        /// Phase 1 把缺口算出來並讓 <c>RETURN_SUBLEDGER_UNREVERSED</c> 擋住關帳，
        /// 但**沒有給出口**——稽核叫了，系統裡沒有任何單據可以把它處理掉，
        /// 於是那筆異常會永久擋著，跟 W4-F7／F13 是同一種死結。
        ///
        /// 這支釘住出口確實通：確認折讓 → 原發票的未結餘額被沖掉（但 TotalAmount
        /// 一毛未動，憑證與稅務申報不變）→ 產生與 Open 發票回沖**完全相同**的分錄
        /// → 稽核那一筆自然消失 → 傳票仍張張平衡。
        /// </summary>
        [Fact]
        public async Task ConfirmingTheCreditNote_ClosesTheGap_AndClearsTheAudit()
        {
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;
            var creditGross = returnQty * Price * (1m + TaxRate);

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            string invoiceId;
            using (var scope = NewScope(out var db))
            {
                var inv = await db.ARInvoices.IgnoreQueryFilters()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                inv.ReceivedAmount = inv.TotalAmount;
                inv.Status = Modules.FIN.Models.ARInvoiceStatus.Received;
                invoiceId = inv.ARInvoiceId;
                await db.SaveChangesAsync();
            }

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            string noteId;
            using (var scope = NewScope(out var db))
                noteId = await db.ARCreditNotes.IgnoreQueryFilters().AsNoTracking()
                    .Where(n => n.SourceDocId == sr).Select(n => n.ARCreditNoteId).SingleAsync();

            var glBefore = default(Dictionary<string, decimal>);
            using (var scope = NewScope(out var db))
                glBefore = await GlNetByAccountAsync(db, Plant);

            using (var scope = NewScope(out var db))
                await Svc<IARCreditNoteService>(scope).ConfirmAsync(noteId);

            using (var scope = NewScope(out var db))
            {
                var note = await db.ARCreditNotes.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(n => n.ARCreditNoteId == noteId);
                Assert.Equal(Modules.FIN.Models.CreditNoteStatus.Posted, note.Status);

                var inv = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.ARInvoiceId == invoiceId);

                // ① 憑證金額一毛未動——折讓在稅務上是另一張憑證，不是把原憑證改小
                Assert.Equal(shipQty * Price * (1m + TaxRate), inv.TotalAmount);
                // ② 沖抵記在專屬欄位上（不是併進 ReceivedAmount——折讓不是收到錢，
                //    併進去之後「客戶到底付了多少」就再也答不出來）
                Assert.Equal(creditGross, inv.CreditedAmount);
                Assert.Equal(shipQty * Price * (1m + TaxRate), inv.ReceivedAmount);

                // ③ 未結餘額變成**負的**，而且那是對的：客戶已全額付款又退了貨，
                //    公司這時候實際上欠客戶這筆錢。第一版把它守成「不得為負」，
                //    等於宣告「已付清的發票不能退貨」，被這支測試當場擋下。
                //    後續由退款或抵下一張發票處理。
                Assert.Equal(-creditGross, inv.TotalAmount - inv.ReceivedAmount - inv.CreditedAmount);

                // ④ 分錄與 Open 發票的既有回沖完全相同：借 收入＋借 銷項稅／貸 應收
                var glAfter = await GlNetByAccountAsync(db, Plant);
                var net = returnQty * Price;
                var tax = creditGross - net;
                // Net() 是「借 − 貸」，故借記收入／稅額是正值、貸記應收是負值。
                Assert.Equal(net, Net(glAfter, StandardAccounts.SalesRevenueCode)
                                - Net(glBefore, StandardAccounts.SalesRevenueCode));
                Assert.Equal(tax, Net(glAfter, StandardAccounts.OutputVatCode)
                                - Net(glBefore, StandardAccounts.OutputVatCode));
                Assert.Equal(-creditGross, Net(glAfter, StandardAccounts.AccountsReceivableCode)
                                         - Net(glBefore, StandardAccounts.AccountsReceivableCode));

                // ⑤ 閘門放行，而且是因為真的處理掉了
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditUnreversedReturnSubledgerAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                await AssertEveryEntryBalancedAsync(db, Plant);
            }
        }

        /// <summary>
        /// 折讓單作廢：沖抵金額要減回去、分錄要回沖，而且**稽核那一筆要重新出現**。
        ///
        /// 只驗「作廢了」是不夠的——真正危險的形狀是「單據作廢了、CreditedAmount 沒減回去」：
        /// 帳面看起來乾淨、閘門也放行，而那筆錢實際上從來沒有被處理過。
        /// </summary>
        [Fact]
        public async Task VoidingAPostedCreditNote_PutsTheGapBack()
        {
            ExpectInvariantViolation("RETURN_SUBLEDGER_UNREVERSED", "本測試驗證作廢已過帳的折讓會把應收缺口放回去（稽核要抓得到）");
            const decimal shipQty   = 40m;
            const decimal returnQty = 10m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            using (var scope = NewScope(out var db))
            {
                var inv = await db.ARInvoices.IgnoreQueryFilters()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                inv.ReceivedAmount = inv.TotalAmount;
                inv.Status = Modules.FIN.Models.ARInvoiceStatus.Received;
                await db.SaveChangesAsync();
            }

            (await CreateReturnAsync(gs, gsLineNo: 1, returnQty)).AssertOk("銷貨退回");
            string sr;
            using (var scope = NewScope(out var db))
                sr = await db.SalesReturns.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId).SingleAsync();
            await ConfirmReturnAsync(sr);

            string noteId;
            using (var scope = NewScope(out var db))
                noteId = await db.ARCreditNotes.IgnoreQueryFilters().AsNoTracking()
                    .Where(n => n.SourceDocId == sr).Select(n => n.ARCreditNoteId).SingleAsync();

            using (var scope = NewScope(out _))
                await Svc<IARCreditNoteService>(scope).ConfirmAsync(noteId);
            using (var scope = NewScope(out _))
                await Svc<IARCreditNoteService>(scope).VoidAsync(noteId, "測試作廢");

            using (var scope = NewScope(out var db))
            {
                var inv = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                Assert.Equal(0m, inv.CreditedAmount);   // 減回去了

                // 稽核那一筆重新出現——作廢之後那筆錢又變成沒人處理
                var audit = Svc<IDataAuditService>(scope);
                Assert.Single(await audit.AuditUnreversedReturnSubledgerAsync(Plant));

                // 分錄回沖後仍張張平衡；應收科目回到折讓前的水位
                await AssertEveryEntryBalancedAsync(db, Plant);
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }

        [Fact]
        public async Task SalesReturn_CannotExceedShippedQty()
        {
            const decimal shipQty = 40m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            (await CreateReturnAsync(gs, gsLineNo: 1, shipQty + 1m)).AssertRejected("退回量大於出貨量");

            using var scope = NewScope(out var db);
            Assert.Empty(await db.SalesReturns.IgnoreQueryFilters()
                .Where(d => d.PlantId == Plant).ToListAsync());
            var gsLine = await db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.GoodsShipmentId == gs && l.LineNo == 1);
            Assert.Equal(0m, gsLine.ReturnedQty);
        }

        // ── 3-4：部分收款 ────────────────────────────────────────────────────

        [Fact]
        public async Task PartialReceipt_AccumulatesAndOnlyClosesInvoiceWhenFullySettled()
        {
            const decimal shipQty = 40m;
            const decimal gross   = shipQty * Price * (1m + TaxRate);   // 12,600
            const decimal firstIn = 5_000m;

            await SeedOpeningStockAsync();
            await SeedSalesOrderAsync(shipQty);
            (await CreateShipmentAsync(shipQty)).AssertOk("出貨");
            var gs = await LatestShipmentAsync();
            await ConfirmShipmentAsync(gs);

            string arId;
            using (var scope = NewScope(out var db))
                arId = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs)
                    .Select(i => i.ARInvoiceId).SingleAsync();

            // ① 部分收款：發票不得結案，未收餘額要跟著降
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var r1 = await fin.CreateARReceiptAsync(new ARReceiptRequest(
                    Customer, RtnDate, PaymentMethod.Wire, null, 1m, firstIn, Bank, null, null,
                    new List<PaymentAllocationLine> { new(arId, firstIn) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(r1.ARReceiptId);

                var invoice = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.ARInvoiceId == arId);
                Assert.Equal(firstIn, invoice.ReceivedAmount);
                Assert.NotEqual(ARInvoiceStatus.Received, invoice.Status);
                Assert.Equal(gross - firstIn,
                    await Svc<IARInvoiceService>(scope).GetOutstandingBalanceAsync(Customer, Plant));
            }

            // ② 收完尾款
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var r2 = await fin.CreateARReceiptAsync(new ARReceiptRequest(
                    Customer, RtnDate, PaymentMethod.Wire, null, 1m, gross - firstIn, Bank, null, null,
                    new List<PaymentAllocationLine> { new(arId, gross - firstIn) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(r2.ARReceiptId);

                var invoice = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.ARInvoiceId == arId);
                Assert.Equal(gross, invoice.ReceivedAmount);
                Assert.Equal(ARInvoiceStatus.Received, invoice.Status);
                Assert.Equal(0m,
                    await Svc<IARInvoiceService>(scope).GetOutstandingBalanceAsync(Customer, Plant));

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsReceivableCode));
                Assert.Equal(0m, gl.Values.Sum());
            }
        }
    }
}
