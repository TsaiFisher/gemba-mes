using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 內部訂單領用（NO）與退料（NR）：退料是沖銷領用，兩邊必須回到同一科目。
    /// 原本 NR 落到「進貨暫估應付」——退一次料，2152 就多一筆沒有供應商、永遠沒人來沖的貸方（2026-10-24）。
    /// </summary>
    [Collection("Integration")]
    public class InternalOrderLedgerFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZINTORD";
        private const string Mat = "FLOW-NO-MAT";
        private static readonly DateOnly D = new(2096, 8, 10);

        public InternalOrderLedgerFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAsync((Mat, "內部領用料")).GetAwaiter().GetResult();

        [Fact]
        public async Task 領用後全數退料_領用科目歸零且不碰進貨暫估應付()
        {
            await PostAsync("FLOW-NO-PI", "PI", "Purchase", 50m, 20m);
            using (var scope = NewScope(out var db))
            {
                var before = (await GlNetByAccountAsync(db, Plant)).GetValueOrDefault(StandardAccounts.GrIrAPClearingCode);
                Assert.Equal(-1000m, before);                                // 進貨貸 2152 1000
            }

            await PostAsync("FLOW-NO-01", "NO", "InternalOrder", 10m, null); // 領用 10 @20
            await PostAsync("FLOW-NR-01", "NR", "InternalOrder", 10m, null); // 全數退回

            using (var scope = NewScope(out var db))
            {
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(-1000m, gl.GetValueOrDefault(StandardAccounts.GrIrAPClearingCode));   // 退料不得再貸 2152
                Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.InternalUseExpenseCode));   // 領用與退料互沖
                Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.ScrapLossCode));            // 不再落報廢損失
                Assert.Equal(1000m, gl.GetValueOrDefault(StandardAccounts.RawMaterialInventoryCode));
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
            }
        }

        private async Task PostAsync(string doc, string type, string category, decimal qty, decimal? price)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = doc, LineNo = 1, ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = type, TypeCategory = category, MovementDate = D,
                UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted = () => { },
            });
            await db.SaveChangesAsync();
        }
    }
}
