using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 領樣單（SampleDoc）的刪除／修改 × 過帳殘留守門。
    /// 結構同報廢單與借出單，斷言由 <see cref="InvDocResidueTestBase"/> 提供。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class SampleDocPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-SM";

        public SampleDocPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/SampleDoc";

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "SM");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.SampleDocLines.IgnoreQueryFilters().Where(l => l.SampleDocId == docNo).ExecuteDeleteAsync();
            await db.SampleDocs.IgnoreQueryFilters().Where(d => d.SampleDocId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            db.SampleDocs.Add(new SampleDoc
            {
                SampleDocId    = docNo,
                SampleDate     = PostDate,
                TypeCode       = TypeCode,
                TypeCategory   = "Adjust",
                EntityType     = "C",
                EntityCode     = Prefix + "-ENT",
                Status         = DocStatus.Draft,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy      = "admin",
                PlantId        = Plant,
                CreatedByRoleId = RoleId,
                Lines = new List<SampleDocLine>
                {
                    new()
                    {
                        SampleDocId = docNo, LineNo = 1, ProductCode = SharedProduct,
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
            return await db.SampleDocLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.SampleDocId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.SampleDocs.IgnoreQueryFilters().AnyAsync(d => d.SampleDocId == docNo);

        protected override IDictionary<string, string> EditFormOrNull(string docNo) => new Dictionary<string, string>
        {
            ["SampleDocId"] = docNo,
            ["SampleDate"]  = PostDate.ToString("yyyy-MM-dd"),
            ["TypeCode"]    = TypeCode,
            ["EntityType"]  = "C",
            ["EntityCode"]  = Prefix + "-ENT",
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
