using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 採購單、銷貨訂單、內部訂單的刪除守門。
    ///
    /// 這三張單自己**不過帳**，所以不套 <see cref="InvDocResidueTestBase"/>——
    /// 它們的殘留形態是第三種：**下游單據懸空**。
    /// 進貨單／出貨單／MRP 需求列都以字串欄位指回上游訂單（同樣沒有外鍵），
    /// 上游被刪掉的話，下游那些 <c>PurchaseOrderId</c> / <c>SalesOrderId</c>
    /// 就指向一張不存在的單：進貨查不到當初訂了多少、MRP 展開出來的工單失去需求來源。
    ///
    /// 三張單的守門訊號各不相同，值得並列：
    ///   ・採購單：<c>ReceivedQty > 0</c>（下游進貨回寫的欄位值）
    ///   ・銷貨訂單：<c>ShippedQty > 0</c> **加上** MRP 引用
    ///   ・內部訂單：<c>Status != Draft</c>、<c>QtyPutAway > 0</c>、MRP 引用
    /// 採購側沒有「被 MRP 引用不可刪」檢查，因為 MRP 是往下游產生請購／採購，
    /// 不會反向引用既有採購單。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class OrderDeleteResidueTests : PostingResidueTestBase
    {
        private const string Product  = Prefix + "-ORD01";
        private const string Category = Prefix + "-ORDCAT";
        private const string Uom      = Prefix + "-ORDEA";
        private const string Supplier = Prefix + "-ORDSUP";
        private const string Customer = Prefix + "-ORDCUS";

        private static readonly DateOnly OrderDate = new(2097, 6, 15);
        private const decimal OrderQty  = 20m;
        private const decimal UnitPrice = 5m;

        public OrderDeleteResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        // ════════════════════════════════════════════════════════════════════
        // 採購單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 已有進貨記錄的採購單不可刪；擋下後上游請購單的已下單量不得被動到。
        ///
        /// 守門的順序在這裡很要緊：<c>DeletePurchaseOrderAsync</c> 是先檢查 ReceivedQty
        /// 才呼叫 <c>RestorePrOrderedQtyAsync</c>。若哪天有人把兩行對調，
        /// 刪除仍會正確被擋、畫面訊息一模一樣，但請購單的已下單量已經先被減掉了——
        /// 那張請購會憑空多出可下單餘量，而採購單其實還在。
        /// </summary>
        [Fact]
        public async Task ReceivedPurchaseOrder_Delete_Blocked_AndRequestOrderedQtyUntouched()
        {
            var (poNo, prNo) = await SeedPurchaseAsync(
                nameof(ReceivedPurchaseOrder_Delete_Blocked_AndRequestOrderedQtyUntouched), receivedQty: OrderQty);
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(OrderQty, await ReadPrOrderedQtyAsync(prNo));

            AssertBlocked(await PostActionAsync(client, token, $"/PurchaseOrder/Delete/{poNo}"),
                "刪除已有進貨的採購單");

            Assert.Equal(OrderQty, await ReadPrOrderedQtyAsync(prNo));
            Assert.True(await PoExistsAsync(poNo), "刪除被擋下了，但採購單卻不見了。");
        }

        /// <summary>未收貨的採購單可刪，且必須把請購單的已下單量減回去。</summary>
        [Fact]
        public async Task UnreceivedPurchaseOrder_Delete_Succeeds_AndRestoresRequestOrderedQty()
        {
            var (poNo, prNo) = await SeedPurchaseAsync(
                nameof(UnreceivedPurchaseOrder_Delete_Succeeds_AndRestoresRequestOrderedQty), receivedQty: 0m);
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(OrderQty, await ReadPrOrderedQtyAsync(prNo));

            AssertAllowed(await PostActionAsync(client, token, $"/PurchaseOrder/Delete/{poNo}"),
                "刪除未收貨的採購單");

            Assert.False(await PoExistsAsync(poNo), "刪除回報成功，但採購單仍在。");
            Assert.Equal(0m, await ReadPrOrderedQtyAsync(prNo));
        }

        // ════════════════════════════════════════════════════════════════════
        // 銷貨訂單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>已出貨的銷貨訂單不可刪。</summary>
        [Fact]
        public async Task ShippedSalesOrder_Delete_Blocked()
        {
            var soNo = await SeedSalesOrderAsync(nameof(ShippedSalesOrder_Delete_Blocked),
                shippedQty: OrderQty, withMrpReference: false);
            var (client, token) = await NewAuthedClientAsync();

            AssertBlocked(await PostActionAsync(client, token, $"/SalesOrder/Delete/{soNo}"),
                "刪除已出貨的銷貨訂單");

            Assert.True(await SoExistsAsync(soNo));
        }

        /// <summary>
        /// 被 MRP 執行單引用的銷貨訂單不可刪——即使一件都還沒出貨。
        ///
        /// 這條守的是與出貨完全無關的另一條懸空路徑：MRP 需求列以 SalesOrderId
        /// 指回訂單，而展開出來的生產批與工單又掛在需求列下。訂單被刪掉的話，
        /// 那些工單還在跑，卻查不到自己是為誰做的——料已經領了、工也開了，
        /// 出貨時才發現沒有訂單可對。
        /// </summary>
        [Fact]
        public async Task MrpReferencedSalesOrder_Delete_Blocked_EvenWhenNothingShipped()
        {
            var soNo = await SeedSalesOrderAsync(nameof(MrpReferencedSalesOrder_Delete_Blocked_EvenWhenNothingShipped),
                shippedQty: 0m, withMrpReference: true);
            var (client, token) = await NewAuthedClientAsync();

            var result = await PostActionAsync(client, token, $"/SalesOrder/Delete/{soNo}");
            AssertBlocked(result, "刪除被 MRP 引用的銷貨訂單");
            Assert.Contains(soNo + "-MRP", result.Message);   // 訊息須指出是哪張 MRP，否則使用者無從處理

            Assert.True(await SoExistsAsync(soNo));
            Assert.True(await MrpLineExistsAsync(soNo), "訂單刪除被擋下，MRP 需求列自然也應原封不動。");
        }

        /// <summary>未出貨、未被 MRP 引用的訂單可刪，且不得留下懸空的 MRP 需求列。</summary>
        [Fact]
        public async Task CleanSalesOrder_Delete_Succeeds_WithNoDanglingReferences()
        {
            var soNo = await SeedSalesOrderAsync(nameof(CleanSalesOrder_Delete_Succeeds_WithNoDanglingReferences),
                shippedQty: 0m, withMrpReference: false);
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/SalesOrder/Delete/{soNo}"), "刪除乾淨的銷貨訂單");

            Assert.False(await SoExistsAsync(soNo), "刪除回報成功，但訂單仍在。");
            Assert.False(await MrpLineExistsAsync(soNo));
        }

        // ════════════════════════════════════════════════════════════════════
        // 內部訂單
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 已有倉庫作業量的內部訂單不可刪。
        ///
        /// 內部訂單全模式下 Confirm 都只轉 Confirmed（不過帳），實際發／退料一律走倉庫作業，
        /// 所以它的「已動過」訊號是明細的 <c>QtyPutAway</c> 而不是單據狀態——
        /// 這也是為什麼它同時有 <c>Status != Draft</c> 與 <c>QtyPutAway > 0</c> 兩道檢查：
        /// 前者擋已確認的，後者擋「被退回草稿但倉庫其實已經動過」的。
        /// </summary>
        [Fact]
        public async Task InternalOrderWithWarehouseOps_Delete_Blocked()
        {
            var ioNo = await SeedInternalOrderAsync(nameof(InternalOrderWithWarehouseOps_Delete_Blocked),
                qtyPutAway: OrderQty, withMrpReference: false);
            var (client, token) = await NewAuthedClientAsync();

            AssertBlocked(await PostActionAsync(client, token, $"/InternalOrder/Delete/{ioNo}"),
                "刪除已有倉庫作業的內部訂單");

            Assert.True(await InternalOrderExistsAsync(ioNo));
        }

        /// <summary>
        /// 被 MRP 引用的內部訂單不可刪。
        ///
        /// 這裡有個容易踩的細節：MRP 需求列**沿用 <c>SalesOrderId</c> 欄位存內部訂單號**
        /// （靠 NO- 前綴區分），兩種訂單共用同一個欄位。也就是說銷貨訂單那側的守門查詢
        /// 與這裡是同一條，任何一邊改了查法都會同時影響另一邊，
        /// 而型別系統完全看不出這層耦合——只能靠測試釘住。
        /// </summary>
        [Fact]
        public async Task MrpReferencedInternalOrder_Delete_Blocked()
        {
            var ioNo = await SeedInternalOrderAsync(nameof(MrpReferencedInternalOrder_Delete_Blocked),
                qtyPutAway: 0m, withMrpReference: true);
            var (client, token) = await NewAuthedClientAsync();

            AssertBlocked(await PostActionAsync(client, token, $"/InternalOrder/Delete/{ioNo}"),
                "刪除被 MRP 引用的內部訂單");

            Assert.True(await InternalOrderExistsAsync(ioNo));
            Assert.True(await MrpLineExistsAsync(ioNo));
        }

        /// <summary>乾淨的內部訂單可刪，不留懸空的 MRP 需求列。</summary>
        [Fact]
        public async Task CleanInternalOrder_Delete_Succeeds()
        {
            var ioNo = await SeedInternalOrderAsync(nameof(CleanInternalOrder_Delete_Succeeds),
                qtyPutAway: 0m, withMrpReference: false);
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/InternalOrder/Delete/{ioNo}"),
                "刪除乾淨的內部訂單");

            Assert.False(await InternalOrderExistsAsync(ioNo));
            Assert.False(await MrpLineExistsAsync(ioNo));
        }

        // ════════════════════════════════════════════════════════════════════
        // 種子
        // ════════════════════════════════════════════════════════════════════

        private async Task<(string PoNo, string PrNo)> SeedPurchaseAsync(string caseName, decimal receivedQty)
        {
            var poNo = $"{Prefix}PO-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";
            var prNo = poNo + "-PR";
            using var _ = NewScope(out var db);

            await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseOrderId == poNo).ExecuteDeleteAsync();
            await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(o => o.PurchaseOrderId == poNo).ExecuteDeleteAsync();
            await db.PurchaseRequestLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseRequestId == prNo).ExecuteDeleteAsync();
            await db.PurchaseRequests.IgnoreQueryFilters()
                .Where(r => r.PurchaseRequestId == prNo).ExecuteDeleteAsync();

            await EnsureMastersAsync(db);

            db.PurchaseRequests.Add(new PurchaseRequest
            {
                PurchaseRequestId = prNo,
                RequestDate       = OrderDate,
                RequestedBy       = "admin",
                PlantId           = Plant,
                CreatedByRoleId   = RoleId,
                ApprovalStatus    = DocumentApprovalStatus.Approved,
                Lines =
                {
                    new PurchaseRequestLine
                    {
                        PurchaseRequestId = prNo, LineNo = 1, ProductCode = Product,
                        RequestQty = OrderQty, StockQty = OrderQty,
                        UomCode = Uom, StockUomCode = Uom,
                        // 採購單建立時已累加；刪採購單必須減回去。
                        OrderedQty = OrderQty,
                    },
                },
            });

            db.PurchaseOrders.Add(new PurchaseOrder
            {
                PurchaseOrderId = poNo,
                OrderDate       = OrderDate,
                SupplierCode    = Supplier,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                Lines =
                {
                    new PurchaseOrderLine
                    {
                        PurchaseOrderId = poNo, LineNo = 1, ProductCode = Product,
                        PurchaseRequestId = prNo, PrLineNo = 1,
                        OrderQty = OrderQty, StockQty = OrderQty,
                        UomCode = Uom, StockUomCode = Uom,
                        UnitPrice = UnitPrice, ReceivedQty = receivedQty,
                    },
                },
            });

            await db.SaveChangesAsync();
            return (poNo, prNo);
        }

        private async Task<string> SeedSalesOrderAsync(string caseName, decimal shippedQty, bool withMrpReference)
        {
            var soNo  = $"{Prefix}SO-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";
            var mrpNo = soNo + "-MRP";
            using var _ = NewScope(out var db);

            await db.MrpRunLines.IgnoreQueryFilters()
                .Where(l => l.MrpRunId == mrpNo).ExecuteDeleteAsync();
            await db.MrpRuns.IgnoreQueryFilters()
                .Where(r => r.MrpRunId == mrpNo).ExecuteDeleteAsync();
            await db.SalesOrderLines.IgnoreQueryFilters()
                .Where(l => l.SalesOrderId == soNo).ExecuteDeleteAsync();
            await db.SalesOrders.IgnoreQueryFilters()
                .Where(o => o.SalesOrderId == soNo).ExecuteDeleteAsync();

            await EnsureMastersAsync(db);

            db.SalesOrders.Add(new SalesOrder
            {
                SalesOrderId    = soNo,
                OrderDate       = OrderDate,
                CompanyCode     = Customer,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                Lines =
                {
                    new SalesOrderLine
                    {
                        SalesOrderId = soNo, LineNo = 1, ProductCode = Product,
                        OrderQty = OrderQty, StockQty = OrderQty,
                        UomCode = Uom, StockUomCode = Uom,
                        UnitPrice = UnitPrice, ShippedQty = shippedQty,
                    },
                },
            });

            if (withMrpReference)
            {
                db.MrpRuns.Add(new MrpRun
                {
                    MrpRunId        = mrpNo,
                    Status          = MrpRunStatus.Draft,
                    PlantId         = Plant,
                    CreatedByRoleId = RoleId,
                });
                db.MrpRunLines.Add(new MrpRunLine
                {
                    MrpRunId     = mrpNo,
                    LineSeq      = 1,
                    SalesOrderId = soNo,
                    SalesOrderLineSeq = 1,
                    ProductCode  = Product,
                    RequiredQty  = OrderQty,
                    RequiredDate = DateTime.UtcNow.AddDays(30),
                });
            }

            await db.SaveChangesAsync();
            return soNo;
        }

        /// <param name="withMrpReference">
        /// 建立指回本單的 MRP 需求列。注意欄位是 <c>SalesOrderId</c>——
        /// 內部訂單與銷貨訂單共用該欄位存來源單號。
        /// </param>
        private async Task<string> SeedInternalOrderAsync(string caseName, decimal qtyPutAway, bool withMrpReference)
        {
            var ioNo  = $"{Prefix}NO-{Math.Abs(caseName.GetHashCode()) % 100000:D5}";
            var mrpNo = ioNo + "-MRP";
            using var _ = NewScope(out var db);

            await db.MrpRunLines.IgnoreQueryFilters()
                .Where(l => l.MrpRunId == mrpNo).ExecuteDeleteAsync();
            await db.MrpRuns.IgnoreQueryFilters()
                .Where(r => r.MrpRunId == mrpNo).ExecuteDeleteAsync();
            await db.InternalOrderLines.IgnoreQueryFilters()
                .Where(l => l.InternalOrderId == ioNo).ExecuteDeleteAsync();
            await db.InternalOrders.IgnoreQueryFilters()
                .Where(o => o.InternalOrderId == ioNo).ExecuteDeleteAsync();

            await EnsureMastersAsync(db);

            db.InternalOrders.Add(new InternalOrder
            {
                InternalOrderId = ioNo,
                OrderDate       = OrderDate,
                TypeCode        = "NO",
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Lines =
                {
                    new InternalOrderLine
                    {
                        InternalOrderId = ioNo, LineNo = 1, ProductCode = Product,
                        OrderQty = OrderQty, StockQty = OrderQty,
                        UomCode = Uom, StockUomCode = Uom,
                        QtyPutAway = qtyPutAway,
                    },
                },
            });

            if (withMrpReference)
            {
                db.MrpRuns.Add(new MrpRun
                {
                    MrpRunId        = mrpNo,
                    Status          = MrpRunStatus.Draft,
                    PlantId         = Plant,
                    CreatedByRoleId = RoleId,
                });
                db.MrpRunLines.Add(new MrpRunLine
                {
                    MrpRunId     = mrpNo,
                    LineSeq      = 1,
                    SalesOrderId = ioNo,
                    ProductCode  = Product,
                    RequiredQty  = OrderQty,
                    RequiredDate = DateTime.UtcNow.AddDays(30),
                });
            }

            await db.SaveChangesAsync();
            return ioNo;
        }

        /// <summary>
        /// 本檔用自己的一組主檔：這幾張單不動庫存，不需要期初量，
        /// 與過帳型單據共用反而會被對方的庫存重設干擾。
        /// </summary>
        private static async Task EnsureMastersAsync(ApplicationDbContext db)
        {
            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(u => u.UomCode == Uom))
                db.Uoms.Add(new Uom { UomCode = Uom, UomName = "個", DecimalPlaces = 4 });

            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Category))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Category, CategoryName = "訂單刪除測試類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                });

            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Product))
                db.Products.Add(new Product
                {
                    ProductCode = Product, ProductName = "訂單刪除測試料",
                    CategoryCode = Category,
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });

            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Supplier))
                db.Companies.Add(new Company
                {
                    CompanyCode = Supplier, CompanyName = "訂單刪除測試供應商",
                    IsSupplier = true, IsActive = true,
                });

            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Customer))
                db.Companies.Add(new Company
                {
                    CompanyCode = Customer, CompanyName = "訂單刪除測試客戶",
                    IsCustomer = true, IsActive = true,
                });

            await db.SaveChangesAsync();
        }

        // ── 查詢 ────────────────────────────────────────────────────────────

        private async Task<decimal> ReadPrOrderedQtyAsync(string prNo)
        {
            using var _ = NewScope(out var db);
            return await db.PurchaseRequestLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PurchaseRequestId == prNo && l.LineNo == 1)
                .Select(l => l.OrderedQty).FirstAsync();
        }

        private async Task<bool> PoExistsAsync(string poNo)
        {
            using var _ = NewScope(out var db);
            return await db.PurchaseOrders.IgnoreQueryFilters().AnyAsync(o => o.PurchaseOrderId == poNo);
        }

        private async Task<bool> SoExistsAsync(string soNo)
        {
            using var _ = NewScope(out var db);
            return await db.SalesOrders.IgnoreQueryFilters().AnyAsync(o => o.SalesOrderId == soNo);
        }

        private async Task<bool> InternalOrderExistsAsync(string ioNo)
        {
            using var _ = NewScope(out var db);
            return await db.InternalOrders.IgnoreQueryFilters().AnyAsync(o => o.InternalOrderId == ioNo);
        }

        private async Task<bool> MrpLineExistsAsync(string soNo)
        {
            using var _ = NewScope(out var db);
            return await db.MrpRunLines.IgnoreQueryFilters().AnyAsync(l => l.SalesOrderId == soNo);
        }
    }
}
