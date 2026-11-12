using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 備料單（MaterialStagingOrder）的刪除／修改 × 過帳殘留守門。
    ///
    /// 本組唯一「確認時不產生庫存異動」的單據：Confirm 建立的是
    /// <see cref="InventoryReservation"/>（硬預留），庫存實際搬動要等 Stage。
    /// 也因此它是最容易被漏掉的一種殘留——預留殘留不會讓任何一張報表對不起來，
    /// 帳面在手量完全正確，只是可用量莫名其妙少一塊，而少的那塊指向一張已不存在的單。
    /// 這正是把預留納入 <see cref="PostingResidueTestBase.PostingFootprint"/> 的原因。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class MaterialStagingPostingResidueTests : InvDocResidueTestBase
    {
        public MaterialStagingPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/MaterialStaging";

        /// <summary>Confirm 只鎖預留，與庫存簡化模式無關。</summary>
        protected override bool RequiresSimplifiedMode => false;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "MS");
            using var _ = NewScope(out var db);

            // 預留是廠級可用量的扣項，跨測試會互相排擠；每輪把本組留下的全清掉，
            // 否則重跑幾輪之後 Confirm 會因「可用量不足」假紅。
            await db.InventoryReservations.IgnoreQueryFilters()
                .Where(r => r.SourceDocId.StartsWith(Prefix)).ExecuteDeleteAsync();
            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.MaterialStagingLines.IgnoreQueryFilters()
                .Where(l => l.StagingId == docNo).ExecuteDeleteAsync();
            await db.MaterialStagingOrders.IgnoreQueryFilters()
                .Where(d => d.StagingId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, Prefix + "-MST", direction: -1);

            db.MaterialStagingOrders.Add(new MaterialStagingOrder
            {
                StagingId           = docNo,
                SourceType          = "Manual",
                StagingWhCode       = SharedWh,
                StagingLocationCode = SharedLoc,
                Status              = "Draft",
                ApprovalStatus      = DocumentApprovalStatus.Approved,
                CreatedBy           = "admin",
                PlantId             = Plant,
                CreatedByRoleId     = RoleId,
                Lines = new List<MaterialStagingLine>
                {
                    new()
                    {
                        StagingId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        UomCode = SharedUom, StockUomCode = SharedUom,
                        RequiredQty = LineQty, PickedQty = 0m,
                        FromWarehouseCode = SharedWh, FromLocationCode = SharedLoc,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.MaterialStagingLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.StagingId == docNo && l.LineNo == 1)
                .Select(l => l.RequiredQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.MaterialStagingOrders.IgnoreQueryFilters().AnyAsync(d => d.StagingId == docNo);

        protected override IDictionary<string, string> EditFormOrNull(string docNo) => new Dictionary<string, string>
        {
            ["StagingId"]           = docNo,
            ["SourceType"]          = "Manual",
            ["StagingWhCode"]       = SharedWh,
            ["StagingLocationCode"] = SharedLoc,
            ["Lines[0].LineNo"]            = "1",
            ["Lines[0].ProductCode"]       = SharedProduct,
            ["Lines[0].UomCode"]           = SharedUom,
            ["Lines[0].StockUomCode"]      = SharedUom,
            ["Lines[0].RequiredQty"]       = "999",
            ["Lines[0].FromWarehouseCode"] = SharedWh,
            ["Lines[0].FromLocationCode"]  = SharedLoc,
        };

        // ── 本單據專屬：預留生命週期 ────────────────────────────────────────

        /// <summary>
        /// 確認鎖的必須是預留而非庫存異動，且鎖量等於需求量。
        /// 這條是上面共用斷言的前提說明——若哪天備料改成確認即移倉，
        /// 本測試會紅，提醒去重新檢視刪除守門要擋的是哪一種殘留。
        /// </summary>
        [Fact]
        public async Task Confirm_LocksReservation_NotStockMovement()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(Confirm_LocksReservation_NotStockMovement));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認備料");

            var f = await CaptureAsync(docNo);
            Assert.Equal(0, f.MovementLines);
            Assert.Equal(1, f.ActiveReservations);
            Assert.Equal(LineQty, f.ReservedQty);
        }

        /// <summary>
        /// 已確認的備料單作廢時，預留必須等額釋放——這是「合法的中途反悔」路徑。
        /// 作廢不刪單，所以單據還在，但鎖住的可用量必須完全還回去；
        /// 少還一筆就是永久性的可用量損失。
        /// </summary>
        [Fact]
        public async Task ConfirmedDoc_Void_ReleasesReservation_NoResidue()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(ConfirmedDoc_Void_ReleasesReservation_NoResidue));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認備料");
            Assert.Equal(1, (await CaptureAsync(docNo)).ActiveReservations);

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Void/{docNo}"), "作廢已確認的備料單");

            await AssertNoResidueAsync(docNo, "作廢後");
            await AssertDocStatusAsync(docNo, "Void");
        }

        /// <summary>
        /// 揀料完成（Completed）後仍不可刪：此時庫存已實際移入備料倉，
        /// 且預留仍在（依規格要留到領料過帳才等額退場）。刪掉會同時留下
        /// 孤兒異動與孤兒預留兩種殘留。
        /// </summary>
        [Fact]
        public async Task StagedDoc_Delete_Blocked_AndBothFootprintsUntouched()
        {
            var docNo = await SeedApprovedDraftAsync(nameof(StagedDoc_Delete_Blocked_AndBothFootprintsUntouched));
            var (client, token) = await NewAuthedClientAsync();

            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Confirm/{docNo}"), "確認備料");
            AssertAllowed(await PostActionAsync(client, token, $"{Route}/Stage/{docNo}"), "揀料移入備料倉");

            var staged = await CaptureAsync(docNo);
            Assert.True(staged.MovementLines > 0, $"前置條件不成立：揀料應產生移倉異動，實際 {staged}");
            Assert.True(staged.ActiveReservations > 0,
                $"前置條件不成立：預留應留到領料才退場，實際 {staged}");

            AssertBlocked(await PostActionAsync(client, token, $"{Route}/Delete/{docNo}"), "刪除已揀料的備料單");
            await AssertUnchangedAsync(staged, docNo, "刪除被擋後");
        }

        private async Task AssertDocStatusAsync(string docNo, string expected)
        {
            using var _ = NewScope(out var db);
            var status = await db.MaterialStagingOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.StagingId == docNo).Select(d => d.Status).FirstAsync();
            Assert.Equal(expected, status);
        }
    }
}
