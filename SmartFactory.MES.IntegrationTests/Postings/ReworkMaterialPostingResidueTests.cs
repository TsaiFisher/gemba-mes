using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 改修領料單（ReworkMaterialDoc）的刪除／修改 × 過帳殘留守門。
    /// 斷言由 <see cref="InvDocResidueTestBase"/> 提供。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReworkMaterialPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-RW";

        public ReworkMaterialPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/ReworkMaterial";

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "RW");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.ReworkMaterialDocLines.IgnoreQueryFilters()
                .Where(l => l.ReworkMaterialDocId == docNo).ExecuteDeleteAsync();
            await db.ReworkMaterialDocs.IgnoreQueryFilters()
                .Where(d => d.ReworkMaterialDocId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            db.ReworkMaterialDocs.Add(new ReworkMaterialDoc
            {
                ReworkMaterialDocId = docNo,
                IssueDate           = PostDate,
                TypeCode            = TypeCode,
                TypeCategory        = "Adjust",
                WorkCenterCode      = Prefix + "-WC",
                Status              = DocStatus.Draft,
                ApprovalStatus      = DocumentApprovalStatus.Approved,
                CreatedBy           = "admin",
                PlantId             = Plant,
                CreatedByRoleId     = RoleId,
                Lines = new List<ReworkMaterialDocLine>
                {
                    new()
                    {
                        ReworkMaterialDocId = docNo, LineNo = 1, ProductCode = SharedProduct,
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
            return await db.ReworkMaterialDocLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.ReworkMaterialDocId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.ReworkMaterialDocs.IgnoreQueryFilters().AnyAsync(d => d.ReworkMaterialDocId == docNo);
    }
}
