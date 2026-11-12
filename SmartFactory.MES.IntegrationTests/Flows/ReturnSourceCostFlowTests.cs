using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 樣品退回（SR←SO）與重工發退料單的退料（RR←RI）：退回來的是當初出去的那一批，
    /// 入庫成本取來源出庫行的成本，不採單上手填的單價（2026-10-24）。
    /// 原本照手填單價：均價被污染，樣品費用／重工損失沖回的金額也與當初出庫對不上。
    /// </summary>
    [Collection("Integration")]
    public class ReturnSourceCostFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZRETSRC";
        private const string Mat = "FLOW-RS-MAT";
        private static readonly DateOnly D = new(2096, 9, 10);

        public ReturnSourceCostFlowTests(TestWebApplicationFactory factory) : base(factory)
        {
            ResetAsync((Mat, "退回成本料")).GetAwaiter().GetResult();
            using var scope = NewScope(out var db);
            db.SampleDocs.RemoveRange(db.SampleDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant));
            db.ReworkMaterialDocs.RemoveRange(db.ReworkMaterialDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant));
            db.SaveChanges();
        }

        [Theory]
        [InlineData("SO", "SR", "Sample")]
        [InlineData("RI", "RR", "Rework")]
        public async Task 出庫後均價上升_全數退回且手填單價_以原出庫成本入庫且費用科目歸零(string outType, string inType, string category)
        {
            await PostAsync("FLOW-RS-PI1", "PI", "Purchase", 10m, 10m, null);          // 10 @10
            await HeaderAsync("FLOW-RS-OUT", outType);
            await PostAsync("FLOW-RS-OUT", outType, category, 6m, null, null);         // 出 6 @10
            await PostAsync("FLOW-RS-PI2", "PI", "Purchase", 4m, 40m, null);           // 4@10＋4@40 → 均價 25
            await HeaderAsync("FLOW-RS-IN", inType);
            await PostAsync("FLOW-RS-IN", inType, category, 6m, 99m, "FLOW-RS-OUT");   // 退 6，手填 99

            using var scope = NewScope(out var db);
            var mv = await db.StockMovements.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.DocNo == "FLOW-RS-IN");
            Assert.Equal(10m, mv.UnitCost);
            var gl = await GlNetByAccountAsync(db, Plant);
            var expense = outType == "SO" ? StandardAccounts.SampleExpenseCode : StandardAccounts.ReworkLossCode;
            Assert.Equal(0m, gl.GetValueOrDefault(expense));
            Assert.Equal(260m, gl.GetValueOrDefault(StandardAccounts.RawMaterialInventoryCode));   // 100＋160
            Assert.Empty(await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
        }

        private async Task HeaderAsync(string doc, string type)
        {
            using var scope = NewScope(out var db);
            if (type is "SO" or "SR")
                db.SampleDocs.Add(new SampleDoc
                {
                    SampleDocId = doc, SampleDate = D, TypeCode = type, EntityType = "Customer", EntityCode = Customer,
                    Status = DocStatus.Posted, CreatedBy = UserId, PlantId = Plant, CreatedByRoleId = RoleId,
                });
            else
                db.ReworkMaterialDocs.Add(new ReworkMaterialDoc
                {
                    ReworkMaterialDocId = doc, IssueDate = D, TypeCode = type, WorkCenterCode = "",
                    Status = DocStatus.Posted, CreatedBy = UserId, PlantId = Plant, CreatedByRoleId = RoleId,
                });
            await db.SaveChangesAsync();
        }

        private async Task PostAsync(string doc, string type, string category, decimal qty, decimal? price, string? source)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = doc, LineNo = 1, ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = type, TypeCategory = category, MovementDate = D,
                UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                SourceDocId = source, SourceLineNo = source == null ? null : 1,
                UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted = () => { },
            });
            await db.SaveChangesAsync();
        }
    }
}
