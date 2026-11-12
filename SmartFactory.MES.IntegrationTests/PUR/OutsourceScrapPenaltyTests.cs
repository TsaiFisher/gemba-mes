using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.PUR
{
    /// <summary>
    /// 委外收貨超耗扣款「發料時點 AVCO 逐料計算」（roadmap-fin-cst-sub.md §1.5）端到端驗證。
    ///
    /// 場景（廠別 ZZOSP 專屬隔離）：工單 WO 用 2 種原料，成品收回 90（OPO 訂購 100，允收損耗 5%）。
    ///   料 M1：兩筆發料（60@50 + 40@75）→ 加權 AVCO = (60×50+40×75)/100 = 60；發料 100；BOM 用量 1/成品。
    ///          預期耗用 = 90×1 = 90 → 損耗 10% → 超耗 5% → 5 × 60 = 300。
    ///   料 M2：發料 200 @ 8；BOM 用量 2/成品。
    ///          預期耗用 = 90×2 = 180 → 損耗率 (1-180/200)=10% → 超耗 5% → 10 × 8 = 80。
    ///   合計 ExcessScrapPenalty = 380。
    ///
    /// 鑑別力：
    ///   - 若仍走舊「標準成本」路徑（StandardMaterialCost/Qty），單價與加權 AVCO 不同，結果 ≠ 380。
    ///   - 若未對 M1 兩筆發料做「數量加權」AVCO（例如取末筆 75 或簡單平均 62.5），結果亦 ≠ 380。
    ///   - 發料量取自 OO StockMovement（100/200）而非 OPO 訂購量（100 成品），M2 尤其可辨。
    /// StockMovement 快照與真實過帳僅 PG 具備，故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class OutsourceScrapPenaltyTests
    {
        private const string Plant = "ZZOSP";
        private const string Routing = "OSP-RT";
        private const string Wo = "OSP-WO";
        private const string Opo = "OSP-OPO";
        private const string OoDoc = "OSP-OO1";
        private const string Rcpt = "OSP-RCPT";
        private const string Finished = "OSP-FG";
        private const string M1 = "OSP-M1";
        private const string M2 = "OSP-M2";

        private readonly TestWebApplicationFactory _factory;

        public OutsourceScrapPenaltyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        [Fact]
        public async Task SettleAsync_UsesIssueTimeWeightedAvcoPerMaterial()
        {
            using var scope = _factory.Services.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IOutsourceReceiptSettlementService>();

            var rec = await db.OutsourceReceipts.IgnoreQueryFilters()
                .Include(r => r.Lines)
                .FirstAsync(r => r.OutsourceReceiptId == Rcpt);

            await svc.SettleAsync(rec);

            var line = rec.Lines.Single();
            Assert.Equal(380m, line.ExcessScrapPenalty);   // M1 300 + M2 80
        }

        /// <summary>
        /// 合約沒有約定損耗條款（<c>AllowedScrapRate = null</c>）時**不扣款**。
        ///
        /// 沒談過的錢不能自己扣。這個欄位過去是不可為 null 的 decimal 而上游沒有任何來源，
        /// 於是恆為 0；扣款計算靠 <c>allowedRate &gt; 0</c> 擋著，等於把「沒約定」與
        /// 「零容忍」混為一談，而超耗扣款在正式環境結構性不可達（人工驗收 W2-F14）。
        /// </summary>
        [Fact]
        public async Task SettleAsync_WithNoContractClause_ChargesNoPenalty()
        {
            using var scope = _factory.Services.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IOutsourceReceiptSettlementService>();

            var rec = await db.OutsourceReceipts.IgnoreQueryFilters()
                .Include(r => r.Lines).FirstAsync(r => r.OutsourceReceiptId == Rcpt);
            rec.Lines.Single().AllowedScrapRate = null;   // 合約未約定

            await svc.SettleAsync(rec);

            Assert.Equal(0m, rec.Lines.Single().ExcessScrapPenalty);
        }

        /// <summary>
        /// 明確約定零容忍（<c>AllowedScrapRate = 0</c>）時，短交的部分**全額**求償。
        ///
        /// 與上一支合起來才說得清楚 null ≠ 0：兩者的財務結果完全相反，
        /// 而在改成可為 null 之前，系統無法表達「約定零容忍」這件事。
        /// 本案短交 10 個成品：M1 少耗 10×1 ＝ 10 個 @60 ＝ 600；
        /// M2 少耗 10×2 ＝ 20 個 @8 ＝ 160，合計 760（＝允收 5% 時 380 的兩倍，因為允收正好吃掉一半）。
        /// </summary>
        [Fact]
        public async Task SettleAsync_WithZeroToleranceClause_ChargesFullShortfall()
        {
            using var scope = _factory.Services.CreateScope();
            var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IOutsourceReceiptSettlementService>();

            var rec = await db.OutsourceReceipts.IgnoreQueryFilters()
                .Include(r => r.Lines).FirstAsync(r => r.OutsourceReceiptId == Rcpt);
            rec.Lines.Single().AllowedScrapRate = 0m;     // 明確約定零容忍

            await svc.SettleAsync(rec);

            Assert.Equal(760m, rec.Lines.Single().ExcessScrapPenalty);
        }

        /// <summary>
        /// 委外貫穿：同一條「OPO → OO 發料 → 收貨確認」的真實資料，
        /// 既要算得出超耗扣款，也要通過 §3.3 在外原料平衡稽核——
        /// 兩者都吃 OO 發料 StockMovement + RoutingBom，口徑一旦分歧就只會有一邊是對的。
        /// 先前罰扣與稽核各自有測試，但沒有任何一支測試把它們釘在同一份資料上。
        /// </summary>
        [Fact]
        public async Task ConfirmedReceipt_SatisfiesOutsourceBalanceAudit()
        {
            using var scope = _factory.Services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IDataAuditService>();

            var rec = await db.OutsourceReceipts.IgnoreQueryFilters()
                .FirstAsync(r => r.OutsourceReceiptId == Rcpt);
            rec.Status = DocStatus.Confirmed;      // 稽核只認 Confirmed/Posted 的收貨
            await db.SaveChangesAsync();

            var failures = await audit.AuditOutsourceBalanceAsync(Plant);

            // M1：發 100、理論耗 90×1 → 在外 10；M2：發 200、理論耗 90×2 → 在外 20
            Assert.Empty(failures);
        }

        /// <summary>
        /// 收回量灌到超過發料所能支撐的程度時，稽核必須揪出負的在外餘額。
        /// 反向驗證上一支不是「稽核根本沒跑到這批資料」的假綠燈。
        /// </summary>
        [Fact]
        public async Task OverReceipt_IsCaughtByOutsourceBalanceAudit()
        {
            using var scope = _factory.Services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IDataAuditService>();

            var rec = await db.OutsourceReceipts.IgnoreQueryFilters()
                .Include(r => r.Lines)
                .FirstAsync(r => r.OutsourceReceiptId == Rcpt);
            rec.Status = DocStatus.Confirmed;
            rec.Lines.Single().StockQty = 110m;
            await db.SaveChangesAsync();

            var failures = await audit.AuditOutsourceBalanceAsync(Plant);

            // 收 110 同時吃穿兩種料：M1 100 − 110×1 = −10、M2 200 − 110×2 = −20
            Assert.Equal(2, failures.Count);
            Assert.All(failures, f => Assert.Equal("OUTSOURCE_NEG_BALANCE", f.AuditType));
            Assert.Equal(new[] { -20m, -10m },
                failures.Select(f => f.DifferenceAmount).OrderBy(d => d).ToArray());
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Purge(db);

            // 生產批（WorkOrder FK 父）
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = "OSP-PB", MrpRunId = "OSP-MRP", MrpRunLineSeq = 1,
                ProductCode = Finished, Quantity = 100m, PlantId = Plant, CreatedByRoleId = "1",
            });

            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = "OSP-PB", ProductCode = Finished,
                RoutingId = Routing, Quantity = 100m, Status = WorkOrderStatus.Completed,
                // 刻意設一個與 AVCO 不同的標準材料成本，證明計算走 AVCO 而非標準成本
                StandardMaterialCost = 9999m, PlantId = Plant,
            });

            // 途程表頭 + 工序（RoutingBom 複合 FK 父）
            db.Routings.Add(new Routing { RoutingId = Routing, ProductCode = Finished, PlantId = Plant });
            db.RoutingOperations.Add(new RoutingOperation
            {
                RoutingId = Routing, OperationSeq = 10, WorkCenterId = "OSP-WC", TM_UNIT = 1m, UomCode = "PCS",
            });

            // BOM 標準用量：M1 每成品 1、M2 每成品 2
            db.RoutingBoms.Add(new RoutingBom { RoutingId = Routing, OperationSeq = 10, BomSeq = 1, ProductCode = M1, Quantity = 1m, UomCode = "PCS" });
            db.RoutingBoms.Add(new RoutingBom { RoutingId = Routing, OperationSeq = 10, BomSeq = 2, ProductCode = M2, Quantity = 2m, UomCode = "PCS" });

            // 委外採購單 + 明細（收貨 line 以 OpoLineNo=1 對應）
            db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = Opo, SupplierCode = "OSP-SUP",
                OrderDate = new DateOnly(2099, 6, 1), Status = OutsourcePoStatus.Confirmed,
                PlantId = Plant, CreatedByRoleId = "1",
            });
            db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = Opo, LineNo = 1, SourceWorkOrderId = Wo,
                OperationSeq = 10, ProductCode = Finished, Qty = 100m, UomCode = "PCS",
            });

            // 委外發料單（OO，已過帳）+ 發料 StockMovement（出庫，含發料時點 AVCO 快照）
            db.OutsourceMaterialDocs.Add(new OutsourceMaterialDoc
            {
                OutsourceMaterialDocId = OoDoc, IssueDate = new DateOnly(2099, 6, 5),
                TypeCode = "OO", TypeCategory = "Outsource", SupplierCode = "OSP-SUP",
                WorkOrderCode = Wo, Status = DocStatus.Posted, PlantId = Plant, CreatedByRoleId = "1",
            });
            AddIssue(db, docLineNo: 1, product: M1, stockQty: 60m, unitCost: 50m);   // M1 第一筆
            AddIssue(db, docLineNo: 2, product: M1, stockQty: 40m, unitCost: 75m);   // M1 第二筆 → 加權 AVCO 60
            AddIssue(db, docLineNo: 3, product: M2, stockQty: 200m, unitCost: 8m);    // M2

            // 委外收貨單（Draft）+ 明細：收回成品 90，允收損耗 5%
            var rec = new OutsourceReceipt
            {
                OutsourceReceiptId = Rcpt, OutsourcePurchaseOrderId = Opo,
                ReceiptDate = new DateOnly(2099, 6, 20), Status = DocStatus.Draft,
                PlantId = Plant, CreatedByRoleId = "1",
                // 短交結案：訂購 100、只收回 90，且廠商宣告做完了。收貨量沒到訂購量時
                // 系統分不出「還在加工」與「已經報廢」，故短交要結算超耗必須有人勾這一欄
                // （見 IOutsourceReceiptSettlementService）。不勾的話正確答案是 0，不是 380。
                IsFinalReceipt = true,
            };
            rec.Lines.Add(new OutsourceReceiptLine
            {
                OutsourceReceiptId = Rcpt, LineNo = 1, OpoLineNo = 1,
                ProductCode = Finished, ReceiptQty = 90m, StockQty = 90m,
                UomCode = "PCS", StockUomCode = "PCS", AllowedScrapRate = 0.05m,
            });
            db.OutsourceReceipts.Add(rec);

            db.SaveChanges();
        }

        private static void AddIssue(ApplicationDbContext db, int docLineNo, string product, decimal stockQty, decimal unitCost)
        {
            db.StockMovements.Add(new StockMovement
            {
                TypeCode = "OO", TypeCategory = "Outsource",
                DocNo = OoDoc, DocLineNo = docLineNo,
                MovementDate = new DateOnly(2099, 6, 5), PlantId = Plant,
                ProductCode = product, LocationCode = "OSP-LOC", LotNo = "",
                UomCode = "PCS", StockUomCode = "PCS",
                Qty = stockQty, StockQty = stockQty, Direction = -1,   // 出庫；服務端取 Abs
                UnitCost = unitCost, TotalCost = unitCost * stockQty,
            });
        }

        private static void Purge(ApplicationDbContext db)
        {
            var recs = db.OutsourceReceipts.IgnoreQueryFilters().Include(r => r.Lines)
                .Where(r => r.PlantId == Plant).ToList();
            if (recs.Count > 0) db.OutsourceReceipts.RemoveRange(recs);

            var movements = db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == OoDoc).ToList();
            if (movements.Count > 0) db.StockMovements.RemoveRange(movements);

            var ooDocs = db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ToList();
            if (ooDocs.Count > 0) db.OutsourceMaterialDocs.RemoveRange(ooDocs);

            var opoLines = db.OutsourcePurchaseOrderLines.IgnoreQueryFilters().Where(l => l.OutsourcePurchaseOrderId == Opo).ToList();
            if (opoLines.Count > 0) db.OutsourcePurchaseOrderLines.RemoveRange(opoLines);
            var opos = db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).ToList();
            if (opos.Count > 0) db.OutsourcePurchaseOrders.RemoveRange(opos);

            // 工單須先於途程/生產批刪除（可能存在 WO→Routing / WO→ProductionBatch FK）
            var wos = db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant).ToList();
            if (wos.Count > 0) db.WorkOrders.RemoveRange(wos);

            var boms = db.RoutingBoms.IgnoreQueryFilters().Where(b => b.RoutingId == Routing).ToList();
            if (boms.Count > 0) db.RoutingBoms.RemoveRange(boms);
            var rtOps = db.RoutingOperations.IgnoreQueryFilters().Where(o => o.RoutingId == Routing).ToList();
            if (rtOps.Count > 0) db.RoutingOperations.RemoveRange(rtOps);
            var routings = db.Routings.IgnoreQueryFilters().Where(r => r.RoutingId == Routing).ToList();
            if (routings.Count > 0) db.Routings.RemoveRange(routings);

            var pbs = db.ProductionBatches.IgnoreQueryFilters().Where(p => p.PlantId == Plant).ToList();
            if (pbs.Count > 0) db.ProductionBatches.RemoveRange(pbs);

            db.SaveChanges();
        }
    }
}
