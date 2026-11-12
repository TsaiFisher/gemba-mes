using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 委外鏈的隨機操作序列測試（model-based，2026-10-22）：原料進貨、開委外工單（含委外採購）、委外發料、
    /// 委外收貨（中間批依 BOM 理論消耗帶入在外原料、結案批帶入剩下全部）隨機交錯。
    ///
    /// <para><b>為什麼</b>：委外發料不入總帳（料只是換了地點），在外原料的價值要等收貨時「帶入」成品才回到明細帳；
    /// 加工費則在收貨時借存貨／貸暫估應付。任何一段帶錯，數量照樣對、傳票照樣平衡——
    /// 穩定版 ADM 工單 19_01 就是這樣留下 −460。</para>
    ///
    /// <para><b>每一步都檢查</b>：原料與成品的在手量、均價；每張委外工單的在外原料（總帳−明細帳）＝模型；
    /// 每張委外收貨的應付發票＝加工費 × 收貨量；總帳存貨對明細帳（INV_GL_*）零異常。
    /// 結案批之後在外原料必須歸零。每條序列結束跑 FlowTestBase 帳務不變量。</para>
    ///
    /// <para>規模：OSP_MODEL_SEEDS（預設 20）、OSP_MODEL_STEPS（預設 30）。</para>
    /// </summary>
    [Collection("Integration")]
    public class OutsourceLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZOSMODEL";

        private const string Mat = "FLOW-OM-MAT";
        private const string Fg  = "FLOW-OM-FG";
        private const string Routing = "FLOW-OM-RT";
        private const decimal PerUnit = 2m;   // 1 個成品耗 2 個原料
        private static readonly DateOnly D = new(2095, 8, 10);

        private readonly ITestOutputHelper _out;

        public OutsourceLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            using (var scope = NewScope(out var db))
            {
                await db.OutsourceReceipts.IgnoreQueryFilters().Where(r => r.PlantId == Plant).ExecuteDeleteAsync();
                await db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
                var opos = await db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).Select(o => o.OutsourcePurchaseOrderId).ToListAsync();
                await db.OutsourcePurchaseOrderLines.IgnoreQueryFilters().Where(l => opos.Contains(l.OutsourcePurchaseOrderId)).ExecuteDeleteAsync();
                await db.OutsourcePurchaseOrders.IgnoreQueryFilters().Where(o => o.PlantId == Plant).ExecuteDeleteAsync();
                await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
                await db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant).ExecuteDeleteAsync();
                await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.PlantId == Plant).ExecuteDeleteAsync();
                await db.RoutingBoms.IgnoreQueryFilters().Where(b => b.RoutingId == Routing).ExecuteDeleteAsync();
                await db.RoutingOperations.IgnoreQueryFilters().Where(o => o.RoutingId == Routing).ExecuteDeleteAsync();
                await db.Routings.IgnoreQueryFilters().Where(r => r.RoutingId == Routing).ExecuteDeleteAsync();
            }
            await ResetAsync((Mat, "委外序列原料"), (Fg, "委外序列成品"));
            using (var scope = NewScope(out var db))
            {
                // 完整模式委外收回品要先品檢；免驗品直放正常倉（才會在上架當下帶入在外原料、回寫完工量）
                (await db.Products.IgnoreQueryFilters().SingleAsync(p => p.ProductCode == Fg)).IqcExempt = true;
                db.Routings.Add(new Routing { RoutingId = Routing, ProductCode = Fg, PlantId = Plant });
                db.RoutingOperations.Add(new RoutingOperation { RoutingId = Routing, OperationSeq = 10, WorkCenterId = "FLOW-OM-WC", TM_UNIT = 1m, UomCode = Uom });
                db.RoutingBoms.Add(new RoutingBom { RoutingId = Routing, OperationSeq = 10, BomSeq = 1, ProductCode = Mat, Quantity = PerUnit, UomCode = Uom });
                await db.SaveChangesAsync();
            }
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Job
        {
            public string Wo = "", Opo = "";
            public decimal Ordered, Fee, Received;
            public decimal IssuedQty, IssuedValue, Carried;
            public decimal ReturnedQty, ReturnedValue;          // 委外退料（廠商退回）
            public bool Final;
            public decimal NetQty => IssuedQty - ReturnedQty;
            public decimal NetValue => IssuedValue - ReturnedValue;
            public decimal AtVendor => NetValue - Carried;      // 在外原料（總帳裡、明細帳外）
        }

        private sealed class Model
        {
            public decimal MatQty, MatAvg, FgQty, FgAvg;
            public readonly List<Job> Jobs = new();
            public void MatIn(decimal q, decimal c) { var n = MatQty + q; MatAvg = n == 0 ? 0 : (MatQty * MatAvg + q * c) / n; MatQty = n; }
            public void FgIn(decimal q, decimal c) { var n = FgQty + q; FgAvg = n == 0 ? 0 : (FgQty * FgAvg + q * c) / n; FgQty = n; }
        }

        private sealed record Outcome(bool Accepted, string Detail);
        private bool _full;

        [Theory]
        [InlineData("true")]   // 簡化模式：收貨確認當下過帳
        [InlineData("false")]  // 完整倉儲模式（ADM）：確認後上架才過帳、帶入在外原料
        public async Task 隨機發料收貨序列_每一步在外原料成品成本應付與總帳都與參考模型一致(string simplified)
        {
            var seeds = Env("OSP_MODEL_SEEDS", 20);
            var steps = Env("OSP_MODEL_STEPS", 30);
            var stats = new Dictionary<string, int>();
            await SetSimplifiedAsync(simplified);
            _full = simplified == "false";
            try
            {
                for (var seed = 1; seed <= seeds; seed++)
                {
                    if (seed > 1) await ResetAllAsync();
                    await RunSequenceAsync(seed, steps, stats);
                }
            }
            finally { await SetSimplifiedAsync(null); }
            _out.WriteLine($"{seeds} 條序列 × {steps} 步；操作分布：{string.Join("、", stats.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}");
        }

        private async Task RunSequenceAsync(int seed, int steps, Dictionary<string, int> stats)
        {
            var rng = new Random(seed);
            var m = new Model();
            var history = new List<string>();
            string Fail(int step, string why) => $"種子 {seed} 第 {step} 步：{why}\n序列：\n  " + string.Join("\n  ", history);
            var seq = 0;

            for (var step = 1; step <= steps; step++)
            {
                var open = m.Jobs.Where(j => !j.Final).ToList();
                var roll = rng.NextDouble();
                var kind = m.MatQty == 0 ? "進料"
                    : open.Count == 0 ? (roll < 0.5 ? "開委外單" : "進料")
                    : roll < 0.15 ? "進料" : roll < 0.26 ? "開委外單" : roll < 0.52 ? "委外發料" : roll < 0.64 ? "委外退料" : "委外收貨";

                string label; Outcome o; string? mismatch = null;
                switch (kind)
                {
                    case "進料":
                    {
                        var q = (decimal)rng.Next(30, 150);
                        var c = rng.Next(4, 30) * 5m;
                        label = $"進料 ×{q} @{c}";
                        o = await PurchaseAsync($"FLOW-OM-{seed}-IN{++seq}", q, c);
                        if (!o.Accepted) mismatch = $"進料被拒絕：{o.Detail}"; else m.MatIn(q, c);
                        break;
                    }
                    case "開委外單":
                    {
                        var q = (decimal)rng.Next(5, 30);
                        var fee = rng.Next(1, 10) * 5m;
                        var j = new Job { Wo = $"FLOW-OM-{seed}-WO{m.Jobs.Count + 1}", Opo = $"FLOW-OM-{seed}-OP{m.Jobs.Count + 1}", Ordered = q, Fee = fee };
                        label = $"開委外單 {j.Wo} ×{q} 加工費 {fee}";
                        o = await CreateJobAsync(j);
                        if (!o.Accepted) mismatch = $"開單失敗：{o.Detail}"; else m.Jobs.Add(j);
                        break;
                    }
                    case "委外發料":
                    {
                        var j = open[rng.Next(open.Count)];
                        var q = rng.NextDouble() < 0.1 ? m.MatQty + rng.Next(1, 10)
                              : Math.Max(1, Math.Floor(Math.Min(m.MatQty, (j.Ordered - j.Received) * PerUnit) * (decimal)rng.NextDouble()));
                        label = $"委外發料 {j.Wo} ×{q}（在手 {m.MatQty}）";
                        var shouldAccept = q <= m.MatQty;
                        var cost = m.MatAvg;
                        o = await IssueAsync($"FLOW-OM-{seed}-OO{++seq}", j.Wo, q);
                        if (o.Accepted != shouldAccept) mismatch = shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "超過在手量卻發料成功";
                        else if (o.Accepted) { m.MatQty -= q; j.IssuedQty += q; j.IssuedValue += q * cost; }
                        break;
                    }
                    case "委外退料":
                    {
                        // 廠商手上還有的料＝淨發料 − 已收成品的理論消耗
                        var j = open[rng.Next(open.Count)];
                        var onHandAtVendor = j.NetQty - j.Received * PerUnit;
                        if (onHandAtVendor <= 0) continue;
                        var q = Math.Max(1, Math.Floor(onHandAtVendor * (decimal)rng.NextDouble()));
                        var unit = j.NetValue / j.NetQty;   // 在外原料的淨加權平均（2026-10-22 修正後的退料成本）
                        label = $"委外退料 {j.Wo} ×{q}（廠商手上 {onHandAtVendor}，退料成本 {unit:0.####}）";
                        o = await ReturnMaterialAsync($"FLOW-OM-{seed}-RT{++seq}", j.Wo, q);
                        if (!o.Accepted) mismatch = $"應接受卻被拒絕：{o.Detail}";
                        else { m.MatIn(q, unit); j.ReturnedQty += q; j.ReturnedValue += q * unit; }
                        break;
                    }
                    default:
                    {
                        var j = open[rng.Next(open.Count)];
                        var left = j.Ordered - j.Received;
                        var r = rng.NextDouble() < 0.35 ? left : Math.Max(1, Math.Floor(left * (decimal)rng.NextDouble()));
                        var final = r >= left;
                        label = $"委外收貨 {j.Wo} ×{r}{(final ? "（收足，結案批）" : "")}（在外原料 {j.AtVendor:0.####}）";
                        o = await ReceiveAsync($"FLOW-OM-{seed}-OR{++seq}", j, r);
                        if (!o.Accepted) mismatch = $"收貨被拒絕：{o.Detail}";
                        else
                        {
                            // 中間批只帶理論消耗（不得超過在外餘額）；結案批帶入剩下全部
                            var remaining = j.NetValue - j.Carried;
                            var avco = j.NetQty > 0 ? j.NetValue / j.NetQty : 0m;
                            var carried = final ? Math.Max(0m, remaining) : Math.Min(r * PerUnit * avco, Math.Max(0m, remaining));
                            j.Carried += carried; j.Received += r; j.Final = final;
                            m.FgIn(r, (r * j.Fee + carried) / r);
                            mismatch = await CheckReceiptAsync(o.Detail, r * j.Fee);
                        }
                        break;
                    }
                }

                history.Add($"{step,2}. {label} → {(o.Accepted ? "接受" : $"拒絕：{o.Detail}")}");
                var key = $"{kind}{(o.Accepted ? "" : "(拒絕)")}";
                stats[key] = stats.GetValueOrDefault(key) + 1;
                Assert.True(mismatch == null, Fail(step, mismatch ?? ""));
                var problem = await CheckAsync(m);
                Assert.True(problem == null, Fail(step, problem ?? ""));
            }

            try { await AssertLedgerInvariantsAsync(); }
            catch (Xunit.Sdk.XunitException ex) { Assert.Fail(Fail(steps, ex.Message)); }
        }

        private async Task<string?> CheckAsync(Model m)
        {
            using var scope = NewScope(out var db);
            var inv = await db.Inventories.IgnoreQueryFilters().AsNoTracking().Where(i => i.PlantId == Plant).ToListAsync();
            foreach (var (p, q, avg) in new[] { (Mat, m.MatQty, m.MatAvg), (Fg, m.FgQty, m.FgAvg) })
            {
                var dq = inv.Where(i => i.ProductCode == p).Sum(i => i.QtyOnHand);
                if (dq != q) return $"{p} 在手 {dq}，模型 {q}";
                var row = inv.FirstOrDefault(i => i.ProductCode == p && i.QtyOnHand != 0);
                if (row != null && Math.Abs(row.UnitCost - avg) > 0.0001m) return $"{p} 均價 {row.UnitCost}，模型 {avg}";
            }
            var bridge = (await new InventoryGlReconciler(db).ReconcileAsync(Plant, 0.01m, null)).SingleOrDefault();
            if (bridge != null)
                foreach (var j in m.Jobs)
                {
                    var actual = bridge.WorkOrders.FirstOrDefault(x => x.WorkOrderId == j.Wo)?.Amount ?? 0m;
                    if (Math.Abs(actual - j.AtVendor) > 0.05m) return $"委外工單 {j.Wo} 在外原料（總帳−明細帳）{actual}，模型 {j.AtVendor}";
                }
            var findings = await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant);
            return findings.Count == 0 ? null : "總帳存貨對明細帳異常：" + string.Join("；", findings.Select(f => $"{f.AuditType} {f.DocId} {f.DifferenceAmount}"));
        }

        private async Task<string?> CheckReceiptAsync(string receiptId, decimal expectedFee)
        {
            using var scope = NewScope(out var db);
            var ap = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.SourceDocType == "OutsourceReceipt" && i.SourceDocId == receiptId && i.Status != APInvoiceStatus.Void)
                .ToListAsync();
            if (ap.Count != 1) return $"委外收貨 {receiptId} 應有一張應付發票，實際 {ap.Count} 張";
            if (ap[0].TotalAmount != expectedFee) return $"委外收貨 {receiptId} 應付 {ap[0].TotalAmount}，應為加工費 {expectedFee}";
            return null;
        }

        // ── 操作 ─────────────────────────────────────────────────────────

        private async Task<Outcome> PurchaseAsync(string doc, decimal q, decimal cost, string product = Mat)
        {
            try
            {
                using var scope = NewScope(out var db);
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = doc, LineNo = 1, ProductCode = product, LocationCode = Loc, LotNo = "",
                    Qty = q, TypeCode = "PI", TypeCategory = "Purchase", MovementDate = D,
                    UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                    UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted = () => { },
                });
                await db.SaveChangesAsync();
                return new(true, "");
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }

        private async Task<Outcome> CreateJobAsync(Job j)
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = j.Wo + "-PB", MrpRunId = "FLOW-OM-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = j.Ordered, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = j.Wo, ProductionBatchId = j.Wo + "-PB", ProductCode = Fg, RoutingId = Routing,
                Quantity = j.Ordered, Status = WorkOrderStatus.Released, ApprovalStatus = DocumentApprovalStatus.Approved,
                PlantId = Plant, IsOutsource = true, OutsourceSupplierCode = Supplier,
            });
            db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = j.Opo, SupplierCode = Supplier, OrderDate = D,
                Status = OutsourcePoStatus.Confirmed, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.OutsourcePurchaseOrderLines.Add(new OutsourcePurchaseOrderLine
            {
                OutsourcePurchaseOrderId = j.Opo, LineNo = 1, SourceWorkOrderId = j.Wo,
                OperationSeq = 10, ProductCode = Fg, Qty = j.Ordered, UomCode = Uom,
            });
            await db.SaveChangesAsync();
            return new(true, "");
        }

        /// <summary>
        /// 委外發料走真的委外領退料單確認入口；完整模式確認後再經倉庫下架才過帳
        /// （2026-10-16 補的 OO 下架路由，2026-10-24 改由序列實證——原本直接呼叫過帳服務，繞過了路由）。
        /// </summary>
        private async Task<Outcome> IssueAsync(string docId, string wo, decimal q, string product = Mat)
        {
            using (var scope = NewScope(out var db))
            {
                var doc = new OutsourceMaterialDoc
                {
                    OutsourceMaterialDocId = docId, IssueDate = D, TypeCode = "OO", TypeCategory = "Outsource",
                    SupplierCode = Supplier, WorkOrderCode = wo, PlantId = Plant, CreatedByRoleId = RoleId, CreatedBy = UserId,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                };
                doc.Lines.Add(new OutsourceMaterialDocLine
                {
                    OutsourceMaterialDocId = docId, LineNo = 1, ProductCode = product, Qty = q, StockQty = q,
                    UomCode = Uom, StockUomCode = Uom, WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceMaterialDocs.Add(doc);
                await db.SaveChangesAsync();
            }
            string? failure = null;
            using (var scope = NewScope(out var db))
            {
                var r = await NewMaterialController(scope, db).Confirm(docId);
                if (r is not OkObjectResult) failure = $"{(r as ObjectResult)?.Value}";
                else if (_full)
                {
                    var pick = await NewWarehouse(scope, db).Pick(new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController.PickRequest
                    {
                        TypeCode = "OO", DocNo = docId, LineNo = 1,
                        Picks = { new() { LocationCode = Loc, LotNo = "", Qty = q } },
                    });
                    if (pick is not OkObjectResult) failure = $"發料下架：{(pick as ObjectResult)?.Value}";
                }
            }
            if (failure == null)
            {
                using var scope = NewScope(out var db);
                var status = await db.OutsourceMaterialDocs.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.OutsourceMaterialDocId == docId).Select(d => d.Status).SingleAsync();
                return status == DocStatus.Posted ? new(true, docId) : new(false, $"發料單停在 {status}，沒有過帳");
            }
            using (var scope = NewScope(out var db))
                await db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.OutsourceMaterialDocId == docId).ExecuteDeleteAsync();
            return new(false, failure);
        }

        private SmartFactory.MES.Modules.INV.Controllers.OutsourceMaterialController NewMaterialController(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new SmartFactory.MES.Modules.INV.Controllers.OutsourceMaterialController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IPlantParamService>(), sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(), sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        private async Task<Outcome> ReceiveAsync(string rcpt, Job j, decimal r)
        {
            using (var scope = NewScope(out var db))
            {
                var rec = new OutsourceReceipt
                {
                    OutsourceReceiptId = rcpt, OutsourcePurchaseOrderId = j.Opo, SupplierCode = Supplier,
                    ReceiptDate = D, CreatedBy = UserId, ExchangeRate = 1m, TypeCode = "OI", TypeCategory = "Purchase",
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = Plant, CreatedByRoleId = RoleId,
                };
                rec.Lines.Add(new OutsourceReceiptLine
                {
                    OutsourceReceiptId = rcpt, LineNo = 1, OpoLineNo = 1, ProductCode = Fg,
                    ReceiptQty = r, StockQty = r, UomCode = Uom, StockUomCode = Uom, UnitCost = j.Fee,
                    LineAmount = r * j.Fee, TotalAmount = r * j.Fee, AmountLocal = r * j.Fee, ExchangeRate = 1m,
                    WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceReceipts.Add(rec);
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewReceiptController(scope, db);
                var json = Assert.IsType<JsonResult>(await ctrl.Confirm(rcpt));
                var ok = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
                var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value)?.ToString();
                if (!Equals(ok, true))
                {
                    await db.OutsourceReceipts.IgnoreQueryFilters().Where(x => x.OutsourceReceiptId == rcpt).ExecuteDeleteAsync();
                    return new(false, msg ?? "");
                }
            }
            if (_full)
            {
                using var scope = NewScope(out var db);
                var put = await NewWarehouse(scope, db).PutAway(new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController.PutAwayRequest
                {
                    TypeCode = "OI", DocType = "OutsourceReceipt", DocNo = rcpt, LineNo = 1, WhCode = Wh, LocationCode = Loc, LotNo = "", Qty = r,
                });
                if (put is not OkObjectResult) return new(false, $"上架：{(put as ObjectResult)?.Value}");
            }
            return new(true, rcpt);
        }

        /// <summary>
        /// FIFO 料的委外退料（2026-10-22）：委外領退料單的單價欄位隱藏、新增時為空，原本入庫沒有單價
        /// 被過帳守門擋下——FIFO 料根本退不回來。現在以該工單在外原料的淨加權成本補上，成本層照發料成本建回。
        /// </summary>
        [Fact]
        public async Task FIFO料委外退料_以發料成本收回成本層()
        {
            const string fifoCat = "FLOW-OM-FIFOCAT", fifoMat = "FLOW-OM-FMAT";
            using (var scope = NewScope(out var db))
            {
                if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == fifoMat))
                    db.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
                    {
                        ProductCode = fifoMat, ProductName = "委外序列 FIFO 料", CategoryCode = fifoCat,
                        PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                    });
                if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == fifoCat))
                    db.ProductCategories.Add(new SmartFactory.MES.Modules.BAS.Models.ProductCategory
                    {
                        CategoryCode = fifoCat, CategoryName = "委外序列 FIFO", CostMethod = SmartFactory.MES.Modules.BAS.Models.CostMethod.Fifo, IsActive = true,
                    });
                await db.SaveChangesAsync();
            }
            await SetSimplifiedAsync("true");
            try
            {
                Assert.True((await PurchaseAsync("FLOW-OM-F-IN1", 10m, 40m, fifoMat)).Accepted);
                Assert.True((await PurchaseAsync("FLOW-OM-F-IN2", 10m, 70m, fifoMat)).Accepted);
                var j = new Job { Wo = "FLOW-OM-F-WO", Opo = "FLOW-OM-F-OP", Ordered = 5m, Fee = 10m };
                Assert.True((await CreateJobAsync(j)).Accepted);
                Assert.True((await IssueAsync("FLOW-OM-F-OO", j.Wo, 15m, fifoMat)).Accepted);   // 10@40 ＋ 5@70 → 淨均價 50

                var ret = await ReturnMaterialAsync("FLOW-OM-F-RT", j.Wo, 6m, fifoMat);
                Assert.True(ret.Accepted, ret.Detail);

                using var scope = NewScope(out var db);
                var mv = await db.StockMovements.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.DocNo == "FLOW-OM-F-RT");
                Assert.Equal(50m, mv.UnitCost);
                var layer = await db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.SourceDocNo == "FLOW-OM-F-RT");
                Assert.Equal(6m, layer.QtyRemaining);
                Assert.Equal(50m, layer.UnitCost);
                Assert.Empty(await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
            }
            finally { await SetSimplifiedAsync(null); }
        }

        /// <summary>委外退料走真的委外領退料單確認入口（單價欄位留空，由系統補在外原料成本）。</summary>
        private async Task<Outcome> ReturnMaterialAsync(string docId, string wo, decimal q, string product = Mat)
        {
            using (var scope = NewScope(out var db))
            {
                var doc = new OutsourceMaterialDoc
                {
                    OutsourceMaterialDocId = docId, IssueDate = D, TypeCode = "OR", TypeCategory = "Outsource",
                    SupplierCode = Supplier, WorkOrderCode = wo, PlantId = Plant, CreatedByRoleId = RoleId, CreatedBy = UserId,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                };
                doc.Lines.Add(new OutsourceMaterialDocLine
                {
                    OutsourceMaterialDocId = docId, LineNo = 1, ProductCode = product, Qty = q, StockQty = q,
                    UomCode = Uom, StockUomCode = Uom, UnitPrice = null, WhCode = Wh, LocationCode = Loc,
                });
                db.OutsourceMaterialDocs.Add(doc);
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
                var ctrl = new SmartFactory.MES.Modules.INV.Controllers.OutsourceMaterialController(
                    db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IPermissionService>(),
                    sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IApprovalService>(),
                    sp.GetRequiredService<IPlantParamService>(), sp.GetRequiredService<IDocNumberService>(),
                    sp.GetRequiredService<IInventoryPostingService>(), sp.GetRequiredService<IUomService>(),
                    sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
                };
                var r = await ctrl.Confirm(docId);
                if (r is OkObjectResult && !_full) return new(true, docId);
                if (r is OkObjectResult)
                {
                    var put = await NewWarehouse(scope, db).PutAway(new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController.PutAwayRequest
                    {
                        TypeCode = "OR", DocNo = docId, LineNo = 1, WhCode = Wh, LocationCode = Loc, LotNo = "", Qty = q,
                    });
                    if (put is OkObjectResult) return new(true, docId);
                    return new(false, $"退料上架：{(put as ObjectResult)?.Value}");
                }
                await db.OutsourceMaterialDocs.IgnoreQueryFilters().Where(d => d.OutsourceMaterialDocId == docId).ExecuteDeleteAsync();
                return new(false, $"{(r as ObjectResult)?.Value}");
            }
        }

        private SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController NewWarehouse(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IUomService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IAPInvoiceService>(), sp.GetRequiredService<IARInvoiceService>(),
                sp.GetRequiredService<IJournalEntryService>(), sp.GetRequiredService<IARCreditNoteService>(),
                sp.GetRequiredService<IAPDebitNoteService>(), sp.GetRequiredService<IInventoryReservationService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        private OutsourceReceiptController NewReceiptController(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new OutsourceReceiptController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IPlantParamService>(), sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IAPInvoiceService>(),
                sp.GetRequiredService<IExportService>(), sp.GetRequiredService<IInventoryReservationService>(),
                sp.GetRequiredService<IOutsourceReceiptSettlementService>(), sp.GetRequiredService<IJournalEntryService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        private async Task SetSimplifiedAsync(string? value)
        {
            using var scope = NewScope(out _);
            await Svc<ISystemParamService>(scope).SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, value);
        }
    }
}
