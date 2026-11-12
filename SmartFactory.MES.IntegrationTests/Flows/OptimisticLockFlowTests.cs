using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// xmin 樂觀鎖：**蓋章必須在寫入之前**。
    ///
    /// 原本 PUR 的四支更新服務（進貨／退貨／採購／請購）內部自己就 SaveChanges，
    /// 而蓋章寫在**控制器裡、呼叫完服務之後**：
    ///
    ///   gr = await _purchaseService.UpdateGoodsReceiptAsync(vm, userId);   // ← 這裡已經寫入
    ///   _context.Entry(gr).Property(e => e.xmin).OriginalValue = vm.RowVersion;  // ← 才蓋章
    ///
    /// 資料早已落地、xmin 也換新了，事後才拿前端帶回的舊版本去比，於是兩件事同時成立：
    ///
    ///   ① **每一次正常修改都回報衝突**——使用者看到「資料已被其他人修改，請重載」，
    ///      但東西其實存進去了。使用者沒有任何辦法知道它成功了。
    ///   ② **真正的並發完全沒有保護**——第一次 SaveChanges 沒有版本檢查，
    ///      兩個人同時改就是後蓋前，而且兩邊都收到衝突訊息。
    ///
    /// 為什麼沒有測試會叫：既有測試都只呼叫服務一次、也不檢查回傳訊息，
    /// 而「寫入成功」與「回報衝突」在那個層級同時為真。
    /// 2026-09-06 人工驗收 4-6／4-11 實測（帶版本號 1 送出，資料照樣寫進去）。
    ///
    /// 這支測試必須跑在真 PostgreSQL 上——xmin 是 PG 的系統欄位，InMemory 沒有。
    /// </summary>
    [Collection("Integration")]
    public class OptimisticLockFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZLOCK";

        private const string Mat      = "FLOW-LOCK-M1";
        private const string Supplier = "FLOW-LOCK-SUP";
        private const string Po       = "FLOW-LOCK-PO1";

        private static readonly DateOnly RecvDate = new(2098, 8, 10);
        private const decimal OrderQty = 100m, FirstQty = 10m, Price = 5m;

        public OptimisticLockFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Mat, "樂觀鎖測試料")).GetAwaiter().GetResult();

        [Fact]
        public async Task UpdateGoodsReceipt_WithStaleRowVersion_ThrowsAndLeavesDataUntouched()
        {
            var grId = await SeedPoAndReceiptAsync();

            uint staleVersion;
            using (var scope = NewScope(out var db))
                staleVersion = (await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(g => g.GoodsReceiptId == grId)).xmin;

            // 第一個人存檔成功 → xmin 換新
            using (var scope = NewScope(out var db))
            {
                await Svc<IPurchaseService>(scope).UpdateGoodsReceiptAsync(
                    BuildVm(grId, staleVersion, qty: 20m), UserId);
            }

            using (var scope = NewScope(out var db))
            {
                var after = await db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.GoodsReceiptId == grId);
                Assert.Equal(20m, after.ReceiptQty);   // 前置：第一次改得進去
            }

            // 第二個人拿著**同一份舊快照**存檔 → 必須被擋，而且什麼都不能寫進去
            using (var scope = NewScope(out var db))
            {
                var svc = Svc<IPurchaseService>(scope);
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                    () => svc.UpdateGoodsReceiptAsync(BuildVm(grId, staleVersion, qty: 77m), UserId));
            }

            using (var scope = NewScope(out var db))
            {
                var line = await db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.GoodsReceiptId == grId);
                Assert.Equal(20m, line.ReceiptQty);    // 不是 77

                // 上游累計器也不能被動到——更新路徑是「先回沖再重新累加」，
                // 中途丟例外若沒整批回滾，PO 的已進貨量會停在回沖後的 0。
                var poLine = await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.PurchaseOrderId == Po && l.LineNo == 1);
                Assert.Equal(20m, poLine.ReceivedQty);
            }
        }

        /// <summary>版本號正確時不得誤報衝突——修好之前，**每一次**正常修改都會丟。</summary>
        [Fact]
        public async Task UpdateGoodsReceipt_WithCurrentRowVersion_Succeeds()
        {
            var grId = await SeedPoAndReceiptAsync();

            uint current;
            using (var scope = NewScope(out var db))
                current = (await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(g => g.GoodsReceiptId == grId)).xmin;

            using (var scope = NewScope(out var db))
            {
                var ex = await Record.ExceptionAsync(() =>
                    Svc<IPurchaseService>(scope).UpdateGoodsReceiptAsync(
                        BuildVm(grId, current, qty: 30m), UserId));
                Assert.Null(ex);
            }

            using (var scope = NewScope(out var db))
            {
                var line = await db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.GoodsReceiptId == grId);
                Assert.Equal(30m, line.ReceiptQty);

                var poLine = await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(l => l.PurchaseOrderId == Po && l.LineNo == 1);
                Assert.Equal(30m, poLine.ReceivedQty);   // 回沖 10 再累加 30，不是 40
            }
        }

        private GoodsReceiptFormVm BuildVm(string grId, uint rowVersion, decimal qty) => new()
        {
            GoodsReceiptId = grId,
            RowVersion     = rowVersion,
            ReceiptDate    = RecvDate,
            CreatedBy      = UserId,
            SupplierCode   = Supplier,
            Lines =
            [
                new GoodsReceiptLineVm
                {
                    LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                    ReceiptQty = qty, StockQty = qty, UomCode = Uom, StockUomCode = Uom,
                    UnitPrice = Price, LineAmount = qty * Price, TotalAmount = qty * Price,
                }
            ],
        };

        private async Task<string> SeedPoAndReceiptAsync()
        {
            using (var scope = NewScope(out var db))
            {
                if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Supplier))
                    db.Companies.Add(new SmartFactory.MES.Modules.BAS.Models.Company
                    {
                        CompanyCode = Supplier, CompanyName = "樂觀鎖測試供應商",
                        IsSupplier = true, IsActive = true,
                    });

                var po = new PurchaseOrder
                {
                    PurchaseOrderId = Po, OrderDate = RecvDate, SupplierCode = Supplier,
                    CreatedBy = UserId, PlantId = Plant,
                };
                po.Lines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                    OrderQty = OrderQty, UomCode = Uom, UnitPrice = Price,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var gr = await Svc<IPurchaseService>(scope).CreateGoodsReceiptAsync(new GoodsReceiptFormVm
                {
                    ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    [
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                            ReceiptQty = FirstQty, StockQty = FirstQty, UomCode = Uom, StockUomCode = Uom,
                            UnitPrice = Price, LineAmount = FirstQty * Price, TotalAmount = FirstQty * Price,
                        }
                    ],
                }, UserId, Plant);
                return gr.GoodsReceiptId;
            }
        }
    }
}
