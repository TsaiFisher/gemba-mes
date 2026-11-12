using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 借出單（LoanDoc）的刪除／修改 × 過帳殘留守門。
    /// 結構同報廢單（簡化模式過帳、QtyPutAway 由過帳回寫），斷言由
    /// <see cref="InvDocResidueTestBase"/> 提供。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class LoanDocPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-LN";

        public LoanDocPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/LoanDoc";

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "LN");
            using var _ = NewScope(out var db);

            // 重跑時先清掉上一輪產物——留著的話冪等檢查會擋住第二次確認。
            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.LoanDocLines.IgnoreQueryFilters().Where(l => l.LoanDocId == docNo).ExecuteDeleteAsync();
            await db.LoanDocs.IgnoreQueryFilters().Where(d => d.LoanDocId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            db.LoanDocs.Add(new LoanDoc
            {
                LoanDocId      = docNo,
                LoanDate       = PostDate,
                TypeCode       = TypeCode,
                TypeCategory   = "Adjust",
                EntityType     = "C",
                EntityCode     = Prefix + "-ENT",
                Status         = DocStatus.Draft,
                ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy      = "admin",
                PlantId        = Plant,
                CreatedByRoleId = RoleId,   // IDataAuthEntity：不設會被資料權限過濾隱藏成 404
                Lines = new List<LoanDocLine>
                {
                    new()
                    {
                        LoanDocId = docNo, LineNo = 1, ProductCode = SharedProduct,
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
            return await db.LoanDocLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.LoanDocId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.LoanDocs.IgnoreQueryFilters().AnyAsync(d => d.LoanDocId == docNo);

        protected override IDictionary<string, string> EditFormOrNull(string docNo) => new Dictionary<string, string>
        {
            ["LoanDocId"]  = docNo,
            ["LoanDate"]   = PostDate.ToString("yyyy-MM-dd"),
            ["TypeCode"]   = TypeCode,
            ["EntityType"] = "C",
            ["EntityCode"] = Prefix + "-ENT",
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
