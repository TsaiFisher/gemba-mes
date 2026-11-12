using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §7.1 的 4-6（本梯次列為最高優先）：
    /// **單據刪除明細行後重新新增再過帳，不得重複過帳／不得漏帳**。
    ///
    /// 全庫 30 餘處 controller／service 用同一種「先 RemoveRange 舊明細、再重建」的改單寫法。
    /// 已知的兩個前科（`ScrapDocController`／`StockAdjustmentController`）都是漏了
    /// `doc.Lines.Clear()`——`RemoveRange` 只把舊行標記為 Deleted，記憶體集合仍留著，
    /// 緊接著過帳就會把舊行一併算進去。那兩處都補上了，全庫掃過也沒有第三處。
    ///
    /// 但「重建明細」真正的難處不在集合本身，而在**改單前必須先把上游累計器回沖、
    /// 改完再重新累加**。這一對動作分寫在兩個方法裡，少呼叫一次不會有任何錯誤訊息：
    /// 單存檔成功、明細正確、庫存沒動，只有採購單上的「已進貨量」悄悄改變。
    ///
    /// 銷售端（`GoodsShipmentController`／`SalesReturnController`）兩邊都寫對了
    /// （`ReverseShippedQtyAsync` → 重建 → `UpdateSalesOrderShippedQtyAsync`）；
    /// 採購端的 <see cref="IPurchaseService.UpdateGoodsReceiptAsync"/> 只做了回沖那一半。
    /// </summary>
    [Collection("Integration")]
    public class DocumentRebuildFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZW4R";

        private const string Mat = "FLOW-W4R-MAT";
        private const string Po  = "FLOW-W4R-PO";

        private static readonly DateOnly OrderDate = new(2098, 9, 1);
        private static readonly DateOnly RecvDate  = new(2098, 9, 10);

        private const decimal OrderQty = 100m, Price = 50m, TaxRate = 0.05m;

        public DocumentRebuildFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "改單重建測試料"));
            using var scope = NewScope(out var db);
            await db.GoodsReceipts.IgnoreQueryFilters().Where(g => g.PlantId == Plant).ExecuteDeleteAsync();
            await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseOrderId == Po).ExecuteDeleteAsync();
            await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.PlantId == Plant).ExecuteDeleteAsync();
        }

        /// <summary>
        /// 改一張草稿進貨單——就算一個字都沒改到明細——採購單的已進貨量必須維持不變。
        ///
        /// 現行實作在改單時只呼叫 `RestorePoReceivedQtyAsync` 把舊量減掉，
        /// 重建明細之後沒有再累加回去，於是每改一次單，採購單就以為那批貨從沒收過。
        /// </summary>
        [Fact]
        public async Task EditingDraftReceipt_MustNotSilentlyReleaseTheOrderedQuantity()
        {
            const decimal firstQty = 60m, editedQty = 40m;

            await SeedPurchaseOrderAsync();
            var grId = await CreateReceiptAsync(firstQty);

            Assert.Equal(firstQty, await PoReceivedQtyAsync());

            // ① 只改備註，明細一字未動
            await UpdateReceiptAsync(grId, firstQty, remarks: "改個備註");
            Assert.Equal(firstQty, await PoReceivedQtyAsync());

            // ② 真的把數量改小 → 累計必須跟著變成新的數量，而不是「減掉再減掉」
            await UpdateReceiptAsync(grId, editedQty, remarks: "數量改小");
            Assert.Equal(editedQty, await PoReceivedQtyAsync());

            // 明細本身自始至終只有一行：重建不得留下舊行
            using var scope = NewScope(out var db);
            var line = await db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(l => l.GoodsReceiptId == grId);
            Assert.Equal(editedQty, line.ReceiptQty);

            var audit = Svc<IDataAuditService>(scope);
            Assert.Empty(await audit.AuditPoOverReceiptAsync(Plant));
        }

        /// <summary>
        /// 超收守門（§5.9）是拿「採購單上的累計已進貨量」在比的，所以它的可信度完全
        /// 建立在那個累計器沒有被改單洗掉。訂購 100、先收 60、改一次單、再想收 60——
        /// 累計 120 超過訂購量，必須被擋；而且擋下之後不得留下任何進貨單或明細。
        ///
        /// 這一條擋不住的話，`PO_OVER_RECEIPT` 稽核到月結才會叫，中間所有依賴
        /// 「已進貨量」的畫面（採購進度、在途量、MRP 供給）給的都是錯的數字。
        /// </summary>
        [Fact]
        public async Task OverReceiptGate_SurvivesAnEdit_AndRejectionLeavesNoTrace()
        {
            const decimal firstQty = 60m, secondQty = 60m;

            await SeedPurchaseOrderAsync();
            var grId = await CreateReceiptAsync(firstQty);
            await UpdateReceiptAsync(grId, firstQty, remarks: "改個備註");

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => CreateReceiptAsync(secondQty));
            PzAssert.Message("Pur_ReceiptExceedsOrderFmt", rejected.Message);

            using var scope = NewScope(out var db);

            // 被擋下的那一張連草稿都不該留
            var receipts = await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                .Where(g => g.PlantId == Plant).ToListAsync();
            Assert.Equal(grId, Assert.Single(receipts).GoodsReceiptId);

            Assert.Equal(firstQty, await PoReceivedQtyAsync());

            var audit = Svc<IDataAuditService>(scope);
            Assert.Empty(await audit.AuditPoOverReceiptAsync(Plant));
            Assert.Empty(await audit.AuditOrphanPostingAsync(Plant));
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private async Task SeedPurchaseOrderAsync()
        {
            using var scope = NewScope(out var db);
            var po = new PurchaseOrder
            {
                PurchaseOrderId = Po, OrderDate = OrderDate, CreatedBy = UserId,
                SupplierCode = Supplier, ApprovalStatus = DocumentApprovalStatus.Approved,
                PlantId = Plant, CreatedByRoleId = RoleId,
            };
            po.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                OrderQty = OrderQty, UomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
            });
            db.PurchaseOrders.Add(po);
            await db.SaveChangesAsync();
        }

        private async Task<string> CreateReceiptAsync(decimal qty)
        {
            using var scope = NewScope(out _);
            var gr = await Svc<IPurchaseService>(scope).CreateGoodsReceiptAsync(
                BuildVm(null, qty, null), UserId, Plant);
            return gr.GoodsReceiptId;
        }

        private async Task UpdateReceiptAsync(string grId, decimal qty, string? remarks)
        {
            using var scope = NewScope(out var db);
            // xmin 樂觀鎖現在在寫入**之前**比對（見 Flows/OptimisticLockFlowTests），
            // 所以更新一律要帶當下的版本號——畫面上那是表單裡的 hidden input，
            // 測試手工建 VM 就得自己讀。不帶（等於 0）現在會被正確地擋下來。
            var vm = BuildVm(grId, qty, remarks);
            vm.RowVersion = (await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(g => g.GoodsReceiptId == grId)).xmin;
            await Svc<IPurchaseService>(scope).UpdateGoodsReceiptAsync(vm, UserId);
        }

        private static GoodsReceiptFormVm BuildVm(string? grId, decimal qty, string? remarks)
            => new()
            {
                GoodsReceiptId = grId,
                ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                Remarks = remarks,
                Lines =
                [
                    new GoodsReceiptLineVm
                    {
                        LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                        ReceiptQty = qty, StockQty = qty,
                        UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                        TaxRate = TaxRate,
                        LineAmount = qty * Price,
                        TotalAmount = qty * Price * (1 + TaxRate),
                    }
                ],
            };

        private async Task<decimal> PoReceivedQtyAsync()
        {
            using var scope = NewScope(out var db);
            return await db.PurchaseOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PurchaseOrderId == Po && l.LineNo == 1)
                .Select(l => l.ReceivedQty).SingleAsync();
        }
    }
}
