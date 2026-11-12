using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 出貨單（GoodsShipment）的刪除／修改 × 過帳殘留守門——進貨單的鏡像，
    /// 差別在方向（出庫）與子帳（應收而非應付）。
    ///
    /// 與進貨單同樣有一個過帳足跡涵蓋不到的方向：<c>SalesOrderLine.ShippedQty</c>。
    /// 沒有異動也沒有傳票，純欄位回寫，任何以單號查足跡的斷言都看不見，
    /// <see cref="DraftDelete_RestoresSalesOrderShippedQty"/> 釘的就是這個數字。
    ///
    /// 回沖本身的下限（不得壓成負數）由單元測試
    /// <c>GoodsShipmentControllerTests.Delete_POST_ShippedQtyReversal_FloorsAtZero</c> 守住；
    /// 此處守的是另一件事——**守門有效時數字不得被動到**。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class GoodsShipmentPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-GS";
        private const string Customer = Prefix + "-CUST";

        public GoodsShipmentPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/GoodsShipment";

        /// <summary>完整模式下 Confirm 只轉 Confirmed（待揀出），庫存與 AR 都還沒動。</summary>
        protected override bool RequiresSimplifiedMode => true;

        protected override Task<string> SeedApprovedDraftAsync(string caseName)
            => SeedAsync(caseName, withSalesOrder: false);

        private async Task<string> SeedAsync(string caseName, bool withSalesOrder)
        {
            var docNo = DocNoFor(caseName, "GS");
            var soNo  = docNo + "-SO";
            using var _ = NewScope(out var db);

            await ClearArAsync(db, docNo);
            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.GoodsShipmentLines.IgnoreQueryFilters()
                .Where(l => l.GoodsShipmentId == docNo).ExecuteDeleteAsync();
            await db.GoodsShipments.IgnoreQueryFilters()
                .Where(d => d.GoodsShipmentId == docNo).ExecuteDeleteAsync();
            await db.SalesOrderLines.IgnoreQueryFilters()
                .Where(l => l.SalesOrderId == soNo).ExecuteDeleteAsync();
            await db.SalesOrders.IgnoreQueryFilters()
                .Where(o => o.SalesOrderId == soNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);
            await EnsureCustomerAsync(db);

            if (withSalesOrder)
            {
                db.SalesOrders.Add(new SalesOrder
                {
                    SalesOrderId    = soNo,
                    OrderDate       = PostDate,
                    CompanyCode     = Customer,
                    CreatedBy       = "admin",
                    PlantId         = Plant,
                    CreatedByRoleId = RoleId,
                    ApprovalStatus  = DocumentApprovalStatus.Approved,
                    Lines =
                    {
                        new SalesOrderLine
                        {
                            SalesOrderId = soNo, LineNo = 1, ProductCode = SharedProduct,
                            OrderQty = LineQty * 2, StockQty = LineQty * 2,
                            UomCode = SharedUom, StockUomCode = SharedUom,
                            UnitPrice = UnitCost,
                            // 出貨單建立時上游已累加過已出貨量；刪單必須減回去。
                            ShippedQty = LineQty,
                        },
                    },
                });
            }

            db.GoodsShipments.Add(new GoodsShipment
            {
                GoodsShipmentId = docNo,
                ShipDate        = PostDate,
                TypeCode        = TypeCode,
                CompanyCode     = Customer,
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                TotalAmount     = LineQty * UnitCost,
                Lines = new List<GoodsShipmentLine>
                {
                    new()
                    {
                        GoodsShipmentId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        SalesOrderId = withSalesOrder ? soNo : null,
                        SoLineNo     = withSalesOrder ? 1 : null,
                        ShipQty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom,
                        UnitPrice = UnitCost, ExchangeRate = 1m,
                        LineAmount = LineQty * UnitCost, TotalAmount = LineQty * UnitCost,
                        AmountLocal = LineQty * UnitCost,
                        WhCode = SharedWh, LocationCode = SharedLoc, LotNo = "",
                        QtyPutAway = 0m, ReturnedQty = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsShipmentId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.GoodsShipments.IgnoreQueryFilters().AnyAsync(d => d.GoodsShipmentId == docNo);

        // ── 本單據專屬 ──────────────────────────────────────────────────────

        /// <summary>確認一次必須同時留下庫存、總帳、應收三方足跡。</summary>
        [Fact]
        public async Task Confirm_LeavesInventoryJournalAndArFootprints()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(Confirm_LeavesInventoryJournalAndArFootprints));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認過帳");

            var f = await CaptureAsync(docNo);
            Assert.True(f.MovementLines > 0, $"應產生庫存異動，實際 {f}");
            Assert.True(f.JournalEntries > 0, $"應產生傳票，實際 {f}");
            Assert.Equal(1, f.ArInvoices);
        }

        /// <summary>
        /// 草稿出貨單刪除時，必須把訂單的已出貨量減回去——且只能減一次。
        /// 回沖沒有下限保護，多減一次就是負的已出貨量，訂單剩餘可出量因此
        /// 算得比訂購量還大，超出的部分沒有任何守門會擋。
        /// </summary>
        [Fact]
        public async Task DraftDelete_RestoresSalesOrderShippedQty()
        {
            var docNo = await SeedAsync(nameof(DraftDelete_RestoresSalesOrderShippedQty), withSalesOrder: true);
            var soNo  = docNo + "-SO";
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(LineQty, await ReadSoShippedQtyAsync(soNo));

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除草稿出貨單");

            await AssertNoResidueAsync(docNo, "草稿刪除後");
            Assert.Equal(0m, await ReadSoShippedQtyAsync(soNo));
        }

        /// <summary>已過帳的出貨單刪除被擋下時，訂單已出貨量不得被動到。</summary>
        [Fact]
        public async Task PostedDelete_Blocked_AndSalesOrderShippedQtyUntouched()
        {
            var docNo = await SeedAsync(nameof(PostedDelete_Blocked_AndSalesOrderShippedQtyUntouched),
                withSalesOrder: true);
            var soNo  = docNo + "-SO";
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認過帳");
            var posted = await CaptureAsync(docNo);

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已過帳出貨單");

            await AssertUnchangedAsync(posted, docNo, "刪除被擋後");
            Assert.Equal(LineQty, await ReadSoShippedQtyAsync(soNo));
        }

        // ── 種子輔助 ────────────────────────────────────────────────────────

        private async Task<decimal> ReadSoShippedQtyAsync(string soNo)
        {
            using var _ = NewScope(out var db);
            return await db.SalesOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.SalesOrderId == soNo && l.LineNo == 1)
                .Select(l => l.ShippedQty).FirstAsync();
        }

        private static async Task ClearArAsync(ApplicationDbContext db, string docNo)
        {
            var arIds = await db.ARInvoices.IgnoreQueryFilters()
                .Where(i => i.SourceDocId == docNo)
                .Select(i => i.ARInvoiceId).ToListAsync();
            if (arIds.Count > 0)
            {
                await db.ARInvoiceLines.IgnoreQueryFilters()
                    .Where(l => arIds.Contains(l.ARInvoiceId)).ExecuteDeleteAsync();
                await db.ARInvoices.IgnoreQueryFilters()
                    .Where(i => arIds.Contains(i.ARInvoiceId)).ExecuteDeleteAsync();
            }

            var journalIds = await db.JournalEntries.IgnoreQueryFilters()
                .Where(e => e.SourceDocId == docNo).Select(e => e.JournalId).ToListAsync();
            if (journalIds.Count > 0)
            {
                await db.JournalLines.IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalId)).ExecuteDeleteAsync();
                await db.JournalEntries.IgnoreQueryFilters()
                    .Where(e => journalIds.Contains(e.JournalId)).ExecuteDeleteAsync();
            }
        }

        private static async Task EnsureCustomerAsync(ApplicationDbContext db)
        {
            if (await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Customer)) return;
            db.Companies.Add(new Company
            {
                CompanyCode = Customer, CompanyName = "殘留測試客戶",
                IsCustomer = true, IsSupplier = false, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
    }
}
