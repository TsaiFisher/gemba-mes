using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 跨廠調撥單（CrossPlantTransfer）的刪除／修改 × 過帳殘留守門。
    ///
    /// 這張單是本組唯一「過帳分兩段、且中途可反悔」的單據：
    /// 來源廠 Confirm 過 XP（出）→ InTransit → 目的廠 Receive 過 XI（入）→ Posted。
    /// 中間的 CancelConfirm 走的是**沖銷**（VoidByMovementsAsync 產生反向異動）而非刪除，
    /// 所以它的正確結果不是「足跡歸零」而是「淨額歸零、筆數加倍」——那正是
    /// <see cref="TestCancelConfirm_ReversesToZeroNet_NotDeletion"/> 要釘的東西。
    /// 若哪天有人把沖銷改成直接刪異動，帳面淨量一樣是 0，但庫存異動歷史憑空消失，
    /// 只有比對「筆數」才抓得到。
    ///
    /// Receive 那一段（XI）以 <see cref="SwitchActivePlantAsync"/> 暫時把 admin 的作用廠別
    /// 切到目的廠來覆蓋——當前廠別來自 <c>ApplicationUser.ActivePlantId</c> 而非驗證 handler，
    /// 所以不需要動測試驗證基建。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class CrossPlantTransferPostingResidueTests : InvDocResidueTestBase
    {
        /// <summary>目的廠：Save 要求必須與來源廠不同；XP 過帳本身不碰目的廠資料。</summary>
        private const string DestPlant = "P2";

        public CrossPlantTransferPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/CrossPlantTransfer";

        /// <summary>Confirm 無 simplified 分支，一律直接過帳 XP。</summary>
        protected override bool RequiresSimplifiedMode => false;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "XP");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.CrossPlantTransferLines.IgnoreQueryFilters()
                .Where(l => l.CrossPlantTransferId == docNo).ExecuteDeleteAsync();
            await db.CrossPlantTransfers.IgnoreQueryFilters()
                .Where(d => d.CrossPlantTransferId == docNo).ExecuteDeleteAsync();

            // XP 的 TypeCode 由服務寫死，不吃單據類別；共用種子在此只為了主檔與期初庫存。
            await EnsureSharedMastersAsync(db, Prefix + "-XPT", direction: -1);

            db.CrossPlantTransfers.Add(new CrossPlantTransfer
            {
                CrossPlantTransferId = docNo,
                TransferDate    = PostDate,
                FromWhCode      = SharedWh,
                ToWhCode        = SharedWh,
                ToPlantId       = DestPlant,
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Lines = new List<CrossPlantTransferLine>
                {
                    new()
                    {
                        CrossPlantTransferId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        Qty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom,
                        FromWhCode = SharedWh, FromLocationCode = SharedLoc,
                        ToWhCode   = SharedWh, ToLocationCode   = SharedLoc,
                        QtyShipped = 0m, QtyReceived = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.CrossPlantTransferLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.CrossPlantTransferId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.CrossPlantTransfers.IgnoreQueryFilters().AnyAsync(d => d.CrossPlantTransferId == docNo);

        protected override IDictionary<string, string> EditFormOrNull(string docNo) => new Dictionary<string, string>
        {
            ["CrossPlantTransferId"] = docNo,
            ["TransferDate"] = PostDate.ToString("yyyy-MM-dd"),
            ["FromWhCode"]   = SharedWh,
            ["ToWhCode"]     = SharedWh,
            ["ToPlantId"]    = DestPlant,
            ["Lines[0].LineNo"]           = "1",
            ["Lines[0].ProductCode"]      = SharedProduct,
            ["Lines[0].Qty"]              = "999",
            ["Lines[0].StockQty"]         = "999",
            ["Lines[0].UomCode"]          = SharedUom,
            ["Lines[0].StockUomCode"]     = SharedUom,
            ["Lines[0].FromWhCode"]       = SharedWh,
            ["Lines[0].FromLocationCode"] = SharedLoc,
            ["Lines[0].ToWhCode"]         = SharedWh,
            ["Lines[0].ToLocationCode"]   = SharedLoc,
        };

        // ── 本單據專屬：中途反悔 ────────────────────────────────────────────

        /// <summary>
        /// 已發出（InTransit）的調撥單撤回時，必須是「沖銷」而非「抹除」：
        /// 異動筆數加倍、淨量與淨成本歸零、庫存回到原始量、單據回 Draft。
        ///
        /// 這是全庫少數「過帳後還能合法退回可編輯狀態」的路徑，也是最容易寫成
        /// 直接刪異動的地方——刪掉的話帳面數字一樣對，但庫存卡不出這批料曾經出去過。
        /// </summary>
        [Fact]
        public async Task CancelConfirm_ReversesToZeroNet_NotDeletion()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(CancelConfirm_ReversesToZeroNet_NotDeletion));
            var (client, token) = await NewAuthedClientAsync();

            var onHandBefore = await ReadOnHandAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認發出");
            var shipped = await CaptureAsync(docNo);
            Assert.True(shipped.MovementLines > 0, $"前置條件不成立：確認發出應產生 XP 異動，實際 {shipped}");
            Assert.Equal(onHandBefore - LineQty, await ReadOnHandAsync());

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/CancelConfirm/{docNo}"), "撤回發出");

            var reversed = await CaptureAsync(docNo);
            Assert.True(reversed.MovementLines == shipped.MovementLines * 2,
                $"撤回發出應以反向異動沖銷（筆數加倍），而非刪掉原異動。\n"
                + $"  發出後：{shipped}\n  撤回後：{reversed}");
            Assert.Equal(0m, reversed.MovementNetQty);
            Assert.Equal(0m, reversed.MovementTotalCost);
            Assert.Equal(onHandBefore, await ReadOnHandAsync());

            using var _ = NewScope(out var db);
            var doc = await db.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(d => d.CrossPlantTransferId == docNo);
            Assert.Equal(DocStatus.Draft, doc.Status);
        }

        /// <summary>
        /// 撤回後單據回到 Draft，此時刪除是合法的——但沖銷異動必須留著。
        /// 這條專門守「回到草稿就以為可以清乾淨」的直覺錯誤：庫存異動是歷史紀錄，
        /// 已發生過的出入庫不因單據被刪而應該消失（消失了庫存卡就對不起 StockLedger）。
        /// </summary>
        [Fact]
        public async Task CancelledDoc_Delete_Succeeds_ButReversalHistoryRemains()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(CancelledDoc_Delete_Succeeds_ButReversalHistoryRemains));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認發出");
            AssertAllowed(await PostActionAsync(client, token, $"{Route}/CancelConfirm/{docNo}"), "撤回發出");

            var beforeDelete = await CaptureAsync(docNo);
            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已撤回的草稿");

            var after = await CaptureAsync(docNo);
            Assert.Equal(beforeDelete.MovementLines, after.MovementLines);
            Assert.Equal(0m, after.MovementNetQty);
        }

        /// <summary>
        /// 目的廠收貨（XI）：全流程唯一一段在**另一個廠別身分**下執行的過帳。
        ///
        /// 這段原本沒有覆蓋，理由是「TestAuthHandler 解出的廠別固定 P1」——那個說法只對一半。
        /// 廠別不是 TestAuthHandler 給的，它是 <c>UserContext.PlantId</c> 讀
        /// <c>ApplicationUser.ActivePlantId</c>，也就是資料庫裡的一個欄位。
        /// 把 admin 的作用廠別暫時切到 P2，Receive 就走得完，不需要動驗證基建。
        ///
        /// 沒測到的後果不是「少一條斷言」：XI 是整條跨廠鏈唯一把料**加回**帳上的一段，
        /// 它若壞掉（例如寫錯 PlantId 寫回來源廠），料在來源廠扣掉、目的廠沒進來，
        /// 兩張庫存卡各自都對得起自己，只有兩廠相加才看得出憑空少一批。
        /// </summary>
        [Fact]
        public async Task Receive_AsDestPlant_PostsXiIntoDestPlant()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(Receive_AsDestPlant_PostsXiIntoDestPlant));
            var (client, token) = await NewAuthedClientAsync();

            // 目的廠的庫存列跨測試輪次會留著（收進去的料沒有人清），所以只能比增量。
            var destBefore = await ReadDestOnHandAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "來源廠確認發出");

            // 來源廠身分收貨必須被擋（守門本身也順帶釘住）
            var wrongPlant = await PostActionAsync(client, token, $"{Route}/Receive/{docNo}");
            AssertBlocked(wrongPlant, "來源廠身分收貨");

            await SwitchActivePlantAsync(DestPlant);
            try
            {
                var (destClient, destToken) = await NewAuthedClientAsync();
                // 切換前就開著的頁面（快照仍是來源廠）：必須被擋、不寫入（security-review §5 #3）
                var stale = await PostActionAsync(destClient, destToken, $"{Route}/Receive/{docNo}");
                AssertBlocked(stale, "切換廠別前開啟的頁面送出收貨");
                Assert.Contains(DestPlant, stale.Message);   // 是廠別快照擋下的，不是其他守門
                // 切換後重新開啟的頁面：快照是目的廠
                UseRenderedPlant(destClient, DestPlant);
                AssertAllowed(await PostActionAsync(destClient, destToken, $"{Route}/Receive/{docNo}"), "目的廠收貨");
            }
            finally
            {
                await SwitchActivePlantAsync(Plant);
            }

            using var _ = NewScope(out var db);

            var xi = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.DocNo == docNo && m.TypeCode == "XI").ToListAsync();
            Assert.Single(xi);
            Assert.Equal(DestPlant, xi[0].PlantId);   // 寫回來源廠 = 料憑空消失
            Assert.Equal(1, xi[0].Direction);
            Assert.Equal(LineQty, xi[0].StockQty);

            var doc = await db.CrossPlantTransfers.IgnoreQueryFilters().AsNoTracking()
                .Include(d => d.Lines)
                .FirstAsync(d => d.CrossPlantTransferId == docNo);
            Assert.Equal(DocStatus.Posted, doc.Status);
            Assert.Equal(LineQty, doc.Lines.Single().QtyReceived);

            // 目的廠確實收到料：來源廠扣掉的量在 P2 帳上出現
            Assert.Equal(destBefore + LineQty, await ReadDestOnHandAsync());
        }

        /// <summary>
        /// 已被目的廠收貨的單，來源廠不得再撤回發出——撤回會沖掉 XP，
        /// 但 XI 已經把料加進目的廠，沖掉之後兩廠合計憑空多一批。
        /// </summary>
        [Fact]
        public async Task ReceivedDoc_CancelConfirm_IsRefused()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(ReceivedDoc_CancelConfirm_IsRefused));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "來源廠確認發出");

            await SwitchActivePlantAsync(DestPlant);
            try
            {
                var (destClient, destToken) = await NewAuthedClientAsync();
                UseRenderedPlant(destClient, DestPlant);
                AssertAllowed(await PostActionAsync(destClient, destToken, $"{Route}/Receive/{docNo}"), "目的廠收貨");
            }
            finally
            {
                await SwitchActivePlantAsync(Plant);
            }

            var before = await CaptureAsync(docNo);
            AssertBlocked(await PostActionAsync(client, token, $"{Route}/CancelConfirm/{docNo}"), "收貨後撤回發出");
            await AssertUnchangedAsync(before, docNo, "收貨後撤回發出");
        }

        /// <summary>
        /// 切換 admin 的作用廠別（<c>ApplicationUser.ActivePlantId</c>）。
        ///
        /// 這是整合測試唯一能改變「當前廠別」的地方——<c>UserContext</c> 每個 request
        /// 重新讀使用者，所以切完之後開的 client 就是新廠別的身分。
        /// 本組測試 collection 不平行（<c>DisableTestParallelization</c>），
        /// 共用的 admin 被暫時改掉不會影響同時跑的其他測試；仍一律在 finally 切回。
        /// </summary>
        private async Task SwitchActivePlantAsync(string plantId)
        {
            using var _ = NewScope(out var db);

            if (!await db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == plantId))
            {
                db.Plants.Add(new Plant { Id = plantId, Name = plantId });
                await db.SaveChangesAsync();
            }

            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            admin.ActivePlantId = plantId;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 模擬「在這個廠別下開啟的頁面」：測試工廠預設帶 P1 的廠別快照（X-Plant-Context），
        /// 切到其他廠後要換成那個廠，否則 StalePlantContextFilter 會以 409 擋下。
        /// </summary>
        private static void UseRenderedPlant(HttpClient client, string plantId)
        {
            client.DefaultRequestHeaders.Remove("X-Plant-Context");
            client.DefaultRequestHeaders.Add("X-Plant-Context", plantId);
        }

        /// <summary>目的廠（P2）同品號同儲位的在手量。</summary>
        private async Task<decimal> ReadDestOnHandAsync()
        {
            using var _ = NewScope(out var db);
            return await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == DestPlant && i.ProductCode == SharedProduct
                            && i.LocationCode == SharedLoc && i.LotNo == "")
                .Select(i => i.QtyOnHand).FirstOrDefaultAsync();
        }

        private async Task<decimal> ReadOnHandAsync()
        {
            using var _ = NewScope(out var db);
            return await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == SharedProduct
                            && i.LocationCode == SharedLoc && i.LotNo == "")
                .Select(i => i.QtyOnHand).FirstAsync();
        }
    }
}
