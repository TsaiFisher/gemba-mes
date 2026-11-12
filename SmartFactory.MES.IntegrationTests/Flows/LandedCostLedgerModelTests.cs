using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Services;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 進料費用分攤（到岸成本）隨機序列（2026-10-23）：兩個料號隨機進貨、報廢，
    /// 穿插分攤單（1～3 張進貨、金額／數量／重量基準、本幣或外幣）。
    ///
    /// <para>參考模型的口徑（策略 a「只調在庫餘量」）：同一張分攤單裡同料號同儲位的各行，
    /// 在庫比例＝min(1, 在手量 ÷ 這些行的進貨量合計)；資本化＝分攤額 × 比例，其餘轉 5151；
    /// 外幣以單據日買匯換成本幣後才進存貨與總帳。</para>
    ///
    /// <para><b>每一步都檢查</b>：兩料在手量與均價＝模型；2153 暫估應付運費＝累計分攤本幣；
    /// 5151＝累計轉費用本幣；總帳存貨對明細帳（INV_GL_*）零異常。序列結束跑 FlowTestBase 帳務不變量。</para>
    ///
    /// <para>規模：LCM_MODEL_SEEDS（預設 20）、LCM_MODEL_STEPS（預設 30）。</para>
    /// </summary>
    [Collection("Integration")]
    public class LandedCostLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZLCMODEL";

        private static readonly string[] Mats = { "FLOW-LC-M1", "FLOW-LC-M2" };
        private static readonly DateOnly D = new(2096, 7, 10);
        private const string Fx = "ZZL";
        private const decimal FxRate = 31.25m;

        private readonly ITestOutputHelper _out;

        public LandedCostLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            using (var scope = NewScope(out var db))
            {
                var ids = await db.LandedCostAllocations.IgnoreQueryFilters().Where(a => a.PlantId == Plant)
                    .Select(a => a.LandedCostAllocationId).ToListAsync();
                await db.LandedCostAllocationLines.IgnoreQueryFilters().Where(l => ids.Contains(l.LandedCostAllocationId)).ExecuteDeleteAsync();
                await db.LandedCostAllocations.IgnoreQueryFilters().Where(a => a.PlantId == Plant).ExecuteDeleteAsync();
            }
            await ResetAsync((Mats[0], "分攤序列料一"), (Mats[1], "分攤序列料二"));
            using (var scope = NewScope(out var db))
            {
                if (!await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == Fx))
                    db.Currencies.Add(new SmartFactory.MES.Modules.BAS.Models.Currency { CurrencyCode = Fx, CurrencyName = "分攤序列外幣", Symbol = "L$", DecimalPlaces = 2, IsActive = true });
                if (!await db.CurrencyRates.AsNoTracking().AnyAsync(r => r.CurrencyCode == Fx && r.RateDate == D))
                    db.CurrencyRates.Add(new SmartFactory.MES.Modules.BAS.Models.CurrencyRate { CurrencyCode = Fx, RateDate = D, BuyRate = FxRate, SellRate = FxRate + 0.3m });
                await db.SaveChangesAsync();
            }
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Stock
        {
            public decimal Qty, Value;
            public decimal Avg => Qty == 0 ? 0 : Value / Qty;
        }

        private sealed record Receipt(string Doc, string Mat, decimal Qty, decimal Price);

        private sealed class Alloc
        {
            public string Id = "";
            public int Step;
            public bool Fx, Void, Invoiced;
            public decimal Total, TotalLocal, ExpLocal;
            public Dictionary<string, decimal> Cap = new();
        }

        [Fact]
        public async Task 隨機進貨報廢分攤序列_每一步均價暫估運費費用差異與總帳都與參考模型一致()
        {
            var seeds = Env("LCM_MODEL_SEEDS", 20);
            var steps = Env("LCM_MODEL_STEPS", 30);
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
            var stock = Mats.ToDictionary(m => m, _ => new Stock());
            var receipts = new List<Receipt>();
            var allocs = new List<Alloc>();
            var lastIssue = Mats.ToDictionary(m => m, _ => 0);
            decimal accrued = 0m, expensed = 0m;
            var history = new List<string>();
            string Fail(int step, string why) => $"種子 {seed} 第 {step} 步：{why}\n序列：\n  " + string.Join("\n  ", history);
            var seq = 0;

            for (var step = 1; step <= steps; step++)
            {
                var roll = rng.NextDouble();
                var kind = receipts.Count == 0 ? "進貨" : roll < 0.32 ? "進貨" : roll < 0.48 ? "報廢" : roll < 0.75 ? "分攤" : roll < 0.88 ? "作廢分攤" : "費用發票";
                var live = allocs.Where(a => !a.Void).ToList();
                if (kind == "作廢分攤" && live.Count == 0) kind = "分攤";
                if (kind == "費用發票" && !live.Any(a => !a.Invoiced && !a.Fx)) kind = "分攤";
                string label; string? mismatch = null; var accepted = true;

                switch (kind)
                {
                    case "進貨":
                    {
                        var mat = Mats[rng.Next(Mats.Length)];
                        var q = (decimal)rng.Next(10, 80);
                        var c = rng.Next(4, 40) * 5m;
                        var doc = $"FLOW-LC-{seed}-GR{++seq}";
                        label = $"進貨 {doc} {mat} ×{q} @{c}";
                        await PostAsync(doc, mat, "PI", "Purchase", q, c);
                        stock[mat].Qty += q; stock[mat].Value += q * c;
                        receipts.Add(new Receipt(doc, mat, q, c));
                        break;
                    }
                    case "報廢":
                    {
                        var mat = Mats.OrderByDescending(m => stock[m].Qty).First();
                        var q = Math.Floor(stock[mat].Qty * (decimal)(0.2 + rng.NextDouble() * 0.6));
                        label = $"報廢 {mat} ×{q}";
                        if (q <= 0) { label += "（略過）"; break; }
                        var avg = stock[mat].Avg;
                        await PostAsync($"FLOW-LC-{seed}-SC{++seq}", mat, "IJ", "Scrap", q, null);
                        stock[mat].Value -= q * avg; stock[mat].Qty -= q;
                        lastIssue[mat] = step;
                        break;
                    }
                    case "作廢分攤":
                    {
                        var a = live[rng.Next(live.Count)];
                        var expectOk = a.Cap.Where(kv => kv.Value != 0m).All(kv => lastIssue[kv.Key] < a.Step);
                        label = $"作廢分攤 {a.Id}（{(expectOk ? "確認後未出庫" : "確認後已出庫，應擋下")}）";
                        var o = await VoidAllocationAsync(a.Id);
                        if ((o == null) != expectOk)
                            mismatch = expectOk ? $"作廢被拒絕：{o}" : "確認後已出庫仍作廢成功";
                        else if (o == null)
                        {
                            foreach (var kv in a.Cap) stock[kv.Key].Value -= kv.Value;
                            accrued -= a.TotalLocal; expensed -= a.ExpLocal; a.Void = true;
                        }
                        else accepted = false;
                        break;
                    }
                    case "費用發票":
                    {
                        var cands = live.Where(x => !x.Invoiced && !x.Fx).ToList();
                        var a = cands[rng.Next(cands.Count)];
                        label = $"費用發票沖 2153：{a.Id} {a.Total}";
                        await ManualFreightInvoiceAsync(a.Total);
                        accrued -= a.Total; a.Invoiced = true;
                        break;
                    }
                    default:
                    {
                        var picked = receipts.OrderBy(_ => rng.Next()).Take(rng.Next(1, Math.Min(3, receipts.Count) + 1)).OrderBy(r => r.Doc).ToList();
                        var basis = (LandedCostBasis)rng.Next(3);
                        var fx = rng.NextDouble() < 0.4;
                        var total = rng.Next(1, 200) * 5m + (fx ? 0.37m : 0m);
                        var id = $"FLOW-LC-{seed}-A{++seq}";
                        label = $"分攤 {id} {basis} {total}{(fx ? " " + Fx : "")}：" + string.Join("、", picked.Select(r => $"{r.Doc}({r.Mat}×{r.Qty})"));

                        var rate = fx ? FxRate : 1m;
                        var shares = LandedCostAllocator.Allocate(total, picked.Select(r => new LandedCostInputLine
                        {
                            PurchaseAmount = r.Qty * r.Price, Quantity = r.Qty, Weight = r.Qty, OriginalUnitCost = r.Price,
                        }).ToList(), basis);
                        var receiptQtyByMat = picked.GroupBy(r => r.Mat).ToDictionary(g => g.Key, g => g.Sum(r => r.Qty));
                        var bump = Mats.ToDictionary(m => m, _ => 0m);
                        decimal capLocalTotal = 0m;
                        for (var i = 0; i < picked.Count; i++)
                        {
                            var r = picked[i];
                            var ratio = Math.Min(1m, stock[r.Mat].Qty / receiptQtyByMat[r.Mat]);
                            var capLocal = Math.Round(Math.Round(shares[i].AllocatedCost * ratio, 4, MidpointRounding.AwayFromZero) * rate, 4, MidpointRounding.AwayFromZero);
                            if (stock[r.Mat].Qty > 0) { bump[r.Mat] += capLocal; capLocalTotal += capLocal; }
                        }
                        var totalLocal = Math.Round(total * rate, 4, MidpointRounding.AwayFromZero);

                        var o = await AllocateAsync(id, basis, total, fx ? Fx : null, picked);
                        accepted = o == null;
                        if (!accepted) { mismatch = $"分攤被拒絕：{o}"; break; }
                        foreach (var m in Mats) stock[m].Value += bump[m];
                        accrued += totalLocal;
                        expensed += totalLocal - capLocalTotal;
                        allocs.Add(new Alloc { Id = id, Step = step, Fx = fx, Total = total, TotalLocal = totalLocal, ExpLocal = totalLocal - capLocalTotal, Cap = bump });
                        break;
                    }
                }

                stats[kind] = stats.GetValueOrDefault(kind) + (accepted ? 1 : 0);
                history.Add($"{step}. {label}");
                mismatch ??= await CheckAsync(stock, accrued, expensed);
                if (mismatch != null) Assert.Fail(Fail(step, mismatch));
            }

            await AssertLedgerInvariantsAsync();
        }

        /// <summary>
        /// 走真正的控制器：已確認單的 Details 給作廢鈕、Void 端點反向傳票＋扣回均價、作廢後 Details 不再給作廢鈕。
        /// </summary>
        [Fact]
        public async Task 控制器作廢已確認分攤單_存貨與2153回到分攤前()
        {
            await PostAsync("FLOW-LC-C-GR1", Mats[0], "PI", "Purchase", 40m, 50m);
            var o = await AllocateAsync("FLOW-LC-C-A1", LandedCostBasis.Quantity, 400m, null,
                new List<Receipt> { new("FLOW-LC-C-GR1", Mats[0], 40m, 50m) });
            Assert.Null(o);

            using (var scope = NewScope(out var db))
            {
                var ctrl = NewController(scope, db);
                var view = Assert.IsType<PartialViewResult>(await ctrl.Details("FLOW-LC-C-A1"));
                Assert.Equal(true, view.ViewData["CanVoid"]);
                var result = await ctrl.Void("FLOW-LC-C-A1", "金額輸錯");
                Assert.True(result is OkObjectResult, (result as BadRequestObjectResult)?.Value?.ToString());
            }

            using (var scope = NewScope(out var db))
            {
                var inv = await db.Inventories.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.PlantId == Plant && i.ProductCode == Mats[0]);
                Assert.Equal(50m, inv.UnitCost);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.AccruedFreightCode));
                Assert.Equal(2000m, gl.GetValueOrDefault(StandardAccounts.RawMaterialInventoryCode));

                var view = Assert.IsType<PartialViewResult>(await NewController(scope, db).Details("FLOW-LC-C-A1"));
                Assert.NotEqual(true, view.ViewData["CanVoid"]);
            }
        }

        private LandedCostAllocationController NewController(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new LandedCostAllocationController(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<ILandedCostService>(),
                sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IExportService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = sp,
                        Request = { Headers = { ["X-Requested-With"] = "XMLHttpRequest" } },
                    },
                },
            };
        }

        // ── 檢查 ─────────────────────────────────────────────────────────

        private async Task<string?> CheckAsync(Dictionary<string, Stock> stock, decimal accrued, decimal expensed)
        {
            using var scope = NewScope(out var db);
            foreach (var m in Mats)
            {
                var inv = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.PlantId == Plant && i.ProductCode == m).ToListAsync();
                var qty = inv.Sum(i => i.QtyOnHand);
                var value = inv.Sum(i => i.QtyOnHand * i.UnitCost);
                if (qty != stock[m].Qty) return $"{m} 在手量 {qty} ≠ 模型 {stock[m].Qty}";
                if (Math.Abs(value - stock[m].Value) > 0.01m + qty * 0.000001m)
                    return $"{m} 明細帳價值 {Math.Round(value, 4)} ≠ 模型 {Math.Round(stock[m].Value, 4)}（均價 {inv.FirstOrDefault()?.UnitCost} vs {Math.Round(stock[m].Avg, 6)}）";
            }

            var gl = await GlNetByAccountAsync(db, Plant);
            var acc = -gl.GetValueOrDefault(StandardAccounts.AccruedFreightCode);
            if (Math.Abs(acc - accrued) > 0.01m) return $"2153 暫估應付運費 {acc} ≠ 模型 {accrued}";
            var exp = gl.GetValueOrDefault(StandardAccounts.LandedCostVarianceCode);
            if (Math.Abs(exp - expensed) > 0.01m) return $"5151 進料費用差異 {exp} ≠ 模型 {expensed}";

            var findings = await Svc<IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant);
            return findings.Count == 0 ? null : "總帳存貨對明細帳異常：" + string.Join("；", findings.Select(f => $"{f.AuditType} {f.DocId} {f.DifferenceAmount}"));
        }

        // ── 操作 ─────────────────────────────────────────────────────────

        private async Task PostAsync(string doc, string mat, string type, string category, decimal qty, decimal? price)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = doc, LineNo = 1, ProductCode = mat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = type, TypeCategory = category, MovementDate = D,
                UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task<string?> VoidAllocationAsync(string id)
        {
            using var scope = NewScope(out var db);
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                await Svc<ILandedCostService>(scope).VoidAsync(id, D, "序列作廢");
                await tx.CommitAsync();
                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        /// <summary>運費發票到了：手動應付發票明細指定 2153，借 2153／貸 應付（本幣、免稅）。</summary>
        private async Task ManualFreightInvoiceAsync(decimal amount)
        {
            using var scope = NewScope(out var db);
            var inv = new SmartFactory.MES.Modules.FIN.Models.APInvoice
            {
                SupplierCode = Supplier, PlantId = Plant, ExchangeRate = 1m, InvoiceDate = D,
                TaxAmount = 0m, TotalAmount = amount, AmountLocal = amount,
            };
            inv.Lines.Add(new SmartFactory.MES.Modules.FIN.Models.APInvoiceLine
            {
                LineNo = 1, AccountCode = StandardAccounts.AccruedFreightCode,
                Qty = 1m, UnitPrice = amount, LineAmount = amount, TaxAmount = 0m,
            });
            await Svc<SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService>(scope)
                .CreateManualAsync(inv, DocumentApprovalStatus.Approved);
        }

        /// <summary>建核准草稿並確認；成功回 null，失敗回訊息。</summary>
        private async Task<string?> AllocateAsync(string id, LandedCostBasis basis, decimal total, string? currency, List<Receipt> picked)
        {
            using (var scope = NewScope(out var db))
            {
                var a = new LandedCostAllocation
                {
                    LandedCostAllocationId = id, DocDate = D, Basis = basis, TotalLandedCost = total, CurrencyCode = currency,
                    Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                    CreatedBy = UserId, PlantId = Plant, CreatedByRoleId = RoleId,
                };
                var n = 0;
                foreach (var r in picked)
                    a.Lines.Add(new LandedCostAllocationLine
                    {
                        LandedCostAllocationId = id, LineNo = ++n, GoodsReceiptId = r.Doc, GoodsReceiptLineNo = 1,
                        ProductCode = r.Mat, LocationCode = Loc, LotNo = "",
                        ReceiptQty = r.Qty, PurchaseAmount = r.Qty * r.Price, Weight = r.Qty, OriginalUnitCost = r.Price,
                    });
                db.LandedCostAllocations.Add(a);
                await db.SaveChangesAsync();
            }
            try
            {
                using var scope = NewScope(out var db);
                await Svc<ILandedCostService>(scope).ConfirmAsync(id);
                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }
    }
}
