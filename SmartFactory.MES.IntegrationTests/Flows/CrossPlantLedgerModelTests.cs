using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 跨廠調撥隨機序列（2026-10-23）：兩廠互撥，隨機穿插進料、報廢、建單、撥出、撤回發出、收貨、作廢，
    /// 走真正的 <see cref="CrossPlantTransferController"/>（撥出／撤回在來源廠、收貨在目的廠）。
    ///
    /// <para><b>每一步都檢查</b>：兩廠在手量＝模型；撥入單價＝撥出當下來源廠均價；兩廠 1450 合計＝在途價值
    /// （沒有在途就是 0）；兩廠總帳存貨對明細帳（INV_GL_*）零異常。序列結束跑 FlowTestBase 帳務不變量。</para>
    ///
    /// <para>規模：XPM_MODEL_SEEDS（預設 20）、XPM_MODEL_STEPS（預設 30）。</para>
    /// </summary>
    [Collection("Integration")]
    public class CrossPlantLedgerModelTests : FlowTestBase
    {
        protected override string Plant => A;
        protected override IEnumerable<string> ExtraPlants => new[] { B };

        private const string A = "ZZXMA";
        private const string B = "ZZXMB";
        private const string Mat = "FLOW-XM-MAT";
        private static readonly DateOnly D = new(2096, 6, 10);

        private readonly ITestOutputHelper _out;

        public CrossPlantLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private static string WhOf(string p) => $"{p}-WH";
        private static string LocOf(string p) => $"{p}-LOC";
        private static string Other(string p) => p == A ? B : A;

        private async Task ResetAllAsync()
        {
            using (var scope = NewScope(out var db))
                await db.CrossPlantTransfers.IgnoreQueryFilters()
                    .Where(d => d.PlantId == A || d.PlantId == B).ExecuteDeleteAsync();
            await ResetAsync((Mat, "跨廠序列料"));
            using (var scope = NewScope(out var db))
            {
                if (!await db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == B))
                    db.Plants.Add(new SmartFactory.MES.Models.Plant { Id = B, Name = $"流程測試廠 {B}" });
                db.Warehouses.Add(new Warehouse { WhCode = WhOf(B), WhName = "對面廠倉", PlantId = B });
                db.StorageLocations.Add(new StorageLocation { WhCode = WhOf(B), LocationCode = LocOf(B), LocationName = "對面廠儲位" });
                await db.SaveChangesAsync();
            }
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Stock
        {
            public decimal Qty, Avg;
            public void In(decimal q, decimal c) { var n = Qty + q; Avg = n == 0 ? 0 : (Qty * Avg + q * c) / n; Qty = n; }
            public void Out(decimal q, decimal c) { var n = Qty - q; Avg = n == 0 ? 0 : (Qty * Avg - q * c) / n; Qty = n; }
        }

        private enum St { Draft, InTransit, Posted, Void }

        private sealed class Doc
        {
            public string Id = "", From = "", To = "";
            public decimal Qty, Cost;
            public St Status;
        }

        private sealed record Outcome(bool Accepted, string Detail);

        [Fact]
        public async Task 隨機兩廠互撥序列_每一步兩廠數量在途與總帳都與參考模型一致()
        {
            var seeds = Env("XPM_MODEL_SEEDS", 20);
            var steps = Env("XPM_MODEL_STEPS", 30);
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
            var stock = new Dictionary<string, Stock> { [A] = new(), [B] = new() };
            var docs = new List<Doc>();
            var history = new List<string>();
            string Fail(int step, string why) => $"種子 {seed} 第 {step} 步：{why}\n序列：\n  " + string.Join("\n  ", history);
            var seq = 0;

            for (var step = 1; step <= steps; step++)
            {
                var drafts = docs.Where(d => d.Status == St.Draft).ToList();
                var transit = docs.Where(d => d.Status == St.InTransit).ToList();
                var posted = docs.Where(d => d.Status == St.Posted).ToList();
                var roll = rng.NextDouble();
                var kind = stock[A].Qty + stock[B].Qty == 0 ? "進料"
                    : roll < 0.15 ? "進料" : roll < 0.22 ? "報廢" : roll < 0.40 ? "建單"
                    : roll < 0.58 ? "撥出" : roll < 0.66 ? "撤回發出" : roll < 0.86 ? "收貨" : "作廢";
                if (kind == "撥出" && drafts.Count == 0) kind = "建單";
                if (kind is "撤回發出" or "收貨" && transit.Count == 0) kind = drafts.Count > 0 ? "撥出" : "建單";
                if (kind == "作廢" && posted.Count == 0) kind = transit.Count > 0 ? "收貨" : "建單";

                string label; Outcome o; string? mismatch = null;
                switch (kind)
                {
                    case "進料":
                    {
                        var p = rng.Next(2) == 0 ? A : B;
                        var q = (decimal)rng.Next(20, 120);
                        var c = rng.Next(4, 30) * 5m;
                        label = $"{p} 進料 ×{q} @{c}";
                        o = await PostAsync(p, $"FLOW-XM-{seed}-IN{++seq}", "PI", "Purchase", q, c);
                        if (!o.Accepted) mismatch = $"進料被拒絕：{o.Detail}"; else stock[p].In(q, c);
                        break;
                    }
                    case "報廢":
                    {
                        var p = stock[A].Qty >= stock[B].Qty ? A : B;
                        var q = Math.Max(1m, Math.Floor(stock[p].Qty * (decimal)rng.NextDouble() * 0.3m));
                        label = $"{p} 報廢 ×{q}";
                        if (stock[p].Qty < q) { label += "（略過：無庫存）"; o = new(true, ""); break; }
                        o = await PostAsync(p, $"FLOW-XM-{seed}-SC{++seq}", "IJ", "Scrap", q, null);
                        if (!o.Accepted) mismatch = $"報廢被拒絕：{o.Detail}"; else stock[p].Out(q, stock[p].Avg);
                        break;
                    }
                    case "建單":
                    {
                        var from = rng.Next(2) == 0 ? A : B;
                        var q = (decimal)rng.Next(1, 60);
                        var d = new Doc { Id = $"FLOW-XM-{seed}-T{docs.Count + 1}", From = from, To = Other(from), Qty = q, Status = St.Draft };
                        label = $"建調撥單 {d.Id} {from}→{d.To} ×{q}";
                        await SeedDocAsync(d);
                        docs.Add(d);
                        o = new(true, "");
                        break;
                    }
                    case "撥出":
                    {
                        var d = drafts[rng.Next(drafts.Count)];
                        var expectOk = stock[d.From].Qty >= d.Qty;
                        label = $"撥出 {d.Id} ×{d.Qty}（{d.From} 在手 {stock[d.From].Qty}）";
                        o = await ActAsync(d.From, c => c.Confirm(d.Id));
                        if (o.Accepted != expectOk)
                            mismatch = expectOk ? $"撥出被拒絕：{o.Detail}" : "庫存不足仍撥出成功";
                        else if (o.Accepted)
                        {
                            d.Cost = stock[d.From].Avg;
                            stock[d.From].Out(d.Qty, d.Cost);
                            d.Status = St.InTransit;
                            mismatch = await CheckXpCostAsync(d);
                        }
                        break;
                    }
                    case "撤回發出":
                    {
                        var d = transit[rng.Next(transit.Count)];
                        label = $"撤回發出 {d.Id}";
                        o = await ActAsync(d.From, c => c.CancelConfirm(d.Id));
                        if (!o.Accepted) mismatch = $"撤回被拒絕：{o.Detail}";
                        else { stock[d.From].In(d.Qty, d.Cost); d.Status = St.Draft; }
                        break;
                    }
                    case "收貨":
                    {
                        var d = transit[rng.Next(transit.Count)];
                        label = $"收貨 {d.Id} ×{d.Qty} @{Math.Round(d.Cost, 4)}";
                        o = await ActAsync(d.To, c => c.Receive(d.Id));
                        if (!o.Accepted) mismatch = $"收貨被拒絕：{o.Detail}";
                        else { stock[d.To].In(d.Qty, d.Cost); d.Status = St.Posted; }
                        break;
                    }
                    default: // 作廢
                    {
                        var d = posted[rng.Next(posted.Count)];
                        var expectOk = stock[d.To].Qty >= d.Qty;   // 目的廠已用掉就退不回來源廠
                        var actor = rng.Next(2) == 0 ? d.From : d.To;
                        label = $"作廢 {d.Id}（{actor} 操作；{d.To} 在手 {stock[d.To].Qty}）";
                        o = await ActAsync(actor, c => c.Void(d.Id, "序列作廢"));
                        if (!o.Accepted && expectOk && IsAveragedAwayGuard(o.Detail))
                            label += "（依設計擋下：撥入已併入均價且被後續出庫用過）";
                        else if (o.Accepted != expectOk)
                            mismatch = expectOk ? $"作廢被拒絕：{o.Detail}" : "目的廠庫存不足仍作廢成功（目的廠變負庫存）";
                        else if (o.Accepted)
                        {
                            stock[d.To].Out(d.Qty, d.Cost);
                            stock[d.From].In(d.Qty, d.Cost);
                            d.Status = St.Void;
                        }
                        break;
                    }
                }

                stats[kind] = stats.GetValueOrDefault(kind) + (o.Accepted ? 1 : 0);
                history.Add($"{step}. {label}{(o.Accepted ? "" : $" → 拒絕：{o.Detail}")}");
                mismatch ??= await CheckAsync(stock, docs);
                if (mismatch != null) Assert.Fail(Fail(step, mismatch));
            }

            await AssertLedgerInvariantsAsync();
        }

        /// <summary>
        /// 移動平均品的入庫被併入均價、之後又被出庫用過，就不能照入庫單價直接沖（會留殘值）——
        /// 進貨單與撥入共用的既有守門（Inv_VoidMovingAvgResidualFmt），要使用者先作廢後續出庫或改用調整單。
        /// </summary>
        private static bool IsAveragedAwayGuard(string message)
        {
            var head = PzText.Get("Inv_VoidMovingAvgResidualFmt", "\u0001", "", "", "", "").Split('\u0001')[0];
            return head.Length > 0 && message.StartsWith(head, StringComparison.Ordinal);
        }

        // ── 檢查 ─────────────────────────────────────────────────────────

        private async Task<string?> CheckXpCostAsync(Doc d)
        {
            using var scope = NewScope(out var db);
            // 取 XP 淨額（撥出 − 撤回沖銷）的單價：同一秒寫入的列 CreatedAt 排不出先後，不能取「最新一列」
            var xp = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.DocNo == d.Id && m.TypeCode == "XP").ToListAsync();
            var netQty = xp.Sum(m => -m.Direction * m.StockQty);
            var cost = netQty == 0 ? 0m : xp.Sum(m => -m.Direction * m.StockQty * (m.UnitCost ?? 0m)) / netQty;
            if (Math.Abs(cost - d.Cost) <= 0.01m) return null;
            var trail = await db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.PlantId == d.From && m.ProductCode == Mat).OrderBy(m => m.CreatedAt)
                .Select(m => $"{m.TypeCode} {m.DocNo} {m.Direction * m.StockQty}@{m.UnitCost}").ToListAsync();
            return $"撥出單價 {Math.Round(cost, 4)} ≠ 來源廠均價 {Math.Round(d.Cost, 4)}\n{d.From} 異動：\n    " + string.Join("\n    ", trail);
        }

        private async Task<string?> CheckAsync(Dictionary<string, Stock> stock, List<Doc> docs)
        {
            using var scope = NewScope(out var db);
            foreach (var p in new[] { A, B })
            {
                var qty = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.PlantId == p && i.ProductCode == Mat).SumAsync(i => (decimal?)i.QtyOnHand) ?? 0m;
                if (qty != stock[p].Qty) return $"{p} 在手量 {qty} ≠ 模型 {stock[p].Qty}";
            }

            var inTransit = docs.Where(d => d.Status == St.InTransit).Sum(d => Math.Round(d.Qty * d.Cost, 4, MidpointRounding.AwayFromZero));
            var clearing = 0m;
            foreach (var p in new[] { A, B })
                clearing += (await GlNetByAccountAsync(db, p)).GetValueOrDefault(StandardAccounts.InterPlantClearingCode);
            if (Math.Abs(clearing - inTransit) > 0.01m) return $"兩廠 1450 合計 {clearing} ≠ 在途價值 {inTransit}";

            var audit = Svc<IDataAuditService>(scope);
            foreach (var p in new[] { A, B })
            {
                var issues = await audit.AuditInventoryGlSubledgerAsync(p);
                if (issues.Count > 0)
                    return $"{p} 總帳存貨對明細帳異常：" + string.Join("；", issues.Select(i => $"{i.AuditType} {i.DocId} {i.DifferenceAmount}"));
            }
            return null;
        }

        // ── 操作 ─────────────────────────────────────────────────────────

        private async Task<Outcome> PostAsync(string plant, string doc, string type, string category, decimal qty, decimal? price)
        {
            try
            {
                using var scope = NewScope(out var db);
                db.CurrentPlantId = plant;
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = plant, DocNo = doc, LineNo = 1, ProductCode = Mat, LocationCode = LocOf(plant), LotNo = "",
                    Qty = qty, TypeCode = type, TypeCategory = category, MovementDate = D,
                    UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                    UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted = () => { },
                });
                await db.SaveChangesAsync();
                return new(true, "");
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }

        private async Task SeedDocAsync(Doc d)
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = d.From;   // 建單在來源廠（參照有效性檢查：來源倉要屬於本廠）
            var doc = new CrossPlantTransfer
            {
                CrossPlantTransferId = d.Id, TransferDate = D,
                PlantId = d.From, ToPlantId = d.To,
                FromWhCode = WhOf(d.From), ToWhCode = WhOf(d.To),
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                CreatedBy = UserId, CreatedByRoleId = RoleId,
            };
            doc.Lines.Add(new CrossPlantTransferLine
            {
                CrossPlantTransferId = d.Id, LineNo = 1, ProductCode = Mat,
                Qty = d.Qty, StockQty = d.Qty, UomCode = Uom, StockUomCode = Uom,
                FromWhCode = WhOf(d.From), FromLocationCode = LocOf(d.From),
                ToWhCode = WhOf(d.To), ToLocationCode = LocOf(d.To),
            });
            db.CrossPlantTransfers.Add(doc);
            await db.SaveChangesAsync();
        }

        private async Task<Outcome> ActAsync(string actingPlant, Func<CrossPlantTransferController, Task<IActionResult>> action)
        {
            using var scope = NewScope(out var db);
            db.CurrentPlantId = actingPlant;
            var sp = new TestControllerServices(scope.ServiceProvider, db, actingPlant, RoleId);
            var ctrl = new CrossPlantTransferController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
            var result = await action(ctrl);
            return result switch
            {
                OkObjectResult => new(true, ""),
                BadRequestObjectResult bad => new(false, bad.Value?.GetType().GetProperty("message")?.GetValue(bad.Value)?.ToString() ?? ""),
                _ => new(false, result.GetType().Name),
            };
        }
    }
}
