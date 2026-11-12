using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 借入物料的負債要能沖平（2026-10-22 使用者裁決）。借入（IB）以借入成本 借存貨／貸其他應付款—借入物料；
    /// 還回（BR）存貨照現值出、負債照借入成本（同對象同料號未還清借入的移動平均）沖，差額轉損益：
    /// 現值高於借入成本借 5220 存貨盤損，低於貸 4210 存貨盤盈。全數還清後負債必須歸零。
    /// </summary>
    [Collection("Integration")]
    public class LoanLedgerFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZLOAN";
        private const string Mat = "FLOW-LOAN-MAT";
        private static readonly DateOnly D = new(2095, 9, 10);

        public LoanLedgerFlowTests(TestWebApplicationFactory factory) : base(factory)
        {
            ResetAsync((Mat, "借入流程料")).GetAwaiter().GetResult();
            using var scope = NewScope(out var db);
            db.LoanDocs.RemoveRange(db.LoanDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant));
            db.SaveChanges();
        }

        [Fact]
        public async Task 借入後均價上升_全數還回_負債歸零且差額入存貨盤損()
        {
            await LoanAsync("FLOW-LOAN-IB1", "IB", "Loan", 10m, 10m);       // 借入 10 @10：負債 100
            await LoanAsync("FLOW-LOAN-PI1", "PI", "Purchase", 10m, 20m);  // 自購 10 @20：均價 15
            await LoanAsync("FLOW-LOAN-BR1", "BR", "Loan", 10m, null);      // 還回 10：存貨出 150、負債沖 100

            using var scope = NewScope(out var db);
            var gl = await GlNetByAccountAsync(db, Plant);
            Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.LoanPayableCode));
            Assert.Equal(50m, gl.GetValueOrDefault(StandardAccounts.InventoryLossCode));
            Assert.Empty(await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
        }

        [Fact]
        public async Task 兩次借入成本不同_分批還回且均價下降_負債歸零且差額入存貨盤盈()
        {
            await LoanAsync("FLOW-LOAN-IB2", "IB", "Loan", 10m, 30m);       // 借入 10 @30
            await LoanAsync("FLOW-LOAN-IB3", "IB", "Loan", 10m, 50m);       // 再借 10 @50：未還清平均 40，負債 800
            await LoanAsync("FLOW-LOAN-PI2", "PI", "Purchase", 20m, 10m);  // 自購 20 @10：均價 (300+500+200)/40 = 25
            await LoanAsync("FLOW-LOAN-BR2", "BR", "Loan", 5m, null);       // 還 5：存貨出 125、負債沖 200 → 盤盈 75
            using (var scope = NewScope(out var db))
                Assert.Equal(-600m, (await GlNetByAccountAsync(db, Plant)).GetValueOrDefault(StandardAccounts.LoanPayableCode));
            await LoanAsync("FLOW-LOAN-BR3", "BR", "Loan", 15m, null);      // 還 15：存貨出 375、負債沖 600 → 盤盈 225

            using (var scope = NewScope(out var db))
            {
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.LoanPayableCode));
                Assert.Equal(-300m, gl.GetValueOrDefault(StandardAccounts.InventoryGainCode));
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
            }
        }

        [Fact]
        public async Task 還回單作廢後再還回_負債照借入成本沖且歸零()
        {
            await LoanAsync("FLOW-LOAN-IB4", "IB", "Loan", 10m, 10m);       // 借入 10 @10：負債 100
            await LoanAsync("FLOW-LOAN-PI4", "PI", "Purchase", 10m, 20m);  // 均價 15
            await LoanAsync("FLOW-LOAN-BR4", "BR", "Loan", 5m, null);       // 還 5：負債沖 50
            using (var scope = NewScope(out var db))
                await Svc<IInventoryPostingService>(scope).VoidDocAsync(new VoidDocRequest
                {
                    PlantId = Plant, DocNo = "FLOW-LOAN-BR4", Reason = "測試作廢", CreatedBy = UserId, SetDocVoid = () => { },
                });
            await LoanAsync("FLOW-LOAN-BR5", "BR", "Loan", 10m, null);      // 作廢的那張不算：還 10 沖 100

            using var scope2 = NewScope(out var db2);
            var gl = await GlNetByAccountAsync(db2, Plant);
            Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.LoanPayableCode));
            Assert.Empty(await Svc<IDataAuditService>(scope2).AuditInventoryGlSubledgerAsync(Plant));
        }

        /// <summary>
        /// 借出（IL）／借出退回（LR）不入總帳，貨始終是我們的：全數退回後明細帳價值必須等於總帳存貨。
        /// 原本 LR 照手填單價入庫，明細帳被改值而總帳沒動，差額又被稽核的「借出在外」橋接吸收（2026-10-24）。
        /// </summary>
        [Fact]
        public async Task 借出後全數退回_手填單價不影響存貨價值_明細帳等於總帳()
        {
            await LoanAsync("FLOW-LOAN-PI6", "PI", "Purchase", 10m, 10m);  // 自購 10 @10
            await LoanAsync("FLOW-LOAN-IL6", "IL", "Loan", 6m, null);       // 借出 6 @10（不入總帳）
            await LoanAsync("FLOW-LOAN-PI7", "PI", "Purchase", 4m, 40m);   // 在手 4@10＋4@40 → 均價 25
            await LoanAsync("FLOW-LOAN-LR6", "LR", "Loan", 6m, 99m);        // 退回 6，手填 99：應以借出成本 10 入庫

            using var scope = NewScope(out var db);
            var gl = await GlNetByAccountAsync(db, Plant);
            var inv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == Plant && i.ProductCode == Mat).ToListAsync();
            Assert.Equal(14m, inv.Sum(i => i.QtyOnHand));
            Assert.Equal(gl.GetValueOrDefault(StandardAccounts.RawMaterialInventoryCode),
                Math.Round(inv.Sum(i => i.QtyOnHand * i.UnitCost), 2));               // 100＋160＝260
            var lr = await db.StockMovements.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.DocNo == "FLOW-LOAN-LR6");
            Assert.Equal(10m, lr.UnitCost);
            Assert.Empty(await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
        }

        /// <summary>借入單表頭（往來對象靠它判斷）＋過帳。進貨 PI 只過帳不建借入單。</summary>
        private async Task LoanAsync(string doc, string type, string category, decimal qty, decimal? price)
        {
            using var scope = NewScope(out var db);
            if (type is "IB" or "BR" or "IL" or "LR")
            {
                db.LoanDocs.Add(new LoanDoc
                {
                    LoanDocId = doc, LoanDate = D, TypeCode = type, TypeCategory = "Loan",
                    EntityType = "Supplier", EntityCode = Supplier, Status = DocStatus.Posted,
                    CreatedBy = UserId, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                await db.SaveChangesAsync();
            }
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
