using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 庫存調整單（StockAdjustment）的刪除／修改 × 過帳殘留守門。
    ///
    /// 兩點與同組其他單據不同：
    ///   ① TypeCode 由 Controller 硬寫成 "IA"（不取 doc.TypeCode），故種子必須備妥該類別；
    ///   ② 方向由**明細**帶（DirectionOverride = l.Direction），盤盈盤虧同單並存，
    ///      不像其他單據吃類別的 Direction。
    /// 這張單也是全庫唯一有「RemoveRange 殘留舊行」重現測試的單據
    /// （StockAdjustmentDuplicateLineReproTests），與 R16 規則同源。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class StockAdjustmentPostingResidueTests : InvDocResidueTestBase
    {
        /// <summary>Controller 硬寫 "IA"，不能改用測試前綴。</summary>
        private const string TypeCode = "IA";

        public StockAdjustmentPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/StockAdjustment";

        /// <summary>Confirm 無 simplified 分支，一律直接過帳。</summary>
        protected override bool RequiresSimplifiedMode => false;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "IA");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.StockAdjustmentLines.IgnoreQueryFilters()
                .Where(l => l.StockAdjustmentId == docNo).ExecuteDeleteAsync();
            await db.StockAdjustments.IgnoreQueryFilters()
                .Where(d => d.StockAdjustmentId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            db.StockAdjustments.Add(new StockAdjustment
            {
                StockAdjustmentId = docNo,
                AdjustDate      = PostDate,
                TypeCode        = TypeCode,
                TypeCategory    = "Adjust",
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Lines = new List<StockAdjustmentLine>
                {
                    new()
                    {
                        StockAdjustmentId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        Qty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom, UnitPrice = UnitCost,
                        WhCode = SharedWh, LocationCode = SharedLoc,
                        Direction = -1,     // 盤虧：扣庫存
                        QtyPutAway = 0m,
                    },
                },
            });

            await db.SaveChangesAsync();
            return docNo;
        }

        protected override async Task<decimal> ReadFirstLineStockQtyAsync(string docNo)
        {
            using var _ = NewScope(out var db);
            return await db.StockAdjustmentLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.StockAdjustmentId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.StockAdjustments.IgnoreQueryFilters().AnyAsync(d => d.StockAdjustmentId == docNo);
    }
}
