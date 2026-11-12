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
    /// 銷貨退回單（SalesReturn）的刪除／修改 × 過帳殘留守門——採購退貨的鏡像。
    ///
    /// 三個與其他單據不同的地方，都會影響斷言怎麼寫：
    ///   ① TypeCode 由 Controller 硬寫成 "GR"（系統內建類別，Direction=+1 退回入庫），
    ///      不吃單據上的 TypeCode，故種子不能用測試前綴。
    ///   ② 應收的變動不在退回單身上，而是負向沖銷**來源出貨單**的 AR 發票
    ///      （以「出貨單號＋出貨行號」對應明細）。只查退回單號的足跡會看起來乾淨。
    ///   ③ 刪除時回沖 <c>GoodsShipmentLine.ReturnedQty</c>，是過帳足跡涵蓋不到的純欄位回寫。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class SalesReturnPostingResidueTests : InvDocResidueTestBase
    {
        /// <summary>Controller 硬寫；系統內建類別（銷貨退回，Direction=+1）。</summary>
        private const string TypeCode     = "GR";
        private const string ShipmentType = Prefix + "-GS2";
        private const string Customer     = Prefix + "-CUSR";

        public SalesReturnPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/SalesReturn";

        protected override bool RequiresSimplifiedMode => true;

        protected override Task<string> SeedApprovedDraftAsync(string caseName)
            => SeedAsync(caseName, withSourceShipment: false);

        private async Task<string> SeedAsync(string caseName, bool withSourceShipment)
        {
            var docNo = DocNoFor(caseName, "SR");
            var gsNo  = docNo + "-GS";
            using var _ = NewScope(out var db);

            await ClearPostingsAsync(db, docNo);
            await ClearPostingsAsync(db, gsNo);
            await db.SalesReturnLines.IgnoreQueryFilters()
                .Where(l => l.SalesReturnId == docNo).ExecuteDeleteAsync();
            await db.SalesReturns.IgnoreQueryFilters()
                .Where(d => d.SalesReturnId == docNo).ExecuteDeleteAsync();
            await db.GoodsShipmentLines.IgnoreQueryFilters()
                .Where(l => l.GoodsShipmentId == gsNo).ExecuteDeleteAsync();
            await db.GoodsShipments.IgnoreQueryFilters()
                .Where(d => d.GoodsShipmentId == gsNo).ExecuteDeleteAsync();

            // 退回是入庫；另備出貨用的出庫類別。TypeCode "GR" 由 HasData 內建，不需自建。
            await EnsureSharedMastersAsync(db, ShipmentType, direction: -1);
            await EnsureCustomerAsync(db);

            if (withSourceShipment)
            {
                db.GoodsShipments.Add(new GoodsShipment
                {
                    GoodsShipmentId = gsNo,
                    ShipDate        = PostDate,
                    TypeCode        = ShipmentType,
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
                            GoodsShipmentId = gsNo, LineNo = 1, ProductCode = SharedProduct,
                            ShipQty = LineQty, StockQty = LineQty,
                            UomCode = SharedUom, StockUomCode = SharedUom,
                            UnitPrice = UnitCost, ExchangeRate = 1m,
                            LineAmount = LineQty * UnitCost, TotalAmount = LineQty * UnitCost,
                            AmountLocal = LineQty * UnitCost,
                            WhCode = SharedWh, LocationCode = SharedLoc, LotNo = "",
                            // 退回單建立時來源出貨已累加過已退量；刪單必須減回去。
                            ReturnedQty = LineQty,
                        },
                    },
                });
            }

            db.SalesReturns.Add(new SalesReturn
            {
                SalesReturnId   = docNo,
                ReturnDate      = PostDate,
                TypeCode        = TypeCode,
                CompanyCode     = Customer,
                GoodsShipmentId = withSourceShipment ? gsNo : null,
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                TotalAmount     = LineQty * UnitCost,
                Lines = new List<SalesReturnLine>
                {
                    new()
                    {
                        SalesReturnId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        GoodsShipmentId = withSourceShipment ? gsNo : null,
                        ShipmentLineNo  = withSourceShipment ? 1 : null,
                        ReturnQty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom,
                        UnitPrice = UnitCost, ExchangeRate = 1m,
                        LineAmount = LineQty * UnitCost, TotalAmount = LineQty * UnitCost,
                        AmountLocal = LineQty * UnitCost,
                        WhCode = SharedWh, LocationCode = SharedLoc, LotNo = "",
                        QtyPutAway = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.SalesReturnLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.SalesReturnId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.SalesReturns.IgnoreQueryFilters().AnyAsync(d => d.SalesReturnId == docNo);

        // ── 本單據專屬 ──────────────────────────────────────────────────────

        /// <summary>
        /// 草稿退回單刪除時，來源出貨單的已退貨量必須減回去（且只減這一次）。
        /// 回沖的下限 0 由單元測試
        /// <c>SalesReturnControllerTests.Delete_POST_ReturnedQtyReversal_FloorsAtZero</c> 守住。
        /// </summary>
        [Fact]
        public async Task DraftDelete_RestoresShipmentReturnedQty()
        {
            var docNo = await SeedAsync(nameof(DraftDelete_RestoresShipmentReturnedQty), withSourceShipment: true);
            var gsNo  = docNo + "-GS";
            var (client, token) = await NewAuthedClientAsync();

            Assert.Equal(LineQty, await ReadGsReturnedQtyAsync(gsNo));

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除草稿退回單");

            await AssertNoResidueAsync(docNo, "草稿刪除後");
            Assert.Equal(0m, await ReadGsReturnedQtyAsync(gsNo));
        }

        /// <summary>
        /// 已過帳的退回單不可刪，且來源出貨單的**應收金額**與**已退貨量**都不得被動到。
        ///
        /// 兩者都在退回單號之外：應收是被負向沖銷的那張 AR 發票，已退量是出貨明細的欄位。
        /// 守門若破，客戶那邊的應收金額被改小、出貨單的已退量被減回去，而退回單不見了——
        /// 對帳時只剩「這張出貨的應收和出貨金額對不起來」這一個症狀。
        /// </summary>
        [Fact]
        public async Task PostedReturn_Delete_Blocked_AndSourceArAndReturnedQtyUntouched()
        {
            var docNo = await SeedAsync(nameof(PostedReturn_Delete_Blocked_AndSourceArAndReturnedQtyUntouched),
                withSourceShipment: true);
            var gsNo  = docNo + "-GS";
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"/GoodsShipment/Confirm/{gsNo}"), "來源出貨確認過帳");
            var arAfterShipment = await ReadArTotalAsync(gsNo);
            Assert.True(arAfterShipment > 0m, $"前置條件不成立：出貨應開立應收，實際 {arAfterShipment}");

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "退回確認過帳");

            var arAfterReturn = await ReadArTotalAsync(gsNo);
            Assert.True(arAfterReturn < arAfterShipment,
                $"前置條件不成立：退回應負向回沖來源應收（{arAfterShipment} → {arAfterReturn}）。");

            var posted = await CaptureAsync(docNo);
            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已過帳退回單");

            await AssertUnchangedAsync(posted, docNo, "刪除被擋後");
            Assert.Equal(arAfterReturn, await ReadArTotalAsync(gsNo));
            Assert.Equal(LineQty, await ReadGsReturnedQtyAsync(gsNo));
        }

        // ── 種子輔助 ────────────────────────────────────────────────────────

        private async Task<decimal> ReadGsReturnedQtyAsync(string gsNo)
        {
            using var _ = NewScope(out var db);
            return await db.GoodsShipmentLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsShipmentId == gsNo && l.LineNo == 1)
                .Select(l => l.ReturnedQty).FirstAsync();
        }

        private async Task<decimal> ReadArTotalAsync(string goodsShipmentId)
        {
            using var _ = NewScope(out var db);
            return await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocId == goodsShipmentId)
                .Select(i => i.TotalAmount).FirstOrDefaultAsync();
        }

        private static async Task ClearPostingsAsync(ApplicationDbContext db, string docNo)
        {
            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();

            var arIds = await db.ARInvoices.IgnoreQueryFilters()
                .Where(i => i.SourceDocId == docNo).Select(i => i.ARInvoiceId).ToListAsync();
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
                CompanyCode = Customer, CompanyName = "殘留測試退回客戶",
                IsCustomer = true, IsSupplier = false, IsActive = true,
            });
            await db.SaveChangesAsync();
        }
    }
}
