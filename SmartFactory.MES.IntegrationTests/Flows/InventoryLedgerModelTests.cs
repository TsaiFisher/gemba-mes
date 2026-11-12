using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 庫存帳的隨機操作序列測試（model-based）：入庫（進貨／銷退／調增）、出庫（報廢／退貨／出貨）、作廢（含作廢已被扣用的進貨、同一張作廢兩次）隨機交錯，
    /// 每一步都跟一個簡單的參考模型對照。
    ///
    /// <para><b>為什麼</b>：流程測試只驗得到「有人寫出來的那幾條」順序，期望值也是寫測試的人自己算的。
    /// 帳錯最常藏在沒人想到的先後順序裡——後面已經扣用了才作廢前面那張、作廢兩次、出庫剛好扣光一層……
    /// 這裡用固定種子產生幾十條序列、每條幾十步，打在真的 <see cref="IInventoryPostingService"/> 上。</para>
    ///
    /// <para><b>每一步都檢查</b>：
    /// ①每個儲位的在手量＝參考模型（Σ入−Σ出，被拒絕的操作不得留下任何變動）；
    /// ②移動平均品的單價＝依文件公式自算的均價（全廠一個）；FIFO 成本層價值＝模型自己模擬的成本層價值；
    /// ③總帳存貨科目淨額＝明細帳存貨價值（FIFO 成本層價值＋移動平均 數量×單價）——金額只要有一邊漏記就現形；
    /// ④FlowTestBase 的十項帳務不變量（傳票平衡、庫存滾動、成本層＝在手……）。</para>
    ///
    /// <para>失敗時印出種子與到失敗為止的完整操作序列，可原樣重現。調整規模：INV_MODEL_SEEDS（預設 30）、INV_MODEL_STEPS（預設 40）。</para>
    /// </summary>
    [Collection("Integration")]
    public class InventoryLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZINVMODEL";

        private const string MaMat = "FLOW-IM-MA";
        private const string FifoMat = "FLOW-IM-FIFO";
        private const string FifoCat = "FLOW-IM-CAT-FIFO";
        private string Loc2 => $"{Plant}-LOC2";
        private static readonly DateOnly D = new(2096, 6, 10);

        private readonly ITestOutputHelper _out;

        public InventoryLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            await ResetAsync((MaMat, "隨機序列－移動平均料"));
            using var scope = NewScope(out var db);
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == FifoCat))
                db.ProductCategories.Add(new ProductCategory { CategoryCode = FifoCat, CategoryName = "隨機序列（FIFO）", CostMethod = CostMethod.Fifo, IsActive = true });
            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == FifoMat))
                db.Products.Add(new Product { ProductCode = FifoMat, ProductName = "隨機序列－FIFO 料", CategoryCode = FifoCat, PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true });
            if (!await db.StorageLocations.IgnoreQueryFilters().AnyAsync(l => l.WhCode == Wh && l.LocationCode == Loc2))
                db.StorageLocations.Add(new StorageLocation { WhCode = Wh, LocationCode = Loc2, LocationName = "隨機序列儲位2" });
            await db.SaveChangesAsync();
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed record Op(string Kind, string Doc, string Product, string Loc, decimal Qty, decimal? Price,
            string TypeCode = "", string Category = "")
        {
            public override string ToString() => $"{Kind}{(TypeCode != "" ? $"({TypeCode})" : "")} {Doc} {Product}@{Loc} ×{Qty}{(Price is { } p ? $" @{p}" : "")}";
        }

        /// <summary>
        /// 只用「過帳引擎自己會開傳票」的異動類別，總帳存貨科目才能跟明細帳對得起來。
        /// 生產領料（IO）、委外（OO/OR）的分錄由製造模組的工單成本鏈開立，這一層刻意不開
        /// （InventoryPostingService.BuildJournalLines），所以不在這裡用。
        /// </summary>
        private static readonly (string Code, string Category)[] InboundTypes =
            { ("PI", "Purchase"), ("GR", "Sales"), ("IA", "Adjustment") };
        private static readonly (string Code, string Category)[] OutboundTypes =
            { ("IJ", "Scrap"), ("PF", "Purchase"), ("GS", "Sales") };

        private sealed class Layer(string doc, decimal qty, decimal cost)
        {
            public string Doc { get; } = doc;
            public decimal Original { get; } = qty;
            public decimal Remaining { get; set; } = qty;
            public decimal Cost { get; } = cost;
        }

        private sealed class Model
        {
            public readonly Dictionary<(string Product, string Loc), decimal> Qty = new();
            /// <summary>移動平均品的均價——<b>每個品項全廠一個</b>（不分儲位；系統語意，2026-10-21 隨機序列第一輪確認）。</summary>
            public readonly Dictionary<string, decimal> MaCost = new();
            /// <summary>已過帳、尚未作廢的單據（作廢時要知道它的方向、數量與凍結單價）。</summary>
            public readonly Dictionary<string, (Op Op, decimal UnitCost)> Posted = new();
            public readonly HashSet<string> Voided = new();

            /// <summary>FIFO 成本層（每儲位，依建立順序；同日過帳時系統以建立順序扣）。</summary>
            public readonly Dictionary<string, List<Layer>> Fifo = new();
            /// <summary>每張 FIFO 出庫扣了哪幾層、各扣多少（作廢時還回同一層）。</summary>
            public readonly Dictionary<string, List<(Layer Layer, decimal Take)>> Consumed = new();
            public List<Layer> LayersAt(string loc) => Fifo.TryGetValue(loc, out var l) ? l : Fifo[loc] = new();
            public decimal FifoValue => Fifo.Values.SelectMany(x => x).Sum(x => x.Remaining * x.Cost);

            public decimal QtyAt(string p, string l) => Qty.GetValueOrDefault((p, l));
            /// <summary>全廠在手（移動平均以它為分母）。</summary>
            public decimal QtyTotal(string p) => Qty.Where(k => k.Key.Product == p).Sum(k => k.Value);
        }

        [Fact]
        public async Task 隨機進出與作廢序列_每一步數量均價與總帳都與參考模型一致()
        {
            var seeds = Env("INV_MODEL_SEEDS", 30);
            var steps = Env("INV_MODEL_STEPS", 40);
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
            var model = new Model();
            var history = new List<string>();
            string? inventoryAccount = null;
            var products = new[] { MaMat, FifoMat };
            var locs = new[] { Loc, Loc2 };

            for (var step = 1; step <= steps; step++)
            {
                var p = products[rng.Next(products.Length)];
                var l = locs[rng.Next(locs.Length)];
                var avail = model.QtyAt(p, l);
                var roll = rng.NextDouble();
                Op op;
                if (roll < 0.40 || model.Posted.Count == 0)
                {
                    var t = InboundTypes[rng.Next(InboundTypes.Length)];
                    op = new Op("進貨", $"FLOW-IM-{seed}-{step}", p, l, rng.Next(1, 40), rng.Next(1, 20) * 5m, t.Code, t.Category);
                }
                else if (roll < 0.70)
                {
                    // 偶爾故意出超過可用量：必須被拒絕，而且不留任何變動
                    var t = OutboundTypes[rng.Next(OutboundTypes.Length)];
                    op = new Op("領料", $"FLOW-IM-{seed}-{step}", p, l,
                        rng.NextDouble() < 0.15 ? avail + rng.Next(1, 10) : Math.Max(1, Math.Floor(avail * (decimal)rng.NextDouble())), null,
                        t.Code, t.Category);
                }
                else if (roll < 0.93)
                {
                    var doc = model.Posted.Keys.ElementAt(rng.Next(model.Posted.Count));
                    var o = model.Posted[doc].Op;
                    op = new Op("作廢", doc, o.Product, o.Loc, o.Qty, null);
                }
                else
                {
                    if (model.Voided.Count == 0) continue;
                    var doc = model.Voided.ElementAt(rng.Next(model.Voided.Count));
                    op = new Op("再作廢", doc, "", "", 0, null);
                }

                var (accepted, rejectReason) = await ApplyAsync(op);
                var expect = Expect(model, op);
                history.Add($"{step,2}. {op} → {(accepted ? "接受" : $"拒絕：{rejectReason}")}");
                var key = $"{op.Kind}{(accepted ? "" : "(拒絕)")}";
                stats[key] = stats.GetValueOrDefault(key) + 1;

                string Fail(string why) =>
                    $"種子 {seed} 第 {step} 步：{why}\n序列：\n  " + string.Join("\n  ", history);

                // 該不該被接受：與參考模型的判斷一致
                Assert.True(accepted == expect.ShouldAccept,
                    Fail(expect.ShouldAccept ? $"應該接受卻被拒絕（{expect.Why}）" : $"應該拒絕卻被接受（{expect.Why}）"));
                if (accepted) Commit(model, op, expect);

                // ① 數量
                using (var scope = NewScope(out var db))
                {
                    var rows = await db.Inventories.IgnoreQueryFilters().AsNoTracking()
                        .Where(i => i.PlantId == Plant).ToListAsync();
                    foreach (var pp in products)
                        foreach (var ll in locs)
                        {
                            var actual = rows.Where(r => r.ProductCode == pp && r.LocationCode == ll).Sum(r => r.QtyOnHand);
                            Assert.True(actual == model.QtyAt(pp, ll), Fail($"{pp}@{ll} 在手 {actual}，模型 {model.QtyAt(pp, ll)}"));
                        }

                    // ② 移動平均單價（容許 6 位小數的捨入）
                    foreach (var ll in locs)
                    {
                        var row = rows.FirstOrDefault(r => r.ProductCode == MaMat && r.LocationCode == ll);
                        if (row == null || row.QtyOnHand == 0) continue;
                        var exp = model.MaCost.GetValueOrDefault(MaMat);
                        Assert.True(Math.Abs(row.UnitCost - exp) <= 0.0001m, Fail($"{MaMat}@{ll} 均價 {row.UnitCost}，模型 {exp}"));
                    }

                    // ③ 總帳存貨科目＝明細帳存貨價值
                    inventoryAccount ??= await InventoryAccountAsync(db);
                    if (inventoryAccount != null)
                    {
                        var gl = (await GlNetByAccountAsync(db, Plant)).GetValueOrDefault(inventoryAccount);
                        var fifoValue = await db.InventoryCostLayers.IgnoreQueryFilters().Where(x => x.PlantId == Plant)
                            .SumAsync(x => (decimal?)(x.QtyRemaining * x.UnitCost)) ?? 0m;
                        var maValue = rows.Where(r => r.ProductCode == MaMat).Sum(r => r.QtyOnHand * r.UnitCost);
                        Assert.True(fifoValue == model.FifoValue, Fail($"FIFO 成本層價值 {fifoValue}，模型 {model.FifoValue}"));
                        Assert.True(Math.Abs(gl - (fifoValue + maValue)) <= 0.05m,
                            Fail($"總帳存貨科目 {inventoryAccount} 淨額 {gl} ≠ 明細帳價值 {fifoValue + maValue}（FIFO {fifoValue}＋均價 {maValue}）"));
                    }
                }

                // ④ 十項帳務不變量
                try { await AssertLedgerInvariantsAsync(); }
                catch (Xunit.Sdk.XunitException ex) { Assert.Fail(Fail(ex.Message)); }
            }
        }

        private sealed record Expectation(bool ShouldAccept, string Why, decimal NewMaCost = 0);

        /// <summary>參考模型對這一步的判斷：該不該接受、接受後移動平均單價應為多少。</summary>
        private Expectation Expect(Model m, Op op)
        {
            switch (op.Kind)
            {
                case "進貨":
                {
                    var q = m.QtyTotal(op.Product);
                    var c = m.MaCost.GetValueOrDefault(op.Product);
                    var newCost = (q * c + op.Qty * op.Price!.Value) / (q + op.Qty);
                    return new(true, "進貨一律接受", newCost);
                }
                case "領料":
                    return op.Qty <= m.QtyAt(op.Product, op.Loc)
                        ? new(true, "可用量足夠", m.MaCost.GetValueOrDefault(op.Product))
                        : new(false, $"可用量 {m.QtyAt(op.Product, op.Loc)} < {op.Qty}，嚴禁負庫存");
                case "作廢":
                {
                    var (orig, unitCost) = m.Posted[op.Doc];
                    var atLoc = m.QtyAt(orig.Product, orig.Loc);
                    var q = m.QtyTotal(orig.Product);
                    var c = m.MaCost.GetValueOrDefault(orig.Product);
                    if (orig.Kind == "進貨")
                    {
                        if (atLoc < orig.Qty) return new(false, $"作廢進貨要收回 {orig.Qty}，儲位只剩 {atLoc}");
                        // FIFO：自己那層已被後續出庫扣用就擋下（2026-10-21 裁決；否則總帳與成本層金額對不上）
                        if (orig.Product == FifoMat && m.LayersAt(orig.Loc).First(x => x.Doc == op.Doc) is { } own && own.Remaining < own.Original)
                            return new(false, $"自己那層只剩 {own.Remaining}/{own.Original}，已被後續出庫扣用");
                        // 反向均價：(q×c − 收回量×原單價) ÷ (q − 收回量)；收光時歸零
                        var nq = q - orig.Qty;
                        // 剩下的價值不夠收回這筆：均價會變負數，系統擋下（ReverseMovingAverageCostAsync 的保護）
                        if (orig.Product == MaMat && q * c - orig.Qty * unitCost < 0)
                            return new(false, $"收回後均價為負（剩餘價值 {q * c} < 收回 {orig.Qty * unitCost}）");
                        // 剛好收光時兩邊價值必須相等，否則總帳會殘留數量 0 的餘額（2026-10-21 起擋下）
                        if (orig.Product == MaMat && nq == 0 && Math.Abs(q * c - orig.Qty * unitCost) > 0.01m)
                            return new(false, $"收光時剩餘價值 {q * c} ≠ 收回 {orig.Qty * unitCost}");
                        return new(true, "儲位存量足以收回", nq == 0 ? 0 : (q * c - orig.Qty * unitCost) / nq);
                    }
                    // 作廢領料：以原凍結單價把量還回去
                    return new(true, "作廢領料＝還回去", (q * c + orig.Qty * unitCost) / (q + orig.Qty));
                }
                default:   // 再作廢：已全部沖銷過的單據再作廢一次＝什麼都不做（與 VoidByMovementsAsync 同原則）
                    return new(true, "重複作廢是 no-op，不得再改動數量、均價或傳票", m.MaCost.GetValueOrDefault(MaMat));
            }
        }

        private void Commit(Model m, Op op, Expectation e)
        {
            switch (op.Kind)
            {
                case "進貨":
                    m.Qty[(op.Product, op.Loc)] = m.QtyAt(op.Product, op.Loc) + op.Qty;
                    if (op.Product == MaMat) m.MaCost[op.Product] = e.NewMaCost;
                    m.Posted[op.Doc] = (op, op.Price!.Value);
                    if (op.Product == FifoMat) m.LayersAt(op.Loc).Add(new Layer(op.Doc, op.Qty, op.Price!.Value));
                    break;
                case "領料":
                    m.Qty[(op.Product, op.Loc)] = m.QtyAt(op.Product, op.Loc) - op.Qty;
                    // 領料的凍結單價：移動平均＝當下均價；FIFO 不追（作廢時看系統的成本層）
                    m.Posted[op.Doc] = (op, m.MaCost.GetValueOrDefault(op.Product));
                    if (op.Product == FifoMat)
                    {
                        var need = op.Qty;
                        var takes = new List<(Layer, decimal)>();
                        foreach (var layer in m.LayersAt(op.Loc))
                        {
                            if (need <= 0) break;
                            var take = Math.Min(layer.Remaining, need);
                            if (take <= 0) continue;
                            layer.Remaining -= take; need -= take; takes.Add((layer, take));
                        }
                        m.Consumed[op.Doc] = takes;
                    }
                    break;
                case "作廢":
                {
                    var (orig, _) = m.Posted[op.Doc];
                    var sign = orig.Kind == "進貨" ? -1 : 1;
                    m.Qty[(orig.Product, orig.Loc)] = m.QtyAt(orig.Product, orig.Loc) + sign * orig.Qty;
                    if (orig.Product == MaMat) m.MaCost[orig.Product] = e.NewMaCost;
                    if (orig.Product == FifoMat)
                    {
                        if (orig.Kind == "進貨") m.LayersAt(orig.Loc).RemoveAll(x => x.Doc == op.Doc);
                        else foreach (var (layer, take) in m.Consumed[op.Doc]) layer.Remaining += take;
                    }
                    m.Posted.Remove(op.Doc);
                    m.Voided.Add(op.Doc);
                    break;
                }
            }
        }

        // ── 打在真服務上 ───────────────────────────────────────────────────

        /// <summary>執行一步；服務以 InvalidOperationException 拒絕時回 false（同一個 scope 的變更全部丟掉）。</summary>
        private async Task<(bool Accepted, string? Reason)> ApplyAsync(Op op)
        {
            using var scope = NewScope(out var db);
            var svc = Svc<IInventoryPostingService>(scope);
            try
            {
                switch (op.Kind)
                {
                    case "進貨":
                    case "領料":
                        await svc.PostLineDetailAsync(new PostLineDetailRequest
                        {
                            PlantId = Plant, DocNo = op.Doc, LineNo = 1, ProductCode = op.Product, LocationCode = op.Loc, LotNo = "",
                            Qty = op.Qty, TypeCode = op.TypeCode, TypeCategory = op.Category,
                            MovementDate = D, UomCode = Uom, StockUomCode = Uom, UnitPrice = op.Price,
                            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                            CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                            SetDocPosted = () => { },
                        });
                        break;
                    default:
                        await svc.VoidDocAsync(new VoidDocRequest
                        {
                            PlantId = Plant, DocNo = op.Doc, Reason = "隨機序列", CreatedBy = UserId, SetDocVoid = () => { },
                        });
                        break;
                }
                await db.SaveChangesAsync();
                return (true, null);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>存貨科目：第一張進貨傳票的借方科目（不寫死科目代碼——它由會計科目規則決定）。</summary>
        private async Task<string?> InventoryAccountAsync(SmartFactory.MES.Data.ApplicationDbContext db) =>
            await db.JournalLines.IgnoreQueryFilters()
                .Where(x => x.JournalEntry!.PlantId == Plant && x.JournalEntry.SourceDocId != null
                         && x.JournalEntry.SourceDocId.StartsWith("FLOW-IM-") && x.Debit > 0)
                .OrderBy(x => x.Id).Select(x => x.AccountCode).FirstOrDefaultAsync();
    }
}
