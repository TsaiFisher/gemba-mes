using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 委外領料單（OutsourceMaterialDoc）的刪除／修改 × 過帳殘留守門。
    ///
    /// 這張單特別值得釘：委外發料把料移到供應商廠區，**所有權未移轉、仍是本公司存貨**，
    /// 發料本身不入總帳（見 Flows/OutsourceFlowTests）。也就是說它的過帳足跡只有庫存那一段，
    /// 若刪除守門失效，異動留下來、單據沒了，§3.3 在外原料平衡稽核就會開始報異常而查無來源。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class OutsourceMaterialPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-OO";

        public OutsourceMaterialPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/OutsourceMaterial";

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "OO");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.OutsourceMaterialDocLines.IgnoreQueryFilters()
                .Where(l => l.OutsourceMaterialDocId == docNo).ExecuteDeleteAsync();
            await db.OutsourceMaterialDocs.IgnoreQueryFilters()
                .Where(d => d.OutsourceMaterialDocId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            db.OutsourceMaterialDocs.Add(new OutsourceMaterialDoc
            {
                OutsourceMaterialDocId = docNo,
                IssueDate       = PostDate,
                TypeCode        = TypeCode,
                TypeCategory    = "Adjust",
                SupplierCode    = Prefix + "-SUP",
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Lines = new List<OutsourceMaterialDocLine>
                {
                    new()
                    {
                        OutsourceMaterialDocId = docNo, LineNo = 1, ProductCode = SharedProduct,
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
            return await db.OutsourceMaterialDocLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.OutsourceMaterialDocId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.OutsourceMaterialDocs.IgnoreQueryFilters().AnyAsync(d => d.OutsourceMaterialDocId == docNo);
    }
}
