using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 採購應付的隨機操作序列測試（model-based，2026-10-22）：進貨（簡化模式確認當下認列應付）、付款、
    /// 進貨退出（未付款發票直接回沖、已付款發票開進貨折讓單）、確認折讓，以及付款／發票／退出／折讓／進貨的作廢隨機交錯。
    /// 與 <see cref="ReceivableLedgerModelTests"/> 對稱。
    ///
    /// <para><b>每一步都檢查</b>：
    /// ①每張應付發票的 總額／已付／已折讓／是否作廢＝參考模型；
    /// ②總帳應付科目（貸餘）＝應付子帳（未作廢發票的 總額−已付−已折讓）；
    /// ③總帳銀行＝−已確認、未作廢付款合計；
    /// ④<b>經濟事實</b>：進貨暫估應付（GR/IR）＝已退貨但折讓還沒確認的（借餘）− 發票作廢後已收貨未請款的（貸餘）——
    ///   簡化模式收貨當下就認列應付，除此之外不該有掛帳；進項稅＝稅率 ×（進貨量−已反映的退貨量）× 單價，只算發票沒作廢的進貨。
    /// 每條序列結束再跑 FlowTestBase 的帳務不變量。</para>
    ///
    /// <para>規模：AP_MODEL_SEEDS（預設 20）、AP_MODEL_STEPS（預設 30）；AP_MODEL_SKIP 逗號分隔略過操作
    /// （另有「付款作廢發票」＝付款不挑已作廢發票、「退款」＝不做多付退款）。</para>
    /// </summary>
    [Collection("Integration")]
    public class PayableLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZAPMODEL";

        private const string Mat = "FLOW-APM-MAT";
        private const string Po  = "FLOW-APM-PO";
        private const string Cat = "FLOW-APM-CAT";
        private static readonly DateOnly D = new(2095, 6, 10);

        private const decimal Price   = 200m;
        private const decimal TaxRate = 0.05m;
        private const decimal Gross   = Price * (1m + TaxRate);   // 每件含稅 210

        private readonly ITestOutputHelper _out;

        public PayableLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "應付序列原料"));
            using var scope = NewScope(out var db);
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Cat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Cat, CategoryName = "應付序列類別", CostMethod = CostMethod.MovingAverage,
                    IsActive = true, DefaultWarehouseCode = Wh, DefaultLocationCode = Loc,
                });
            await db.SaveChangesAsync();
            var p = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == Mat);
            p.CategoryCode = Cat;
            p.IqcExempt = true;   // 免驗直放：收貨當下就進正常倉、認列應付
            await db.SaveChangesAsync();

            var po = new PurchaseOrder
            {
                PurchaseOrderId = Po, OrderDate = D, CreatedBy = UserId, SupplierCode = Supplier,
                ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            po.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat, OrderQty = 100_000m, UomCode = Uom,
                UnitPrice = Price, TaxRate = TaxRate,
            });
            db.PurchaseOrders.Add(po);
            await db.SaveChangesAsync();

            if (!await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == Fx))
                db.Currencies.Add(new SmartFactory.MES.Modules.BAS.Models.Currency { CurrencyCode = Fx, CurrencyName = "應付序列外幣", Symbol = "G$", DecimalPlaces = 2, IsActive = true });
            for (var d = D; d <= D.AddDays(200); d = d.AddDays(1))
                if (!await db.CurrencyRates.AsNoTracking().AnyAsync(r => r.CurrencyCode == Fx && r.RateDate == d))
                    db.CurrencyRates.Add(new SmartFactory.MES.Modules.BAS.Models.CurrencyRate { CurrencyCode = Fx, RateDate = d, BuyRate = BuyRate(d), SellRate = BuyRate(d) + 0.3m });
            await db.SaveChangesAsync();
            Svc<ICurrencyService>(scope).InvalidateCache();
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        /// <summary>
        /// 已知缺陷、修法待裁決時放這裡（預設略過，其餘操作照常守門）。2026-10-22 第一輪找到的 5 項已全部改成守門擋下、清空；
        /// 之後再找到新的，先列進來、修好再拿掉。AP_MODEL_SKIP=none 全開重現。
        /// </summary>
        private static readonly Dictionary<string, string> KnownDefects = new()
        {
        };

        private static readonly HashSet<string> Skip =
            Environment.GetEnvironmentVariable("AP_MODEL_SKIP") is { } env
                ? env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x != "none").ToHashSet()
                : KnownDefects.Keys.ToHashSet();

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Recv { public string Id = ""; public decimal Qty; public bool Void; public string InvId = ""; }
        private sealed class Inv { public string Id = ""; public string Gr = ""; public decimal Total, Paid, Debited; public bool Void; }
        private sealed class Ret { public string Id = ""; public string Gr = ""; public decimal Qty; public bool Void; public decimal ReducedInvoice; public string? NoteId; }
        private sealed class Note { public string Id = ""; public string InvId = ""; public decimal Gross; public bool Posted, Void; }
        private sealed class Pay { public string Id = ""; public string InvId = ""; public decimal Amt; public bool Void; }

        private sealed class Model
        {
            public readonly List<Recv> Recvs = new();
            public readonly Dictionary<string, Inv> Invs = new();
            public readonly List<Ret> Rets = new();
            public readonly Dictionary<string, Note> Notes = new();
            public readonly List<Pay> Pays = new();

            public decimal Outstanding(Inv i) => i.Total - i.Paid - i.Debited;
            public decimal Returnable(Recv r) => r.Qty - Rets.Where(x => x.Gr == r.Id && !x.Void).Sum(x => x.Qty);
            public bool Reflected(Ret r) => r.ReducedInvoice != 0m
                || (r.NoteId != null && Notes.TryGetValue(r.NoteId, out var n) && n.Posted && !n.Void);

            /// <summary>經濟事實：發票沒作廢的進貨，實際買進來的量（進貨−已反映到應付的有效退貨）。</summary>
            public decimal BoughtQty => Recvs.Where(r => !r.Void && !Invs[r.InvId].Void)
                .Sum(r => r.Qty - Rets.Where(x => x.Gr == r.Id && !x.Void && Reflected(x)).Sum(x => x.Qty));

            /// <summary>退貨已出庫、應付還沒沖的量（折讓草稿或折讓被作廢）：這段期間 GR/IR 掛著借餘。</summary>
            public decimal PendingReturnQty => Rets.Where(x => !x.Void && !Reflected(x)
                    && !Invs[Recvs.Single(r => r.Id == x.Gr).InvId].Void)
                .Sum(x => x.Qty);

            /// <summary>發票作廢、進貨還在：已收貨未請款，GR/IR 掛貸餘等新發票來沖（GRIR_AGING 逾期會提醒）。</summary>
            public decimal UninvoicedQty => Recvs.Where(r => !r.Void && Invs[r.InvId].Void).Sum(Returnable);
        }

        private sealed record Outcome(bool Accepted, string Detail);

        /// <summary>這一輪是否為完整倉儲模式（確認後要上架／揀貨）。</summary>
        private bool _full;

        // ── 外幣情境（付外幣給廠商走買匯；每步換一天）──────────────────
        private const string Fx = "ZZG";
        private bool _fx;
        private DateOnly _date = D;
        private static decimal BuyRate(DateOnly d) => 28m + (d.DayNumber % 7) * 0.4m;

        [Theory]
        [InlineData("true")]   // 簡化模式：確認當下過帳
        [InlineData("false")]  // 完整倉儲模式（ADM 與系統預設）：確認後經上架／揀貨才過帳
        [InlineData("fx")]     // 外幣（簡化模式）：每一步換一天、匯率跟著變，驗本幣帳與匯兌損益
        public async Task 隨機進貨付款退貨與作廢序列_每一步應付子帳總帳與經濟事實都一致(string simplified)
        {
            var seeds = Env("AP_MODEL_SEEDS", 20);
            var steps = Env("AP_MODEL_STEPS", 30);
            var stats = new Dictionary<string, int>();
            _fx = simplified == "fx";
            await SetSimplifiedAsync(_fx ? "true" : simplified);
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

            var kinds = new (string Kind, double Weight)[]
            {
                ("進貨", 3), ("付款", 3), ("退出", 2), ("確認折讓", 1.5),
                ("作廢付款", 1), ("作廢發票", 1), ("作廢退出", 1), ("作廢折讓", 0.7), ("作廢進貨", 0.7),
            };
            kinds = kinds.Where(k => !Skip.Contains(k.Kind)).ToArray();

            for (var step = 1; step <= steps; step++)
            {
                var kind = m.Recvs.Count == 0 ? "進貨" : Pick(rng, kinds);
                _date = _fx ? D.AddDays(step) : D;
                var status = await InvoiceStatusesAsync();
                string label;
                Func<Task<Outcome>> act;
                Func<Outcome, string?> expectAndCommit;

                switch (kind)
                {
                    case "進貨":
                    {
                        var q = (decimal)rng.Next(1, 20);
                        label = $"進貨 ×{q}";
                        act = () => ReceiveAsync(q);
                        expectAndCommit = o =>
                        {
                            if (!o.Accepted) return $"進貨被拒絕：{o.Detail}";
                            var (gr, inv) = SplitPair(o.Detail);
                            m.Recvs.Add(new Recv { Id = gr, Qty = q, InvId = inv });
                            m.Invs[inv] = new Inv { Id = inv, Gr = gr, Total = q * Gross };
                            return null;
                        };
                        break;
                    }
                    case "付款":
                    {
                        var live = m.Invs.Values.Where(i => !i.Void || !Skip.Contains("付款作廢發票")).ToList();
                        if (live.Count == 0) continue;
                        var inv = live[rng.Next(live.Count)];
                        var outstanding = m.Outstanding(inv);
                        if (outstanding < 0m && Skip.Contains("退款")) continue;
                        decimal amt;
                        if (outstanding < 0m) amt = rng.NextDouble() < 0.8 ? outstanding : outstanding - 1m;   // 多付的退回
                        else if (outstanding == 0m) amt = 10m;
                        else amt = rng.NextDouble() < 0.12 ? outstanding + 1m
                                 : rng.NextDouble() < 0.5 ? outstanding : Math.Max(1m, Math.Floor(outstanding * (decimal)rng.NextDouble()));
                        label = $"付款 {inv.Id} {amt}（未結 {outstanding}{(inv.Void ? "，發票已作廢" : "")}）";
                        var shouldAccept = !inv.Void && amt != 0m
                            && (amt > 0m ? amt <= outstanding : outstanding < 0m && amt >= outstanding);
                        act = () => PayAsync(inv.Id, amt);
                        expectAndCommit = o =>
                        {
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "應拒絕卻被接受（" + (inv.Void ? "發票已作廢" : "超過未結") + "）";
                            if (o.Accepted) { inv.Paid += amt; m.Pays.Add(new Pay { Id = o.Detail, InvId = inv.Id, Amt = amt }); }
                            return null;
                        };
                        break;
                    }
                    case "退出":
                    {
                        var cands = m.Recvs.Where(r => !r.Void && m.Returnable(r) > 0).ToList();
                        if (cands.Count == 0) continue;
                        // 一半機率挑已付款的：折讓單那條路徑才走得到
                        var paidCands = cands.Where(r => status[r.InvId] != APInvoiceStatus.Open && !m.Invs[r.InvId].Void).ToList();
                        if (paidCands.Count > 0 && rng.NextDouble() < 0.5) cands = paidCands;
                        var r0 = cands[rng.Next(cands.Count)];
                        var max = m.Returnable(r0);
                        var q = rng.NextDouble() < 0.1 ? max + 1 : Math.Max(1, Math.Floor(max * (decimal)rng.NextDouble()));
                        var inv = m.Invs[r0.InvId];
                        label = $"退出 {r0.Id} ×{q}（可退 {max}）";
                        act = () => ReturnAsync(r0.Id, q);
                        expectAndCommit = o =>
                        {
                            var shouldAccept = q <= max;
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "退貨量超過可退量卻被接受";
                            if (!o.Accepted) return null;
                            var ret = new Ret { Id = o.Detail, Gr = r0.Id, Qty = q };
                            m.Rets.Add(ret);
                            if (inv.Void) return null;
                            if (status[inv.Id] == APInvoiceStatus.Open) { inv.Total -= q * Gross; ret.ReducedInvoice = q * Gross; }
                            else ret.NoteId = "?";
                            return null;
                        };
                        break;
                    }
                    case "確認折讓":
                    {
                        var drafts = m.Notes.Values.Where(n => !n.Posted && !n.Void).ToList();
                        if (drafts.Count == 0) continue;
                        var n = drafts[rng.Next(drafts.Count)];
                        var inv = m.Invs[n.InvId];
                        label = $"確認折讓 {n.Id} {n.Gross}（發票 {inv.Id}{(inv.Void ? " 已作廢" : "")}）";
                        act = () => Try(scope => Svc<IAPDebitNoteService>(scope).ConfirmAsync(n.Id));
                        expectAndCommit = o =>
                        {
                            var shouldAccept = !inv.Void;
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "發票已作廢卻仍可確認折讓";
                            if (o.Accepted) { n.Posted = true; inv.Debited += n.Gross; }
                            return null;
                        };
                        break;
                    }
                    case "作廢付款":
                    {
                        var live = m.Pays.Where(p => !p.Void).ToList();
                        if (live.Count == 0) continue;
                        var p = live[rng.Next(live.Count)];
                        label = $"作廢付款 {p.Id}（{p.InvId} {p.Amt}）";
                        act = () => Try(scope => Svc<IFinPaymentService>(scope).VoidAPPaymentAsync(p.Id));
                        var pInv = m.Invs[p.InvId];
                        var mustReject = p.Amt > 0m && pInv.Paid - p.Amt < 0m;
                        expectAndCommit = o =>
                        {
                            if (o.Accepted == mustReject)
                                return mustReject ? "已退回卻可作廢原付款（已付會變負數）" : $"應接受卻被拒絕：{o.Detail}";
                            if (!o.Accepted) return null;
                            p.Void = true; m.Invs[p.InvId].Paid -= p.Amt;
                            return null;
                        };
                        break;
                    }
                    case "作廢發票":
                    {
                        var live = m.Invs.Values.Where(i => !i.Void && (i.Debited == 0m || !Skip.Contains("作廢有折讓的發票"))).ToList();
                        if (live.Count == 0) continue;
                        var inv = live[rng.Next(live.Count)];
                        // 折讓單有自己的分錄，作廢發票只沖發票那幾張；已確認的折讓不先作廢，總帳就留一筆沖不掉的折讓
                        var shouldAccept = inv.Paid == 0m && inv.Debited == 0m && status[inv.Id] == APInvoiceStatus.Open;
                        label = $"作廢發票 {inv.Id}（{status[inv.Id]}、已付 {inv.Paid}、已折讓 {inv.Debited}）";
                        act = () => Try(scope => Svc<IAPInvoiceService>(scope).VoidAsync(inv.Id));
                        expectAndCommit = o =>
                        {
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : (inv.Debited != 0m ? "有已確認折讓的發票卻可作廢" : "已付款發票卻可作廢");
                            if (o.Accepted) inv.Void = true;
                            return null;
                        };
                        break;
                    }
                    case "作廢退出":
                    {
                        var live = m.Rets.Where(r => !r.Void).ToList();
                        if (live.Count == 0) continue;
                        var r = live[rng.Next(live.Count)];
                        var inv = m.Invs[m.Recvs.Single(x => x.Id == r.Gr).InvId];
                        label = $"作廢退出 {r.Id}（{r.Gr} ×{r.Qty}，當初回沖發票 {r.ReducedInvoice}、折讓單 {r.NoteId ?? "-"}）";
                        act = () => VoidReturnAsync(r.Id);
                        expectAndCommit = o =>
                        {
                            // 2026-10-22 定案前的守門：發票還活著、或折讓單未作廢就擋（作廢退貨不會把回沖補回去）
                            var noteLive = r.NoteId != null && m.Notes.TryGetValue(r.NoteId, out var nn) && !nn.Void;
                            var shouldAccept = inv.Void && !noteLive;
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "發票未作廢或折讓未作廢，作廢退貨卻被放行";
                            if (!o.Accepted) return null;
                            r.Void = true;
                            if (r.ReducedInvoice != 0m && !inv.Void) inv.Total += r.ReducedInvoice;
                            if (r.NoteId != null && m.Notes.TryGetValue(r.NoteId, out var n) && !n.Void)
                            {
                                if (n.Posted && !inv.Void) inv.Debited -= n.Gross;
                                n.Void = true;
                            }
                            return null;
                        };
                        break;
                    }
                    case "作廢折讓":
                    {
                        var live = m.Notes.Values.Where(n => !n.Void).ToList();
                        if (live.Count == 0) continue;
                        var n = live[rng.Next(live.Count)];
                        var inv = m.Invs[n.InvId];
                        label = $"作廢折讓 {n.Id}（{(n.Posted ? "已確認" : "草稿")} {n.Gross}）";
                        act = () => Try(scope => Svc<IAPDebitNoteService>(scope).VoidAsync(n.Id, "序列測試"));
                        expectAndCommit = o =>
                        {
                            if (!o.Accepted) return $"應接受卻被拒絕：{o.Detail}";
                            if (n.Posted && !inv.Void) inv.Debited -= n.Gross;
                            n.Void = true;
                            if (m.Rets.Any(r => r.NoteId == n.Id && !r.Void) && !inv.Void)
                                ExpectInvariantViolation("RETURN_SUBLEDGER_UNREVERSED",
                                    "序列中作廢了退出仍有效的折讓單：退出的應付從此沒沖，要重開折讓單才會消失，稽核照實報它");
                            return null;
                        };
                        break;
                    }
                    case "作廢進貨":
                    {
                        var live = m.Recvs.Where(r => !r.Void).ToList();
                        if (live.Count == 0) continue;
                        var r = live[rng.Next(live.Count)];
                        var inv = m.Invs[r.InvId];
                        label = $"作廢進貨 {r.Id}（發票 {inv.Id}{(inv.Void ? " 已作廢" : $" 未作廢、已付 {inv.Paid}")}、有效退出 {m.Rets.Count(x => x.Gr == r.Id && !x.Void)} 張）";
                        act = () => VoidReceiptAsync(r.Id);
                        expectAndCommit = o =>
                        {
                            // 發票還活著、或還有未作廢的退貨就擋：作廢只沖庫存，應收應付與退貨不會跟著走
                            var shouldAccept = inv.Void && !m.Rets.Any(x => x.Gr == r.Id && !x.Void);
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "發票未作廢或有有效退貨，作廢卻被放行";
                            if (!o.Accepted) return null;
                            r.Void = true;
                            return null;
                        };
                        break;
                    }
                    default: throw new InvalidOperationException(kind);
                }

                var outcome = await act();
                history.Add($"{step,2}. {label} → {(outcome.Accepted ? "接受" : $"拒絕：{outcome.Detail}")}");
                var key = $"{kind}{(outcome.Accepted ? "" : "(拒絕)")}";
                stats[key] = stats.GetValueOrDefault(key) + 1;

                var mismatch = expectAndCommit(outcome);
                Assert.True(mismatch == null, Fail(step, mismatch ?? ""));
                await SyncNotesAsync(m);

                var problem = await CheckAsync(m);
                Assert.True(problem == null, Fail(step, problem ?? ""));
            }

            foreach (var n in m.Notes.Values.Where(n => !n.Posted && !n.Void && !m.Invs[n.InvId].Void).ToList())
            {
                var o = await Try(scope => Svc<IAPDebitNoteService>(scope).ConfirmAsync(n.Id));
                history.Add($"收尾 確認折讓 {n.Id} → {(o.Accepted ? "接受" : $"拒絕：{o.Detail}")}");
                Assert.True(o.Accepted, Fail(steps, $"收尾確認折讓被拒絕：{o.Detail}"));
                n.Posted = true; m.Invs[n.InvId].Debited += n.Gross;
            }
            var last = await CheckAsync(m);
            Assert.True(last == null, Fail(steps, last ?? ""));

            try { await AssertLedgerInvariantsAsync(); }
            catch (Xunit.Sdk.XunitException ex) { Assert.Fail(Fail(steps, ex.Message)); }
        }

        /// <summary>
        /// 退出退錯了的人工更正路徑（2026-10-22 使用者裁決：不自動補回應付，由人開單處理）：
        /// 作廢退出被擋下，另開一張進貨單把貨收回來。採購單已收滿時掛原採購單會撞超收（退出不釋放採購量），
        /// 不掛採購單的進貨要能開並認列應付——否則擋下訊息就是在誤導人。
        /// </summary>
        [Fact]
        public async Task 退出退錯了_不能作廢_另開進貨單收回後應付與存貨回到正確()
        {
            await SetSimplifiedAsync("true");
            try
            {
                using (var scope = NewScope(out var db))
                {
                    var line = await db.PurchaseOrderLines.IgnoreQueryFilters().SingleAsync(l => l.PurchaseOrderId == Po);
                    line.OrderQty = 10m;   // 收滿：掛原採購單補收會被擋
                    await db.SaveChangesAsync();
                }
                var recv = await ReceiveAsync(10m);
                Assert.True(recv.Accepted, recv.Detail);
                var (gr, _) = SplitPair(recv.Detail);
                var ret = await ReturnAsync(gr, 4m);
                Assert.True(ret.Accepted, ret.Detail);

                var v = await VoidReturnAsync(ret.Detail);
                Assert.False(v.Accepted);

                Assert.False((await ReceiveAsync(4m)).Accepted);   // 掛原採購單：超收

                var before = await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId));
                using (var scope = NewScope(out var db))
                {
                    var ctrl = NewReceiptController(scope, db);
                    var saved = await ctrl.Save(new GoodsReceiptFormVm
                    {
                        ReceiptDate = D, CreatedBy = UserId, SupplierCode = Supplier,
                        Lines =
                        {
                            new GoodsReceiptLineVm
                            {
                                LineNo = 1, ProductCode = Mat, ReceiptQty = 4m, StockQty = 4m, UomCode = Uom, StockUomCode = Uom,
                                UnitPrice = Price, ExchangeRate = 1m, TaxRate = TaxRate,
                                LineAmount = 4m * Price, TaxAmount = 4m * Price * TaxRate, TotalAmount = 4m * Gross, AmountLocal = 4m * Gross,
                            },
                        },
                    });
                    Assert.True(saved is ContentResult, "不掛採購單補進貨應成功：" + ModelErrors(ctrl));
                }
                var gr2 = (await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId))).Except(before).Single();
                using (var scope = NewScope(out var db))
                    if ((await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.GoodsReceiptId == gr2)).Status == DocStatus.Draft)
                        Assert.IsType<OkObjectResult>(await NewReceiptController(scope, db).Confirm(gr2));

                using (var scope = NewScope(out var db))
                {
                    var gl = await GlNetByAccountAsync(db, Plant);
                    Assert.Equal(-10m * Gross, gl.GetValueOrDefault(StandardAccounts.AccountsPayableCode));   // 應付回到 10 件
                    Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.GrIrAPClearingCode));
                    Assert.Equal(10m, await QtyOnHandAsync(db, Plant, Mat));
                }
            }
            finally { await SetSimplifiedAsync(null); }
        }

        /// <summary>
        /// 完整倉儲模式分批上架：第一批認列的應付作廢後，第二批上架要開出新的應付。原本認列時找到作廢那張、
        /// 丟「不可再追加」，下一批整個收不進來；唯一索引也不分狀態（2026-10-22）。
        /// </summary>
        [Fact]
        public async Task 完整模式分批上架_第一批應付作廢後_第二批重新認列()
        {
            await SetSimplifiedAsync("false");
            try
            {
                var before = await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId));
                using (var scope = NewScope(out var db))
                {
                    var ctrl = NewReceiptController(scope, db);
                    Assert.True(await ctrl.Save(new GoodsReceiptFormVm
                    {
                        ReceiptDate = D, CreatedBy = UserId, SupplierCode = Supplier,
                        Lines =
                        {
                            new GoodsReceiptLineVm
                            {
                                LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat, ReceiptQty = 10m, StockQty = 10m,
                                UomCode = Uom, StockUomCode = Uom, UnitPrice = Price, ExchangeRate = 1m, TaxRate = TaxRate,
                                LineAmount = 10m * Price, TaxAmount = 10m * Price * TaxRate, TotalAmount = 10m * Gross, AmountLocal = 10m * Gross,
                            },
                        },
                    }) is ContentResult, ModelErrors(ctrl));
                }
                var gr = (await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId))).Except(before).Single();

                Assert.True((await PutAwayAsync("PI", gr, 4m)).Accepted);
                string first;
                using (var scope = NewScope(out var db))
                    first = (await db.APInvoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.SourceDocId == gr)).APInvoiceId;
                Assert.True((await Try(scope => Svc<IAPInvoiceService>(scope).VoidAsync(first))).Accepted);

                var put2 = await PutAwayAsync("PI", gr, 6m);
                Assert.True(put2.Accepted, put2.Detail);

                using (var scope = NewScope(out var db))
                {
                    var live = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                        .SingleAsync(i => i.SourceDocId == gr && i.Status != APInvoiceStatus.Void);
                    Assert.NotEqual(first, live.APInvoiceId);
                    Assert.Equal(6m * Gross, live.TotalAmount);   // 新發票只認第二批；第一批 4 件在 GR/IR 等人處理
                    var gl = await GlNetByAccountAsync(db, Plant);
                    Assert.Equal(-4m * Price, gl.GetValueOrDefault(StandardAccounts.GrIrAPClearingCode));
                }
            }
            finally { await SetSimplifiedAsync(null); }
        }

        /// <summary>
        /// 應付作廢後依來源重新認列（2026-10-22）：完整模式上架 6 → 作廢 → 重開得 6，之後再上架 4 累加到新發票；
        /// 作廢之後又過帳退出時重開擋下（那段退貨沒回沖到任何發票，數量算不準）。
        /// </summary>
        [Fact]
        public async Task 作廢應付後依來源重開_重認列原量_作廢後有退出則擋下()
        {
            await SetSimplifiedAsync("false");
            _full = true;
            try
            {
                async Task<string> NewGrAsync(decimal qty)
                {
                    var before = await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId));
                    using (var scope = NewScope(out var db))
                    {
                        var ctrl = NewReceiptController(scope, db);
                        Assert.True(await ctrl.Save(new GoodsReceiptFormVm
                        {
                            ReceiptDate = D, CreatedBy = UserId, SupplierCode = Supplier,
                            Lines =
                            {
                                new GoodsReceiptLineVm
                                {
                                    LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat, ReceiptQty = qty, StockQty = qty,
                                    UomCode = Uom, StockUomCode = Uom, UnitPrice = Price, ExchangeRate = 1m, TaxRate = TaxRate,
                                    LineAmount = qty * Price, TaxAmount = qty * Price * TaxRate, TotalAmount = qty * Gross, AmountLocal = qty * Gross,
                                },
                            },
                        }) is ContentResult, ModelErrors(ctrl));
                    }
                    return (await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId))).Except(before).Single();
                }
                async Task<string> LiveApAsync(string gr)
                {
                    using var scope = NewScope(out var db);
                    return await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                        .Where(i => i.SourceDocId == gr && i.Status != APInvoiceStatus.Void).Select(i => i.APInvoiceId).SingleAsync();
                }

                // ① 上架 6、作廢、重開 → 6；再上架 4 累加到新發票 → 10
                var gr1 = await NewGrAsync(10m);
                Assert.True((await PutAwayAsync("PI", gr1, 6m)).Accepted);
                var old1 = await LiveApAsync(gr1);
                Assert.True((await Try(scope => Svc<IAPInvoiceService>(scope).VoidAsync(old1))).Accepted);
                using (var scope = NewScope(out var db))
                    Assert.Equal(6m * Gross, (await Svc<IAPInvoiceService>(scope).ReissueFromSourceAsync(old1)).TotalAmount);
                Assert.True((await PutAwayAsync("PI", gr1, 4m)).Accepted);
                using (var scope = NewScope(out var db))
                {
                    var live = await db.APInvoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.SourceDocId == gr1 && i.Status != APInvoiceStatus.Void);
                    Assert.Equal(10m * Gross, live.TotalAmount);
                    Assert.Equal(0m, (await GlNetByAccountAsync(db, Plant)).GetValueOrDefault(StandardAccounts.GrIrAPClearingCode));
                }

                // ② 上架 5、作廢、再過帳退出 2 → 重開擋下
                var gr2 = await NewGrAsync(5m);
                Assert.True((await PutAwayAsync("PI", gr2, 5m)).Accepted);
                var old2 = await LiveApAsync(gr2);
                Assert.True((await Try(scope => Svc<IAPInvoiceService>(scope).VoidAsync(old2))).Accepted);
                await Task.Delay(20);   // UpdatedAt 先後
                Assert.True((await ReturnAsync(gr2, 2m)).Accepted);
                Assert.False((await Try(scope => Svc<IAPInvoiceService>(scope).ReissueFromSourceAsync(old2))).Accepted);
            }
            finally { _full = false; await SetSimplifiedAsync(null); }
        }

        /// <summary>
        /// 兩張進貨單價不同（均價因此不等於任一張的進貨價），從其中一張退貨：
        /// 應付以那張的進貨價回沖，存貨若以當下均價出庫，進貨暫估應付（GR/IR）就留下沖不掉的差額（2026-10-23）。
        /// </summary>
        [Theory]
        [InlineData("true")]
        [InlineData("false")]
        public async Task 進貨單價不同_退其中一張_進貨暫估應付歸零(string simplified)
        {
            await SetSimplifiedAsync(simplified);
            _full = simplified == "false";
            try
            {
                var r1 = await ReceiveAsync(10m, 100m);
                Assert.True(r1.Accepted, r1.Detail);
                var r2 = await ReceiveAsync(10m, 300m);   // 均價 200
                Assert.True(r2.Accepted, r2.Detail);
                var (gr1, _) = SplitPair(r1.Detail);
                var ret = await ReturnAsync(gr1, 5m);
                Assert.True(ret.Accepted, ret.Detail);

                using var scope = NewScope(out var db);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, gl.GetValueOrDefault(StandardAccounts.GrIrAPClearingCode));
                Assert.Empty(await Svc<SmartFactory.MES.Modules.ACT.Services.IDataAuditService>(scope).AuditInventoryGlSubledgerAsync(Plant));
            }
            finally { _full = false; await SetSimplifiedAsync(null); }
        }

        private static string Pick(Random rng, (string Kind, double Weight)[] kinds)
        {
            var x = rng.NextDouble() * kinds.Sum(k => k.Weight);
            foreach (var k in kinds) { if ((x -= k.Weight) < 0) return k.Kind; }
            return kinds[^1].Kind;
        }

        private static (string, string) SplitPair(string s) { var p = s.Split('|'); return (p[0], p[1]); }

        private async Task<Dictionary<string, APInvoiceStatus>> InvoiceStatusesAsync()
        {
            using var scope = NewScope(out var db);
            return await db.APInvoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.PlantId == Plant)
                .ToDictionaryAsync(i => i.APInvoiceId, i => i.Status);
        }

        private async Task SyncNotesAsync(Model m)
        {
            var pending = m.Rets.Where(r => r.NoteId == "?").ToList();
            if (pending.Count == 0) return;
            using var scope = NewScope(out var db);
            foreach (var r in pending)
            {
                var note = await db.APDebitNotes.IgnoreQueryFilters().AsNoTracking().Include(n => n.Lines)
                    .FirstOrDefaultAsync(n => n.SourceDocType == "GoodsReturn" && n.SourceDocId == r.Id);
                if (note == null) { r.NoteId = null; continue; }
                r.NoteId = note.APDebitNoteId;
                Assert.True(note.TotalAmount == r.Qty * Gross,
                    $"退出 {r.Id} ×{r.Qty} 開出的折讓單 {note.APDebitNoteId} 金額 {note.TotalAmount}，應為 {r.Qty * Gross}");
                var invId = note.Lines.Select(l => l.APInvoiceId).Distinct().Single();
                m.Notes[note.APDebitNoteId] = new Note { Id = note.APDebitNoteId, InvId = invId, Gross = note.TotalAmount };
            }
        }

        private async Task<string?> CheckAsync(Model m)
        {
            using var scope = NewScope(out var db);
            var invs = await db.APInvoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.PlantId == Plant).ToListAsync();
            foreach (var mi in m.Invs.Values)
            {
                var di = invs.SingleOrDefault(i => i.APInvoiceId == mi.Id);
                if (di == null) return $"發票 {mi.Id} 不見了";
                if ((di.Status == APInvoiceStatus.Void) != mi.Void) return $"發票 {mi.Id} 作廢狀態 {di.Status}，模型 {(mi.Void ? "已作廢" : "未作廢")}";
                if (di.TotalAmount != mi.Total) return $"發票 {mi.Id} 總額 {di.TotalAmount}，模型 {mi.Total}";
                if (di.PaidAmount != mi.Paid) return $"發票 {mi.Id} 已付 {di.PaidAmount}，模型 {mi.Paid}";
                if (di.DebitedAmount != mi.Debited) return $"發票 {mi.Id} 已折讓 {di.DebitedAmount}，模型 {mi.Debited}";
            }

            var gl = await GlNetByAccountAsync(db, Plant);
            decimal G(string code) => gl.GetValueOrDefault(code);
            if (_fx) return await CheckFxAsync(m, db, invs, G);

            var subledger = invs.Where(i => i.Status != APInvoiceStatus.Void).Sum(i => i.TotalAmount - i.PaidAmount - i.DebitedAmount);
            if (-G(StandardAccounts.AccountsPayableCode) != subledger)
                return $"總帳應付（貸餘）{-G(StandardAccounts.AccountsPayableCode)} ≠ 應付子帳 {subledger}";

            var paid = await db.APPayments.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PlantId == Plant && p.Status == APPaymentStatus.Paid).SumAsync(p => (decimal?)p.AmountLocal) ?? 0m;
            // 期初種子銀行餘額不入總帳（BankAccount.Balance），總帳銀行科目只有付款
            if (-G(StandardAccounts.CashAndBankCode) != paid)
                return $"總帳銀行（貸餘）{-G(StandardAccounts.CashAndBankCode)} ≠ 已確認付款 {paid}";

            var grir = G(StandardAccounts.GrIrAPClearingCode);
            var expectedGrir = (m.PendingReturnQty - m.UninvoicedQty) * Price;
            if (grir != expectedGrir)
                return $"進貨暫估應付（GR/IR）淨額 {grir} ≠（已退出未沖應付 {m.PendingReturnQty} − 已收貨未請款 {m.UninvoicedQty}）× {Price} = {expectedGrir}";

            var bought = m.BoughtQty;
            if (G(StandardAccounts.InputVatCode) != bought * Price * TaxRate)
                return $"進項稅額 {G(StandardAccounts.InputVatCode)} ≠ 實際買進 {bought} × {Price * TaxRate} = {bought * Price * TaxRate}";
            return null;
        }

        // ── 操作（走真入口） ─────────────────────────────────────────────

        /// <summary>
        /// 外幣情境的本幣檢查（容許四捨五入）：發票匯率＝進貨日買匯；應付總帳＝未結外幣 × 發票匯率；
        /// 進項稅＝實際買進 × 價 × 稅率 × 發票匯率；GR/IR＝（已退出未沖 − 已收貨未請款）× 價 × 該張進貨匯率；
        /// 銀行＝−付款本幣；匯兌損益（借方淨額）＝核銷外幣 ×（付款匯率 − 發票匯率）。
        /// </summary>
        private async Task<string?> CheckFxAsync(Model m, ApplicationDbContext db, List<APInvoice> invs, Func<string, decimal> G)
        {
            const decimal tol = 1m;
            var rate = invs.ToDictionary(i => i.APInvoiceId, i => i.ExchangeRate);
            foreach (var r0 in m.Recvs)
            {
                var grDate = await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking().Where(g => g.GoodsReceiptId == r0.Id).Select(g => g.ReceiptDate).SingleAsync();
                var di = invs.Single(i => i.APInvoiceId == r0.InvId);
                if (di.CurrencyCode != Fx || di.ExchangeRate != BuyRate(grDate))
                    return $"應付 {di.APInvoiceId} 幣別 {di.CurrencyCode} 匯率 {di.ExchangeRate}，應為 {Fx} @進貨日買匯 {BuyRate(grDate)}";
            }
            var apExpected = invs.Where(i => i.Status != APInvoiceStatus.Void).Sum(i => (i.TotalAmount - i.PaidAmount - i.DebitedAmount) * i.ExchangeRate);
            if (Math.Abs(-G(StandardAccounts.AccountsPayableCode) - apExpected) > tol)
                return $"總帳應付（本幣貸餘）{-G(StandardAccounts.AccountsPayableCode)} ≠ 未結外幣 × 發票匯率 {apExpected}";

            decimal vat = 0m, grir = 0m;
            foreach (var r0 in m.Recvs.Where(x => !x.Void))
            {
                var inv = m.Invs[r0.InvId];
                var rt = rate[r0.InvId];
                var activeRets = m.Rets.Where(x => x.Gr == r0.Id && !x.Void).ToList();
                if (inv.Void) { grir -= m.Returnable(r0) * Price * rt; continue; }
                vat += (r0.Qty - activeRets.Where(m.Reflected).Sum(x => x.Qty)) * Price * TaxRate * rt;
                grir += activeRets.Where(x => !m.Reflected(x)).Sum(x => x.Qty) * Price * rt;
            }
            if (Math.Abs(G(StandardAccounts.InputVatCode) - vat) > tol)
                return $"進項稅額（本幣）{G(StandardAccounts.InputVatCode)} ≠ {vat}";
            if (Math.Abs(G(StandardAccounts.GrIrAPClearingCode) - grir) > tol)
                return $"進貨暫估應付（本幣）{G(StandardAccounts.GrIrAPClearingCode)} ≠ {grir}";

            var pays = await db.APPayments.IgnoreQueryFilters().AsNoTracking().Include(p => p.Allocations)
                .Where(p => p.PlantId == Plant && p.Status == APPaymentStatus.Paid).ToListAsync();
            foreach (var p in pays)
                if (p.ExchangeRate != BuyRate(p.PaymentDate) || Math.Abs(p.AmountLocal - p.Amount * p.ExchangeRate) > 0.5m)
                    return $"付款 {p.APPaymentId} 匯率 {p.ExchangeRate}／本幣 {p.AmountLocal}，應為 付款日買匯 {BuyRate(p.PaymentDate)}";
            if (Math.Abs(-G(StandardAccounts.CashAndBankCode) - pays.Sum(p => p.AmountLocal)) > tol)
                return $"總帳銀行（貸餘）{-G(StandardAccounts.CashAndBankCode)} ≠ 已付款本幣 {pays.Sum(p => p.AmountLocal)}";
            var fxExpected = pays.SelectMany(p => p.Allocations.Select(a => a.AllocatedAmount * (p.ExchangeRate - rate[a.APInvoiceId]))).Sum();
            var fxActual = G(StandardAccounts.FxGainCode) + G(StandardAccounts.FxLossCode);
            if (Math.Abs(fxActual - fxExpected) > tol)
                return $"匯兌損益（借方淨額）{fxActual} ≠ 核銷外幣 ×（付款匯率 − 發票匯率）{fxExpected}";
            return null;
        }

        private async Task<HashSet<string>> IdsAsync(Func<ApplicationDbContext, IQueryable<string>> q)
        {
            using var scope = NewScope(out var db);
            return (await q(db).ToListAsync()).ToHashSet();
        }

        private async Task<Outcome> ReceiveAsync(decimal q, decimal? unitPrice = null)
        {
            var price = unitPrice ?? Price;
            var before = await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId));
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewReceiptController(scope, db);
                var vm = new GoodsReceiptFormVm
                {
                    ReceiptDate = _date, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    {
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat, ReceiptQty = q, StockQty = q,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = price, CurrencyCode = _fx ? Fx : null, ExchangeRate = 1m, TaxRate = TaxRate,
                            LineAmount = q * price, TaxAmount = q * price * TaxRate, TotalAmount = q * price * (1m + TaxRate), AmountLocal = q * price * (1m + TaxRate),
                        },
                    },
                };
                var saved = await ctrl.Save(vm);
                if (saved is not ContentResult) return new(false, ModelErrors(ctrl));
            }
            var gr = (await IdsAsync(db => db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReceiptId)))
                .Except(before).Single();
            // 沒設簽核流程時存檔即核准（GoodsReceiptController.Save）：簡化模式直接過帳，完整模式停在 Confirmed 待上架
            using (var scope = NewScope(out var db))
                if ((await db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.GoodsReceiptId == gr)).Status == DocStatus.Draft)
                {
                    var r = await NewReceiptController(scope, db).Confirm(gr);
                    if (r is not OkObjectResult) return new(false, $"確認：{(r as ObjectResult)?.Value}");
                }
            if (_full)
            {
                var put = await PutAwayAsync("PI", gr, q);   // 免驗品直上正常倉，此刻才認列應付
                if (!put.Accepted) return put;
            }
            using (var scope = NewScope(out var db))
            {
                var inv = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleOrDefaultAsync(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == gr);
                return inv == null ? new(false, "進貨確認後沒有應付發票") : new(true, $"{gr}|{inv.APInvoiceId}");
            }
        }

        private async Task<Outcome> PayAsync(string invId, decimal amt)
        {
            try
            {
                using var scope = NewScope(out var db);
                var fin = Svc<IFinPaymentService>(scope);
                var pay = await fin.CreateAPPaymentAsync(new APPaymentRequest(Supplier, _date, PaymentMethod.Wire,
                    _fx ? Fx : null, _fx ? BuyRate(_date) : 1m, amt, Bank, null, null,
                    new List<PaymentAllocationLine> { new(invId, amt) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(pay.APPaymentId);
                return new(true, pay.APPaymentId);
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }

        private async Task<Outcome> ReturnAsync(string gr, decimal q)
        {
            var before = await IdsAsync(db => db.GoodsReturns.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReturnId));
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewReturnController(scope, db);
                var vm = new GoodsReturnFormVm
                {
                    ReturnDate = _date, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    {
                        new GoodsReturnLineVm
                        {
                            LineNo = 1, GoodsReceiptId = gr, ReceiptLineNo = 1, ProductCode = Mat, ReturnQty = q, StockQty = q,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = Price, CurrencyCode = _fx ? Fx : null, ExchangeRate = 1m, TaxRate = TaxRate,
                            LineAmount = q * Price, TaxAmount = q * Price * TaxRate, TotalAmount = q * Gross, AmountLocal = q * Gross,
                        },
                    },
                };
                var saved = await ctrl.Save(vm);
                if (saved is not ContentResult) return new(false, ModelErrors(ctrl));
            }
            var id = (await IdsAsync(db => db.GoodsReturns.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsReturnId)))
                .Except(before).Single();
            using (var scope = NewScope(out var db))
            {
                if ((await db.GoodsReturns.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.GoodsReturnId == id)).Status == DocStatus.Draft)
                {
                    var r = await NewReturnController(scope, db).Confirm(id);
                    if (r is not OkObjectResult)
                    {
                        await db.GoodsReturns.IgnoreQueryFilters().Where(d => d.GoodsReturnId == id).ExecuteDeleteAsync();
                        return new(false, $"確認：{(r as ObjectResult)?.Value}");
                    }
                }
            }
            if (_full)
            {
                var pick = await PickAsync("PF", id, q);   // 完整模式：退出下架時才過帳並回沖應付
                if (!pick.Accepted) return pick;
            }
            return new(true, id);
        }

        private async Task<Outcome> VoidReturnAsync(string id)
        {
            using var scope = NewScope(out var db);
            var r = await NewReturnController(scope, db).Void(id, "序列測試");
            return r is OkObjectResult ? new(true, "") : new(false, $"{(r as ObjectResult)?.Value}");
        }

        private async Task<Outcome> VoidReceiptAsync(string id)
        {
            using var scope = NewScope(out var db);
            var r = await NewReceiptController(scope, db).Void(id, "序列測試");
            return r is OkObjectResult ? new(true, "") : new(false, $"{(r as ObjectResult)?.Value}");
        }

        private async Task<Outcome> Try(Func<IServiceScope, Task> body)
        {
            try { using var scope = NewScope(out _); await body(scope); return new(true, ""); }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }

        private static string ModelErrors(Controller c) =>
            string.Join("；", c.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));

        private async Task SetSimplifiedAsync(string? value)
        {
            using var scope = NewScope(out _);
            await Svc<ISystemParamService>(scope).SetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, value);
        }

        private SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController NewWarehouseController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IUomService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IAPInvoiceService>(), sp.GetRequiredService<IARInvoiceService>(),
                sp.GetRequiredService<SmartFactory.MES.Modules.ACT.Services.IJournalEntryService>(),
                sp.GetRequiredService<IARCreditNoteService>(), sp.GetRequiredService<IAPDebitNoteService>(),
                sp.GetRequiredService<IInventoryReservationService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        /// <summary>完整倉儲模式：單據確認後停在 Confirmed，要經倉儲 API 上架／揀貨才過帳（應收應付的回沖也在這一步）。</summary>
        private async Task<Outcome> PutAwayAsync(string typeCode, string docNo, decimal qty)
        {
            using var scope = NewScope(out var db);
            var r = await NewWarehouseController(scope, db).PutAway(new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController.PutAwayRequest
            {
                TypeCode = typeCode, DocNo = docNo, LineNo = 1, WhCode = Wh, LocationCode = Loc, LotNo = "", Qty = qty,
            });
            return r is OkObjectResult ? new(true, "") : new(false, $"上架：{(r as ObjectResult)?.Value}");
        }

        private async Task<Outcome> PickAsync(string typeCode, string docNo, decimal qty)
        {
            using var scope = NewScope(out var db);
            var r = await NewWarehouseController(scope, db).Pick(new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController.PickRequest
            {
                TypeCode = typeCode, DocNo = docNo, LineNo = 1,
                Picks = { new SmartFactory.MES.Modules.INV.Controllers.WarehouseApiController.PickItem { LocationCode = Loc, LotNo = "", Qty = qty } },
            });
            return r is OkObjectResult ? new(true, "") : new(false, $"揀貨：{(r as ObjectResult)?.Value}");
        }


        private GoodsReceiptController NewReceiptController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new GoodsReceiptController(
                db, sp.GetRequiredService<IPurchaseService>(), sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(), sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(), sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IInventoryPostingService>(), sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IAPInvoiceService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        private GoodsReturnController NewReturnController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new GoodsReturnController(
                db, sp.GetRequiredService<IPurchaseService>(), sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<IPermissionService>(), sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IApprovalService>(), sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IInventoryPostingService>(), sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IAPInvoiceService>(), sp.GetRequiredService<IAPDebitNoteService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }
    }
}
