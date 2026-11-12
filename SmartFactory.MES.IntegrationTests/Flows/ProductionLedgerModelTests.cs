using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 生產鏈的隨機操作序列測試（model-based，2026-10-22）：原料進貨、開工單、發料（Push）、退料、完工入庫（後沖＋繳庫＋吸收分錄）
    /// 隨機交錯，每一步都跟參考模型對照。與 <see cref="InventoryLedgerModelTests"/> 同一套做法，換到工單成本鏈。
    ///
    /// <para><b>為什麼</b>：穩定版 ADM 的總帳存貨對明細帳差額八成以上落在在製，而「在製」只活在
    /// 「生產不開傳票、完工才吸收人工製費」這條約定裡——發料、退料、後沖、繳庫任何一腿的金額記錯，
    /// 數量照樣對、傳票照樣平衡。</para>
    ///
    /// <para><b>每一步都檢查</b>：
    /// ①各料在手量與移動平均單價＝模型（退料以該工單淨發料的加權平均成本收回，MaterialSlipService 的規則）；
    /// ②每張工單的在製金額（發料 − 退料）＝模型；
    /// ③完工後：工單結案、實際材料成本＝淨發料＋後沖、繳庫價值＝材料＋人工＋製費、成品均價＝模型；
    /// ④總帳存貨對明細帳（INV_GL_*）零異常——殘差、在製為負、結案留在製三種都不得出現；
    /// ⑤每條序列結束跑 FlowTestBase 帳務不變量。</para>
    ///
    /// <para>規模：MFG_MODEL_SEEDS（預設 20）、MFG_MODEL_STEPS（預設 30）。</para>
    /// </summary>
    [Collection("Integration")]
    public class ProductionLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGMODEL";

        private const string MatA = "FLOW-PM-A";   // Push 發料
        private const string MatB = "FLOW-PM-B";   // 完工後沖
        private const string Fg   = "FLOW-PM-FG";
        private const decimal PerUnitA = 2m, PerUnitB = 3m;
        private static readonly DateOnly D = new(2095, 7, 10);

        private readonly ITestOutputHelper _out;

        public ProductionLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            using (var scope = NewScope(out var db))
            {
                var wos = await db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant).Select(w => w.WorkOrderId).ToListAsync();
                await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
                await db.MaterialSlips.IgnoreQueryFilters().Where(s => s.PlantId == Plant).ExecuteDeleteAsync();
                await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => wos.Contains(m.WorkOrderId)).ExecuteDeleteAsync();
                await db.Dispatches.IgnoreQueryFilters().Where(d => d.PlantId == Plant).ExecuteDeleteAsync();
                await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => wos.Contains(o.WorkOrderId)).ExecuteDeleteAsync();
                await db.WorkOrders.IgnoreQueryFilters().Where(w => w.PlantId == Plant).ExecuteDeleteAsync();
                await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.PlantId == Plant).ExecuteDeleteAsync();
            }
            await ResetAsync((MatA, "生產序列原料A"), (MatB, "生產序列原料B"), (Fg, "生產序列成品"));
        }

        /// <summary>
        /// 已知缺陷、修法待裁決（預設略過；MFG_MODEL_SKIP=none 全開重現）。
        /// </summary>
        private static readonly Dictionary<string, string> KnownDefects = new()
        {
        };

        private static readonly HashSet<string> Skip =
            Environment.GetEnvironmentVariable("MFG_MODEL_SKIP") is { } env
                ? env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x != "none").ToHashSet()
                : KnownDefects.Keys.ToHashSet();

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Wo
        {
            public string Id = "";
            public decimal Qty;
            public bool Closed;
            public decimal IssuedQtyA, IssuedValueA;   // 只計發料（退料成本的分母）
            public decimal ReturnedQtyA, ReturnedValueA;
            public decimal Absorbed;                   // 完工時轉入成品的在製
            public decimal ClosedReturnJe;             // 結案後退料的補分錄（借存貨／貸製造成本差異，掛工單）
            public decimal Wip => IssuedValueA - ReturnedValueA - Absorbed + ClosedReturnJe;
        }

        private sealed class Model
        {
            public readonly Dictionary<string, decimal> Qty = new() { [MatA] = 0, [MatB] = 0, [Fg] = 0 };
            public readonly Dictionary<string, decimal> Avg = new() { [MatA] = 0, [MatB] = 0, [Fg] = 0 };
            public readonly List<Wo> Wos = new();

            public void In(string p, decimal q, decimal cost)
            {
                var nq = Qty[p] + q;
                Avg[p] = nq == 0 ? 0 : (Qty[p] * Avg[p] + q * cost) / nq;
                Qty[p] = nq;
            }
            public void Out(string p, decimal q) { Qty[p] -= q; if (Qty[p] == 0) { } }
        }

        private sealed record Outcome(bool Accepted, string Detail);

        [Fact]
        public async Task 隨機進料發料退料完工序列_每一步數量均價在製與總帳都與參考模型一致()
        {
            var seeds = Env("MFG_MODEL_SEEDS", 20);
            var steps = Env("MFG_MODEL_STEPS", 30);
            var stats = new Dictionary<string, int>();
            for (var seed = 1; seed <= seeds; seed++)
            {
                if (seed > 1) await ResetAllAsync();
                await RunSequenceAsync(seed, steps, stats);
            }
            _out.WriteLine($"{seeds} 條序列 × {steps} 步；操作分布：{string.Join("、", stats.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}");
        }

        private async Task RunSequenceAsync(int seed, int steps, Dictionary<string, int> stats)
        {
            var rng = new Random(seed);
            var m = new Model();
            var history = new List<string>();
            string Fail(int step, string why) => $"種子 {seed} 第 {step} 步：{why}\n序列：\n  " + string.Join("\n  ", history);
            var docSeq = 0;

            for (var step = 1; step <= steps; step++)
            {
                var open = m.Wos.Where(w => !w.Closed).ToList();
                var roll = rng.NextDouble();
                string kind = m.Qty[MatA] == 0 && m.Qty[MatB] == 0 ? "進料"
                    : open.Count == 0 ? (roll < 0.5 ? "開工單" : "進料")
                    : roll < 0.18 ? "進料" : roll < 0.30 ? "開工單" : roll < 0.62 ? "發料" : roll < 0.80 ? "退料" : "完工";

                string label; Outcome o; string? mismatch = null;
                switch (kind)
                {
                    case "進料":
                    {
                        var p = rng.NextDouble() < 0.5 ? MatA : MatB;
                        var q = (decimal)rng.Next(20, 120);
                        var c = rng.Next(4, 30) * 5m;
                        var doc = $"FLOW-PM-{seed}-IN{++docSeq}";
                        label = $"進料 {p} ×{q} @{c}";
                        o = await PurchaseAsync(doc, p, q, c);
                        if (!o.Accepted) mismatch = $"進料被拒絕：{o.Detail}";
                        else m.In(p, q, c);
                        break;
                    }
                    case "開工單":
                    {
                        var q = (decimal)rng.Next(5, 30);
                        var id = $"FLOW-PM-{seed}-WO{m.Wos.Count + 1}";
                        label = $"開工單 {id} ×{q}";
                        o = await CreateWoAsync(id, q);
                        if (!o.Accepted) mismatch = $"開工單失敗：{o.Detail}";
                        else m.Wos.Add(new Wo { Id = id, Qty = q });
                        break;
                    }
                    case "發料":
                    {
                        // 偶爾挑已結案的工單：結案後還發得出料，那批料就永遠留在總帳的在製裡
                        var closed = m.Wos.Where(x => x.Closed).ToList();
                        var w = closed.Count > 0 && rng.NextDouble() < 0.15 ? closed[rng.Next(closed.Count)] : open[rng.Next(open.Count)];
                        var avail = m.Qty[MatA];
                        var q = rng.NextDouble() < 0.1 ? avail + rng.Next(1, 10) : Math.Max(1, Math.Floor(Math.Min(avail, w.Qty * PerUnitA) * (decimal)rng.NextDouble()));
                        label = $"發料 {w.Id} {MatA} ×{q}（在手 {avail}）";
                        var shouldAccept = q <= avail && !w.Closed;
                        if (w.Closed) label += "（工單已結案）";
                        var cost = m.Avg[MatA];
                        o = await IssueAsync(w.Id, q);
                        if (o.Accepted != shouldAccept) mismatch = shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : (w.Closed ? "已結案工單卻還能發料" : "超過在手量的發料卻被接受");
                        else if (o.Accepted) { m.Out(MatA, q); w.IssuedQtyA += q; w.IssuedValueA += q * cost; }
                        break;
                    }
                    case "退料":
                    {
                        var closed = Skip.Contains("結案後退料") ? new List<Wo>() : m.Wos.Where(x => x.Closed && x.IssuedQtyA > x.ReturnedQtyA).ToList();
                        var w = closed.Count > 0 && rng.NextDouble() < 0.15 ? closed[rng.Next(closed.Count)] : open[rng.Next(open.Count)];
                        var net = w.IssuedQtyA - w.ReturnedQtyA;
                        if (net <= 0 && rng.NextDouble() < 0.7) continue;
                        var q = rng.NextDouble() < 0.12 ? net + 1 : Math.Max(1, Math.Floor(net * (decimal)rng.NextDouble()));
                        label = $"退料 {w.Id} {MatA} ×{q}（淨已發 {net}）";
                        // 結案後退料允許（現場清出剩料），但要補分錄讓結案工單在製維持 0（2026-10-22 裁決）
                        var shouldAccept = q <= net;
                        if (w.Closed) label += "（工單已結案）";
                        o = await ReturnAsync(w.Id, q);
                        if (o.Accepted != shouldAccept) mismatch = shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "退料超過淨已發卻被接受";
                        else if (o.Accepted)
                        {
                            // 該工單還在線上的料的加權平均（已發價值扣已退價值）——MaterialSlipService 2026-10-22 修正後的規則
                            var unit = (w.IssuedValueA - w.ReturnedValueA) / (w.IssuedQtyA - w.ReturnedQtyA);
                            m.In(MatA, q, unit); w.ReturnedQtyA += q; w.ReturnedValueA += q * unit;
                            if (w.Closed) w.ClosedReturnJe += q * unit;
                        }
                        break;
                    }
                    default: // 完工
                    {
                        var w = open[rng.Next(open.Count)];
                        var needB = w.Qty * PerUnitB;
                        var labor = rng.Next(0, 20) * 50m;
                        var oh = rng.Next(0, 20) * 50m;
                        label = $"完工 {w.Id} ×{w.Qty}（後沖 {MatB} ×{needB}，在手 {m.Qty[MatB]}；人工 {labor} 製費 {oh}；在製 {w.Wip}）";
                        var shouldAccept = needB <= m.Qty[MatB];
                        var bCost = m.Avg[MatB];
                        o = await CompleteAsync(w.Id, w.Qty, labor, oh);
                        if (o.Accepted != shouldAccept) mismatch = shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "後沖料不足卻完工成功";
                        else if (o.Accepted)
                        {
                            var material = w.Wip + needB * bCost;
                            m.Out(MatB, needB);
                            m.In(Fg, w.Qty, (material + labor + oh) / w.Qty);
                            w.Closed = true;
                            mismatch = await CheckCompletionAsync(w.Id, material, labor, oh);
                            if (mismatch == null) w.Absorbed = w.Wip;   // 在製已轉入成品
                        }
                        else await ResetWoStatusAsync(w.Id);
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
            foreach (var p in new[] { MatA, MatB, Fg })
            {
                var q = inv.Where(i => i.ProductCode == p).Sum(i => i.QtyOnHand);
                if (q != m.Qty[p]) return $"{p} 在手 {q}，模型 {m.Qty[p]}";
                var row = inv.FirstOrDefault(i => i.ProductCode == p && i.QtyOnHand != 0);
                if (row != null && Math.Abs(row.UnitCost - m.Avg[p]) > 0.0001m) return $"{p} 均價 {row.UnitCost}，模型 {m.Avg[p]}";
            }

            var bridge = (await new InventoryGlReconciler(db).ReconcileAsync(Plant, 0.01m, null)).SingleOrDefault();
            if (bridge != null)
                foreach (var w in m.Wos)
                {
                    var actual = bridge.WorkOrders.FirstOrDefault(x => x.WorkOrderId == w.Id)?.Amount ?? 0m;
                    if (Math.Abs(actual - w.Wip) > 0.01m) return $"工單 {w.Id} 在製（總帳−明細帳）{actual}，模型 {w.Wip}";
                }

            var findings = await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant);
            return findings.Count == 0 ? null : "總帳存貨對明細帳異常：" + string.Join("；", findings.Select(f => $"{f.AuditType} {f.DocId} {f.DifferenceAmount}"));
        }

        private async Task<string?> CheckCompletionAsync(string woId, decimal material, decimal labor, decimal oh)
        {
            using var scope = NewScope(out var db);
            var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkOrderId == woId);
            if (wo.Status != WorkOrderStatus.Closed) return $"工單 {woId} 完工後狀態 {wo.Status}";
            if (Math.Abs(wo.ActualMaterialCost - material) > 0.01m) return $"工單 {woId} 實際材料 {wo.ActualMaterialCost}，模型 {material}";
            var it = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.DocNo == woId && x.TypeCode == "IT").SumAsync(x => x.StockQty * (x.UnitCost ?? 0m));
            if (Math.Abs(it - (material + labor + oh)) > 0.05m) return $"工單 {woId} 繳庫價值 {it}，應為 材料 {material}＋人工 {labor}＋製費 {oh}";
            return null;
        }

        // ── 操作 ─────────────────────────────────────────────────────────

        private async Task<Outcome> PurchaseAsync(string doc, string p, decimal q, decimal cost)
        {
            try
            {
                using var scope = NewScope(out var db);
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = doc, LineNo = 1, ProductCode = p, LocationCode = Loc, LotNo = "",
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

        private async Task<Outcome> CreateWoAsync(string id, decimal qty)
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = id + "-PB", MrpRunId = "FLOW-PM-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = qty, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = id, ProductionBatchId = id + "-PB", ProductCode = Fg, RoutingId = "FLOW-PM-RT", Quantity = qty,
                Status = WorkOrderStatus.InProgress, ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = id, OperationSeq = 10, WorkCenterId = "FLOW-WC", UnitTime = 1m });
            db.WorkOrderMaterials.Add(new WorkOrderMaterial
            {
                WorkOrderId = id, OperationSeq = 10, BomSeq = 1, ProductCode = MatA, RequiredQty = PerUnitA * qty,
                UomCode = Uom, IssueMethod = MaterialIssueMethod.Push,
            });
            db.WorkOrderMaterials.Add(new WorkOrderMaterial
            {
                WorkOrderId = id, OperationSeq = 10, BomSeq = 2, ProductCode = MatB, RequiredQty = PerUnitB * qty,
                UomCode = Uom, IssueMethod = MaterialIssueMethod.Backflush, BackflushTiming = BackflushTiming.AtCompletion,
            });
            await db.SaveChangesAsync();
            return new(true, "");
        }

        private async Task<Outcome> IssueAsync(string woId, decimal q)
        {
            var slipId = $"{woId}-MI{Guid.NewGuid().ToString("N")[..6]}";
            try
            {
                using (var scope = NewScope(out var db))
                {
                    var slip = new MaterialSlip
                    {
                        SlipId = slipId, SlipNo = slipId, Category = MaterialSlipCategory.InternalIssue,
                        Status = MaterialSlipStatus.Approved, SlipDate = D, SourceDocType = "WorkOrder", SourceDocId = woId,
                        PlantId = Plant, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    };
                    slip.Lines.Add(new MaterialSlipLine { SlipId = slipId, LineNo = 1, SourceLineId = "10_1", ProductCode = MatA, RequestedQty = q, UomCode = Uom });
                    db.MaterialSlips.Add(slip);
                    await db.SaveChangesAsync();
                }
                using (var scope = NewScope(out var db))
                    await Svc<IMaterialSlipService>(scope).PickAndPostAsync(slipId, new[] { new SlipPickLine(1, Wh, Loc, "", q) });
                return new(true, slipId);
            }
            catch (InvalidOperationException ex)
            {
                using var scope = NewScope(out var db);
                await db.MaterialSlips.IgnoreQueryFilters().Where(s => s.SlipId == slipId).ExecuteDeleteAsync();
                return new(false, ex.Message);
            }
        }

        private async Task<Outcome> ReturnAsync(string woId, decimal q)
        {
            string? slipId = null;
            try
            {
                using (var scope = NewScope(out var db))
                    slipId = await Svc<IMaterialSlipService>(scope).CreateForWorkOrderAsync(
                        MaterialSlipCategory.InternalReturn, woId, new[] { new SlipRequestLine(MatA, q) }, "序列退料");
                using (var scope = NewScope(out var db))
                    await Svc<IMaterialSlipService>(scope).PickAndPostAsync(slipId, new[] { new SlipPickLine(1, Wh, Loc, "", q) });
                return new(true, slipId);
            }
            catch (InvalidOperationException ex)
            {
                if (slipId != null)
                {
                    using var scope = NewScope(out var db);
                    await db.MaterialSlips.IgnoreQueryFilters().Where(s => s.SlipId == slipId).ExecuteDeleteAsync();
                }
                return new(false, ex.Message);
            }
        }

        private async Task<Outcome> CompleteAsync(string woId, decimal qty, decimal labor, decimal oh)
        {
            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == woId);
                wo.Status = WorkOrderStatus.Completed;
                wo.ActualLaborCost = labor;
                wo.ActualOverheadCost = oh;
                if (!await db.Dispatches.IgnoreQueryFilters().AnyAsync(d => d.WorkOrderId == woId))
                    db.Dispatches.Add(new Dispatch
                    {
                        DispatchId = $"{woId}-DP", WorkOrderId = woId, OperationSeq = 10, Quantity = qty, QtyDone = qty,
                        Status = DispatchStatus.Closed, PlantId = Plant,
                    });
                await db.SaveChangesAsync();
            }
            try
            {
                using var scope = NewScope(out var db);
                await Svc<IProductionCompletionService>(scope).CompleteAsync(woId, Plant, Wh, Loc);
                return new(true, "");
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }

        /// <summary>後沖料不足而完工失敗：工單退回生產中，之後補了料可以再完工。</summary>
        private async Task ResetWoStatusAsync(string woId)
        {
            using var scope = NewScope(out var db);
            var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == woId);
            wo.Status = WorkOrderStatus.InProgress;
            await db.SaveChangesAsync();
        }
    }
}
