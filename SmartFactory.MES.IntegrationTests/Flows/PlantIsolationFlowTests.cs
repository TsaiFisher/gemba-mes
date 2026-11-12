using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §8 的 5-2（資料權限）與 5-3（多廠隔離）。
    ///
    /// 這兩項的通過標準寫的是「看不到別人的資料」，但人工測試很難證明**沒有**看到什麼：
    /// 測試員只能翻幾頁列表，翻不到不等於查不到。真正該釘的是兩層機制：
    ///
    ///   ① 全域查詢過濾（`ApplicationDbContext.OnModelCreating`）——
    ///      `IPlantEntity` 比 `CurrentPlantId`、`IDataAuthEntity` 比角色／組織路徑。
    ///      這層是宣告式的，只要 entity 有實作介面就自動套用，漏的通常不是它。
    ///
    ///   ② **稽核**——`DataAuditService` 的每一支都 `IgnoreQueryFilters()` 之後
    ///      手寫 `plantId == null || x.PlantId == plantId`。十六支各寫各的，
    ///      任何一支在 join 進來的第二張表上漏掉那個條件，甲廠的稽核就會列出乙廠的單號。
    ///      這不只是機密外洩：關帳前稽核有異常就不准關帳（§8.1），
    ///      漏一個條件等於「乙廠有爛帳，甲廠關不了帳」，而且畫面上完全看不出為什麼。
    ///
    /// 所以本測試只在 A 廠建資料，然後以 **B 廠**的身分把十六支稽核全跑一遍，
    /// 斷言結果裡不得出現任何一個 A 廠的單號。一次涵蓋全部，漏哪一支就指名哪一支。
    /// </summary>
    [Collection("Integration")]
    public class PlantIsolationFlowTests : FlowTestBase
    {
        protected override string Plant => PlantA;
        protected override IEnumerable<string> ExtraPlants => new[] { PlantB };

        private const string PlantA = "ZZW5A";
        private const string PlantB = "ZZW5B";

        private const string Mat    = "FLOW-W5A-MAT";
        private const string Po     = "FLOW-W5A-PO";
        /// <summary>刻意製造的孤兒過帳：沒有任何單據叫這個名字，稽核一定會報它。</summary>
        private const string Orphan = "FLOW-W5A-ORPHAN";

        private static readonly DateOnly OrderDate = new(2095, 11, 1);
        private static readonly DateOnly RecvDate  = new(2095, 11, 10);

        private const decimal Qty = 100m, Price = 250m, TaxRate = 0.05m;
        private const decimal Net = Qty * Price, Tax = Net * TaxRate, Gross = Net + Tax;

        /// <summary>A 廠建立的所有單號；B 廠不論從哪個入口都不該看到其中任何一個。</summary>
        private readonly List<string> _plantADocs = new() { Po, Orphan };

        public PlantIsolationFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "廠別隔離測試料"));
            using var scope = NewScope(out var db);
            if (!await db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == PlantB))
                db.Plants.Add(new SmartFactory.MES.Models.Plant { Id = PlantB, Name = $"流程測試廠 {PlantB}" });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 5-3：以 B 廠身分跑全部稽核，一筆 A 廠的單號都不得出現。
        ///
        /// 這條會失敗的方式只有一種——某支稽核在某張表上漏了廠別條件。
        /// 失敗訊息會直接指出是哪一支（AuditType）與哪一張單。
        /// </summary>
        [Fact]
        public async Task AuditsRunForOnePlant_MustNotReportAnotherPlantsDocuments()
        {
            await SeedPlantAChainAsync();

            using var scope = NewScope(out var db);
            var audit = Svc<IDataAuditService>(scope);

            // 先確認這條測試有牙：A 廠的稽核必須真的報出那筆孤兒過帳。
            // 少了這一步，下面的迴圈在「B 廠一筆異常都沒有」時會空轉而假綠——
            // 一支永遠不執行斷言的測試，看起來跟真的一模一樣。
            var plantAFailures = await audit.RunAllAsync(PlantA);
            Assert.Contains(plantAFailures.Failures,
                f => f.AuditType == "ORPHAN_STOCK_MOVEMENT" && f.DocId == Orphan);

            var result = await audit.RunAllAsync(PlantB);

            foreach (var f in result.Failures)
            {
                Assert.True(
                    f.PlantId == null || f.PlantId == PlantB,
                    $"以 {PlantB} 廠執行稽核，卻回報了 {f.PlantId} 廠的異常：[{f.AuditType}] {f.DocId}");

                Assert.False(
                    _plantADocs.Any(d => f.DocId != null && f.DocId.Contains(d)),
                    $"稽核 [{f.AuditType}] 把 A 廠的單號洩漏給了 B 廠：{f.DocId}");
            }
        }

        /// <summary>
        /// 5-3：全域查詢過濾這一層。B 廠的上下文查不到 A 廠的單據、庫存與傳票。
        /// 分開驗是因為它與稽核是兩套獨立機制——稽核一律 IgnoreQueryFilters，
        /// 過濾層再嚴也擋不住它。
        /// </summary>
        [Fact]
        public async Task PlantBContext_SeesNothingThatBelongsToPlantA()
        {
            await SeedPlantAChainAsync();

            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = PlantB;
            db.CurrentRoleId  = RoleId;

            Assert.Empty(await db.PurchaseOrders.Where(p => p.PurchaseOrderId == Po).ToListAsync());
            Assert.Null(await db.PurchaseOrders.FindAsync(Po));
            Assert.Empty(await db.GoodsReceipts.ToListAsync());
            Assert.Empty(await db.Inventories.Where(i => i.ProductCode == Mat).ToListAsync());
            Assert.Empty(await db.APInvoices.ToListAsync());
            Assert.Empty(await db.JournalEntries.ToListAsync());

            // 對照組：同一份資料在 A 廠的上下文下必須查得到，
            // 否則「查不到」可能只是因為根本沒建起來，這條測試就什麼都沒證明。
            db.CurrentPlantId = PlantA;
            Assert.NotNull(await db.PurchaseOrders.FirstOrDefaultAsync(p => p.PurchaseOrderId == Po));
        }

        /// <summary>
        /// 5-2：建立者自己必須看得到自己剛建的單。
        ///
        /// 作業書特別點名這一條是有原因的：`IDataAuthEntity` 的過濾在
        /// 沒有組織路徑時退化成 `CreatedByRoleId == CurrentRoleId`，
        /// 而 `CreatedByRoleId` 是 `SaveChanges` 時由 `ApplyGlobalValues` 補的。
        /// 只要單據不是經過那條路徑落地（種子腳本、批次匯入、直接 SQL），
        /// 這個欄位就是 null，建立者本人一查就是 404——**症狀完全不像權限問題**。
        /// </summary>
        [Fact]
        public async Task Creator_SeesOwnDocument_ButAnotherRoleDoesNot()
        {
            await SeedPlantAChainAsync();

            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = PlantA;

            // 建立者本人（同角色）
            db.CurrentRoleId = RoleId;
            var mine = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.PurchaseOrderId == Po);
            Assert.NotNull(mine);
            Assert.Equal(RoleId, mine!.CreatedByRoleId);   // 欄位真的有被補上，不是靠 legacy 放行

            // 另一個角色（無組織路徑 → 退化成逐角色比對）
            db.CurrentRoleId = "ZZW5-OTHER-ROLE";
            Assert.Null(await db.PurchaseOrders.FirstOrDefaultAsync(p => p.PurchaseOrderId == Po));
        }

        // ── 佈景：A 廠跑一條採購 → 進貨 → 入庫 → 應付認列 ─────────────────

        private async Task SeedPlantAChainAsync()
        {
            using (var scope = NewScope(out var db))
            {
                var po = new PurchaseOrder
                {
                    PurchaseOrderId = Po, OrderDate = OrderDate, CreatedBy = UserId,
                    SupplierCode = Supplier, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = PlantA, CreatedByRoleId = RoleId,
                };
                po.Lines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                    OrderQty = Qty, UomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();
            }

            string grId;
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
                            ReceiptQty = Qty, StockQty = Qty,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                            TaxRate = TaxRate, LineAmount = Net, TotalAmount = Gross,
                        }
                    ],
                }, UserId, PlantA);
                grId = gr.GoodsReceiptId;
                _plantADocs.Add(grId);
            }

            using (var scope = NewScope(out var db))
            {
                var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                    .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);
                var line = gr.Lines.Single();

                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = PlantA, DocNo = grId, LineNo = line.LineNo,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = line.StockQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                    UpdateQtyPutAwayAsync      = q => { line.QtyPutAway = q; return Task.CompletedTask; },
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => gr.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var ap = await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, Qty);
                _plantADocs.Add(ap.APInvoiceId);
            }

            // 刻意留一筆孤兒過帳在 A 廠：單號不對應任何單據，AuditOrphanPosting 必報。
            // 這是給廠別隔離用的「可辨識髒資料」——B 廠的稽核若看得到它，就是漏了廠別條件。
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = PlantA, DocNo = Orphan, LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = 1m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }
        }
    }
}
