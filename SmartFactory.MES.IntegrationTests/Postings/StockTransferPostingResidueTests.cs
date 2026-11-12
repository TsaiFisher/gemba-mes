using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 移倉單（StockTransfer）的刪除／修改 × 過帳殘留守門。
    ///
    /// 與同組其他單據的差別：過帳走 <c>PostSamePlantTransferAsync</c> 而非
    /// PostDocSimplifiedAsync，TypeCode 由服務直接寫死成 WP（出）／WI（入），
    /// **一行明細會產生兩筆庫存異動**。這正好順帶守住一件事——殘留斷言比的是
    /// 筆數而非「有沒有」，移倉若哪天只寫出不寫入（或反之），足跡比對就會紅。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class StockTransferPostingResidueTests : InvDocResidueTestBase
    {
        private const string TypeCode = Prefix + "-TR";
        private const string ToLoc    = Prefix + "-LOC2";

        public StockTransferPostingResidueTests(AuthedScheduleFactory factory) : base(factory) { }

        protected override string Route => "/StockTransfer";

        /// <summary>Confirm 無 simplified 分支，一律直接過帳。</summary>
        protected override bool RequiresSimplifiedMode => false;

        protected override async Task<string> SeedApprovedDraftAsync(string caseName)
        {
            var docNo = DocNoFor(caseName, "TR");
            using var _ = NewScope(out var db);

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == docNo).ExecuteDeleteAsync();
            await db.StockTransferLines.IgnoreQueryFilters()
                .Where(l => l.StockTransferId == docNo).ExecuteDeleteAsync();
            await db.StockTransfers.IgnoreQueryFilters()
                .Where(d => d.StockTransferId == docNo).ExecuteDeleteAsync();

            await EnsureSharedMastersAsync(db, TypeCode, direction: -1);

            // 移倉需要目標儲位；共用種子只備了一個。
            if (!await db.StorageLocations.IgnoreQueryFilters()
                    .AnyAsync(l => l.WhCode == SharedWh && l.LocationCode == ToLoc))
            {
                db.StorageLocations.Add(new StorageLocation
                {
                    WhCode = SharedWh, LocationCode = ToLoc, LocationName = "殘留測試目標儲位",
                });
                await db.SaveChangesAsync();
            }

            db.StockTransfers.Add(new StockTransfer
            {
                StockTransferId = docNo,
                TransferDate    = PostDate,
                TypeCode        = TypeCode,
                FromWhCode      = SharedWh,
                ToWhCode        = SharedWh,
                Status          = DocStatus.Draft,
                ApprovalStatus  = DocumentApprovalStatus.Approved,
                CreatedBy       = "admin",
                PlantId         = Plant,
                CreatedByRoleId = RoleId,
                Lines = new List<StockTransferLine>
                {
                    new()
                    {
                        StockTransferId = docNo, LineNo = 1, ProductCode = SharedProduct,
                        Qty = LineQty, StockQty = LineQty,
                        UomCode = SharedUom, StockUomCode = SharedUom, UnitPrice = UnitCost,
                        FromWhCode = SharedWh, FromLocationCode = SharedLoc,
                        ToWhCode   = SharedWh, ToLocationCode   = ToLoc,
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
            return await db.StockTransferLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.StockTransferId == docNo && l.LineNo == 1)
                .Select(l => l.StockQty).FirstAsync();
        }

        protected override Task<bool> DocExistsAsync(ApplicationDbContext db, string docNo)
            => db.StockTransfers.IgnoreQueryFilters().AnyAsync(d => d.StockTransferId == docNo);
    }
}
