using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 採購分攤費用單（LandedCostAllocation）的刪除／修改 × 過帳殘留守門。
    ///
    /// 本組唯一「完全不動庫存數量」的單據：確認時把外包費用滾進
    /// <c>Inventory.UnitCost</c> 與 FIFO 成本層，並產生一張傳票。
    /// 它的殘留形態因此和其他單據都不同——**帳面數量永遠是對的**，
    /// 錯的是單價；而單價沒有任何「筆數」可以核對，重複確認一次就永久多墊一層成本，
    /// 事後除了翻傳票沒有別的辦法反推。故本檔在共用斷言之外，
    /// 額外把 UnitCost 本身當成一項足跡來釘。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class LandedCostAllocationPostingResidueTests : InvDocResidueTestBase
    {
        /// <summary>待分攤費用總額；取整數避免比對時卡在分攤餘數。</summary>
        private const decimal LandedCost = 100m;

        public LandedCostAllocationPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/LandedCostAllocation";

        /// <summary>不經庫存過帳服務，與簡化模式無關。</summary>
        protected override bool RequiresSimplifiedMode => false;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "LC");
            using var _ = NewScope(out var db);

            await db.JournalLines.IgnoreQueryFilters()
                .Where(l => db.JournalEntries.IgnoreQueryFilters()
                    .Where(e => e.SourceDocId == docNo).Select(e => e.JournalId).Contains(l.JournalId))
                .ExecuteDeleteAsync();
            await db.JournalEntries.IgnoreQueryFilters()
                .Where(e => e.SourceDocId == docNo).ExecuteDeleteAsync();
            await db.LandedCostAllocationLines.IgnoreQueryFilters()
                .Where(l => l.LandedCostAllocationId == docNo).ExecuteDeleteAsync();
            await db.LandedCostAllocations.IgnoreQueryFilters()
                .Where(d => d.LandedCostAllocationId == docNo).ExecuteDeleteAsync();

            // 共用種子把 UnitCost 重設回基準，確認後的單價漲幅才可預期。
            await EnsureSharedMastersAsync(db, Prefix + "-LCT", direction: -1);

            db.LandedCostAllocations.Add(new LandedCostAllocation
            {
                LandedCostAllocationId = docNo,
                DocDate         = PostDate,
                Basis           = LandedCostBasis.Amount,
                TotalLandedCost = LandedCost,
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Lines = new List<LandedCostAllocationLine>
                {
                    new()
                    {
                        LandedCostAllocationId = docNo, LineNo = 1,
                        GoodsReceiptId = Prefix + "-GR", GoodsReceiptLineNo = 1,
                        ProductCode = SharedProduct, LocationCode = SharedLoc, LotNo = "",
                        ReceiptQty = LineQty, PurchaseAmount = LineQty * UnitCost,
                        Weight = LineQty, OriginalUnitCost = UnitCost,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.LandedCostAllocationLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.LandedCostAllocationId == docNo && l.LineNo == 1)
                .Select(l => l.ReceiptQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.LandedCostAllocations.IgnoreQueryFilters().AnyAsync(d => d.LandedCostAllocationId == docNo);

        protected override IDictionary<string, string> EditFormOrNull(string docNo) => new Dictionary<string, string>
        {
            ["LandedCostAllocationId"] = docNo,
            ["DocDate"]         = PostDate.ToString("yyyy-MM-dd"),
            ["Basis"]           = nameof(LandedCostBasis.Amount),
            ["TotalLandedCost"] = "99999",
            ["Lines[0].LineNo"]             = "1",
            ["Lines[0].GoodsReceiptId"]     = Prefix + "-GR",
            ["Lines[0].GoodsReceiptLineNo"] = "1",
            ["Lines[0].ProductCode"]        = SharedProduct,
            ["Lines[0].LocationCode"]       = SharedLoc,
            ["Lines[0].ReceiptQty"]         = "10",
            ["Lines[0].PurchaseAmount"]     = "50",
            ["Lines[0].Weight"]             = "10",
            ["Lines[0].OriginalUnitCost"]   = "5",
        };

        // ── 本單據專屬：成本足跡 ────────────────────────────────────────────

        /// <summary>
        /// 確認一次把費用滾進單價，且**只滾一次**。
        ///
        /// 這是本單據最危險的一條：重複確認若守門失效，單價會被墊第二層，
        /// 而庫存數量、異動筆數、傳票張數若也一併重複還算好查——
        /// 最壞的情況是狀態守門擋住了傳票卻沒擋住成本回寫，那就完全無跡可循。
        /// 故此處把「傳票」與「單價」分開斷言，不靠單一足跡涵蓋。
        /// </summary>
        [Fact]
        public async Task ConfirmTwice_DoesNotDoubleBumpUnitCost()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(ConfirmTwice_DoesNotDoubleBumpUnitCost));
            var (client, token) = await NewAuthedClientAsync();

            var costBefore = await ReadUnitCostAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "第一次確認");
            var costAfterFirst = await ReadUnitCostAsync();
            Assert.True(costAfterFirst > costBefore,
                $"前置條件不成立：確認後單價應上升（{costBefore} → {costAfterFirst}）。");

            var posted = await CaptureAsync(docNo);
            Assert.True(posted.JournalEntries > 0, $"前置條件不成立：確認應產生傳票，實際 {posted}");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "第二次確認");
            await AssertUnchangedAsync(posted, docNo, "重複確認後");
            Assert.Equal(costAfterFirst, await ReadUnitCostAsync());
        }

        /// <summary>
        /// 已確認的分攤單不可刪；擋下後單價與傳票都不得變動。
        /// 若這道守門失效，留下的是「一批單價被墊高、但查不到任何來源單據的庫存」——
        /// 比孤兒庫存異動更難察覺，因為數量對得起來。
        /// </summary>
        [Fact]
        public async Task ConfirmedDoc_Delete_Blocked_AndUnitCostUntouched()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(ConfirmedDoc_Delete_Blocked_AndUnitCostUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認分攤");
            var costAfter = await ReadUnitCostAsync();

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已確認的分攤單");

            Assert.Equal(costAfter, await ReadUnitCostAsync());
        }

        private async Task<decimal> ReadUnitCostAsync()
        {
            using var _ = NewScope(out var db);
            return await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == SharedProduct
                            && i.LocationCode == SharedLoc && i.LotNo == "")
                .Select(i => i.UnitCost).FirstAsync();
        }
    }
}
