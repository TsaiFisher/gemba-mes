using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 銷貨退回貫穿：期初入庫 → 出貨過帳 → 應收開立 → **退回過帳＋應收回沖**。
    ///
    /// 為什麼要單獨一支（manual-test-plan.md 3-6）：退回要同時發生三件事——
    /// 庫存回補、AR 沖回、COGS 沖回——而這三腿目前**各自都有測、卻沒有一支把它們綁在一起**：
    ///   - 庫存與 COGS 腿：`INV/StockMovementJournalTests.SimplifiedSalesReturn_CreditsCogs`
    ///     （過帳服務層，分錄服務是 mock）
    ///   - AR 腿：`FIN/ArInvoiceJournalAndRateTests`、`FIN/ARReversalByReturnedQtyTests`
    ///   - 串起三腿的是 `SalesReturnController.Confirm`，而該處的 controller 測試
    ///     （`Confirm_SimplifiedMode_ReversesArForSourceShipment`）把過帳服務也 mock 掉了，
    ///     只驗「有沒有呼叫 AR 回沖」。
    ///
    /// 於是「只沖一半」這個 §7.1 4-3 點名的壞法，在現有測試網裡是穿得過去的：
    /// 任何一腿悄悄不做，上面每一支測試都還是綠的。本檔用真服務、真 PostgreSQL
    /// 跑完整條鏈，再回頭對「庫存量 × 總帳逐科目淨額 × 發票餘額」同時下斷言。
    ///
    /// 退回入庫的成本刻意**不給單價**，與 `SalesReturnController.BuildSimplifiedLinesAsync`
    /// 一致——退回是按當下移動平均成本收回，不是按售價，否則退一次貨存貨就被灌成售價。
    /// </summary>
    [Collection("Integration")]
    public class SalesReturnFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZSRT";

        private const string Fg = "FLOW-SRT-FG";

        private static readonly DateOnly StockDate  = new(2097, 6, 1);
        private static readonly DateOnly ShipDate   = new(2097, 6, 10);
        private static readonly DateOnly ReturnDate = new(2097, 6, 20);

        private const decimal OpeningQty  = 100m;
        private const decimal OpeningCost = 120m;
        private const decimal ShipQty     = 40m;
        private const decimal Price       = 300m;
        private const decimal TaxRate     = 0.05m;

        public SalesReturnFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Fg, "流程測試退回品")).GetAwaiter().GetResult();

        /// <summary>部分退回：三腿都只沖回退回的那一部分，剩下的必須原封不動。</summary>
        [Fact]
        public async Task PartialReturn_ReversesInventoryArAndCogs_ByReturnedPortionOnly()
        {
            const decimal returnQty = 15m;
            const decimal keptQty   = ShipQty - returnQty;          // 25

            var (gs, arId) = await ShipAndInvoiceAsync("SRT-GS-1", "SRT-AR-1");
            await ReturnAsync("SRT-SR-1", gs, returnQty);

            using var scope = NewScope(out var db);

            // ① 庫存回補：期初 − 出貨 + 退回
            Assert.Equal(OpeningQty - ShipQty + returnQty, await QtyOnHandAsync(db, Plant, Fg));

            // ② 總帳：COGS 與存貨都只留下「實際賣掉的那一部分」
            await AssertEveryEntryBalancedAsync(db, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);
            Assert.Equal(keptQty * OpeningCost, Net(gl, StandardAccounts.CogsCode));
            Assert.Equal(OpeningQty * OpeningCost - keptQty * OpeningCost,
                         Net(gl, StandardAccounts.RawMaterialInventoryCode));

            // ③ 應收與營收：退回後只剩未退部分（貸方為負）
            var keptRevenue = keptQty * Price;
            var keptTax     = keptRevenue * TaxRate;
            Assert.Equal(-keptRevenue, Net(gl, StandardAccounts.SalesRevenueCode));
            Assert.Equal(-keptTax,     Net(gl, StandardAccounts.OutputVatCode));
            Assert.Equal(keptRevenue + keptTax, Net(gl, StandardAccounts.AccountsReceivableCode));

            // ④ 子帳與總帳一致：發票餘額＝應收科目淨額
            var invoice = await db.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == arId);
            Assert.Equal(keptRevenue + keptTax, invoice.TotalAmount);

            // ⑤ 整廠淨額仍為 0（沒有任何一腿被漏掉或多做）
            Assert.Equal(0m, gl.Values.Sum());
        }

        /// <summary>
        /// 全額退回：庫存回到期初、COGS／營收／銷項稅／應收**四個科目同時歸零**。
        /// 「只沖一半」在這裡會直接顯形——任何一腿沒沖，對應科目就留有殘值。
        /// </summary>
        [Fact]
        public async Task FullReturn_UnwindsInventoryArAndCogsToZero()
        {
            var (gs, arId) = await ShipAndInvoiceAsync("SRT-GS-2", "SRT-AR-2");
            await ReturnAsync("SRT-SR-2", gs, ShipQty);

            using var scope = NewScope(out var db);

            Assert.Equal(OpeningQty, await QtyOnHandAsync(db, Plant, Fg));

            await AssertEveryEntryBalancedAsync(db, Plant);
            var gl = await GlNetByAccountAsync(db, Plant);

            Assert.Equal(0m, Net(gl, StandardAccounts.CogsCode));
            Assert.Equal(0m, Net(gl, StandardAccounts.SalesRevenueCode));
            Assert.Equal(0m, Net(gl, StandardAccounts.OutputVatCode));
            Assert.Equal(0m, Net(gl, StandardAccounts.AccountsReceivableCode));

            // 存貨回到期初金額（退回是按 AVCO 收回，不是按售價）
            Assert.Equal(OpeningQty * OpeningCost, Net(gl, StandardAccounts.RawMaterialInventoryCode));

            var invoice = await db.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == arId);
            Assert.Equal(0m, invoice.TotalAmount);
            Assert.Equal(0m, gl.Values.Sum());
        }

        // ── 流程步驟 ────────────────────────────────────────────────────────

        /// <summary>期初入庫 → 出貨過帳 → 開立應收。回傳出貨單號與發票號。</summary>
        private async Task<(string Gs, string ArId)> ShipAndInvoiceAsync(string gs, string _)
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = $"{gs}-OPEN", LineNo = 1,
                    ProductCode = Fg, LocationCode = Loc, LotNo = "",
                    Qty = OpeningQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = OpeningCost, CreatedBy = UserId,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var revenue = ShipQty * Price;
                var tax     = revenue * TaxRate;

                var ship = new GoodsShipment
                {
                    GoodsShipmentId = gs, ShipDate = ShipDate, TypeCode = "GS",
                    CompanyCode = Customer, CreatedBy = UserId, CreatedByRoleId = RoleId,
                    PlantId = Plant, Status = DocStatus.Draft,
                    ApprovalStatus = DocumentApprovalStatus.Approved,
                };
                ship.Lines.Add(new GoodsShipmentLine
                {
                    GoodsShipmentId = gs, LineNo = 1, ProductCode = Fg,
                    ShipQty = ShipQty, StockQty = ShipQty, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = Price, TaxRate = TaxRate,
                    LineAmount = revenue, TaxAmount = tax, TotalAmount = revenue + tax,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.GoodsShipments.Add(ship);
                await db.SaveChangesAsync();

                await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
                {
                    PlantId = Plant, DocNo = gs, MovementDate = ShipDate,
                    TypeCode = "GS", TypeCategory = "Sales", CreatedBy = UserId,
                    Lines = new List<SimplifiedPostLine>
                    {
                        new()
                        {
                            LineNo = 1, ProductCode = Fg, WhCode = Wh, LocationCode = Loc, LotNo = "",
                            Qty = ShipQty, UomCode = Uom, StockUomCode = Uom, SetQtyPutAway = _ => { },
                        },
                    },
                    SetDocPosted = () => ship.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var _unused))
            {
                var invoice = await Svc<IARInvoiceService>(scope).GenerateFromShipmentAsync(gs);
                return (gs, invoice.ARInvoiceId);
            }

        }

        /// <summary>
        /// 銷貨退回：建單 → 退回入庫過帳 → 回沖來源出貨的應收。
        /// 這正是 <c>SalesReturnController.Confirm</c> 簡化模式那條路徑的三個步驟，
        /// 差別只在這裡用真服務、不經 HTTP。
        /// </summary>
        private async Task ReturnAsync(string srId, string gs, decimal returnQty)
        {
            using var scope = NewScope(out var db);

            var doc = new SalesReturn
            {
                SalesReturnId = srId, ReturnDate = ReturnDate, CompanyCode = Customer,
                CreatedBy = UserId, CreatedByRoleId = RoleId, PlantId = Plant,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
            };
            doc.Lines.Add(new SalesReturnLine
            {
                SalesReturnId = srId, LineNo = 1, ProductCode = Fg,
                GoodsShipmentId = gs, ShipmentLineNo = 1,
                ReturnQty = returnQty, StockQty = returnQty,
                UomCode = Uom, StockUomCode = Uom,
                WhCode = Wh, LocationCode = Loc,
            });
            db.SalesReturns.Add(doc);
            await db.SaveChangesAsync();

            // 退回入庫：**不給 UnitPrice**，按當下 AVCO 收回（與 controller 一致）
            await Svc<IInventoryPostingService>(scope).PostDocSimplifiedAsync(new PostDocSimplifiedRequest
            {
                PlantId = Plant, DocNo = srId, MovementDate = ReturnDate,
                TypeCode = "GR", TypeCategory = "Sales", CreatedBy = UserId,
                Lines = new List<SimplifiedPostLine>
                {
                    new()
                    {
                        LineNo = 1, ProductCode = Fg, WhCode = Wh, LocationCode = Loc, LotNo = "",
                        Qty = returnQty, BusinessQty = returnQty,
                        UomCode = Uom, StockUomCode = Uom, SetQtyPutAway = _ => { },
                    },
                },
                SetDocPosted = () => doc.Status = DocStatus.Posted,
            });
            await db.SaveChangesAsync();

            await Svc<IARInvoiceService>(scope).ReverseForReturnedQtyAsync(gs, 1, returnQty);
        }
    }
}
