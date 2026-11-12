using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 報廢單（ScrapDoc）的刪除／修改 × 過帳殘留守門。
    ///
    /// 與同組其他單據的差別：報廢單的 Confirm **沒有** simplified 分支，一律直接過帳，
    /// 故 <see cref="RequiresSimplifiedMode"/> 覆寫為 false。這個差異本身就值得釘住——
    /// 若哪天有人「順手統一」把報廢單也加上 simplified 分支，完整模式下 Confirm 就不再
    /// 過帳，本測試的前置條件斷言（確認後應產生庫存異動）會立刻紅。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ScrapDocPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-IJ";

        public ScrapDocPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/ScrapDoc";

        /// <summary>報廢單一律簡化過帳，不需要開全域開關。</summary>
        protected override bool RequiresSimplifiedMode => false;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "SD");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.ScrapDocLines.IgnoreQueryFilters().Where(l => l.ScrapDocId == docNo).ExecuteDeleteAsync();
            await db.ScrapDocs.IgnoreQueryFilters().Where(d => d.ScrapDocId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            db.ScrapDocs.Add(new ScrapDoc
            {
                ScrapDocId     = docNo,
                ScrapDate      = PostDate,
                TypeCode       = TypeCode,
                TypeCategory   = "Adjust",
                Status         = DocStatus.Draft,
                ApprovalStatus = DocumentApprovalStatus.Approved,   // Confirm 要求已核准
                CreatedBy      = "admin",
                PlantId        = Plant,
                CreatedByRoleId = RoleId,   // IDataAuthEntity：不設會被資料權限過濾隱藏成 404
                Lines = new List<ScrapDocLine>
                {
                    new()
                    {
                        ScrapDocId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        Qty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom, UnitPrice = UnitCost,
                        WhCode = SharedWh, LocationCode = SharedLoc, QtyPutAway = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.ScrapDocLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.ScrapDocId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.ScrapDocs.IgnoreQueryFilters().AnyAsync(d => d.ScrapDocId == docNo);

        protected override IDictionary<string, string> EditFormOrNull(string docNo) => new Dictionary<string, string>
        {
            ["ScrapDocId"] = docNo,
            ["ScrapDate"]  = PostDate.ToString("yyyy-MM-dd"),
            ["Lines[0].LineNo"]       = "1",
            ["Lines[0].ProductCode"]  = SharedProduct,
            ["Lines[0].Qty"]          = "999",
            ["Lines[0].StockQty"]     = "999",
            ["Lines[0].UomCode"]      = SharedUom,
            ["Lines[0].StockUomCode"] = SharedUom,
            ["Lines[0].WhCode"]       = SharedWh,
            ["Lines[0].LocationCode"] = SharedLoc,
        };
    }
}
