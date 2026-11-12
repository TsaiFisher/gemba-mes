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
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 銷售應收的隨機操作序列測試（model-based，2026-10-22）：出貨開票、收款、銷退（未收款發票直接回沖、
    /// 已收款發票開折讓單）、確認折讓，以及收款／發票／銷退／折讓／出貨的作廢隨機交錯，
    /// 每一步都跟一個參考模型對照。與 <see cref="InventoryLedgerModelTests"/> 同一套做法，換到應收這一條鏈。
    ///
    /// <para><b>出貨與銷退走真的 Controller</b>：「退貨要回沖應收、已收款就開折讓單」這段串接寫在
    /// <c>SalesReturnController</c> 裡，直接呼叫服務等於把要驗的那段跳過去（O2CShipmentReturnFlowTests 的教訓）。</para>
    ///
    /// <para><b>每一步都檢查</b>：
    /// ①每張發票的 總額／已收／已折讓／是否作廢＝參考模型；
    /// ②總帳應收科目＝應收子帳（未作廢發票的 總額−已收−已折讓）；
    /// ③總帳銀行＝已確認、未作廢收款合計；
    /// ④<b>經濟事實</b>：銷貨收入＝售價 ×（出貨量−仍有效的退貨量），只算發票沒作廢的出貨；銷項稅同理——
    ///   這一條不看系統怎麼記，只看「貨實際賣出去多少」，作廢退貨或作廢出貨若沒把應收帶著走就會現形。
    /// 每條序列結束再跑 FlowTestBase 的帳務不變量。</para>
    ///
    /// <para>失敗時印出種子與完整操作序列，可原樣重現。規模：AR_MODEL_SEEDS（預設 20）、AR_MODEL_STEPS（預設 30）；
    /// AR_MODEL_SKIP 可逗號分隔略過某幾種操作（例如已知缺陷未修時，先看其他操作有沒有問題）；
    /// 「收款作廢發票」表示收款不挑已作廢的發票；「退款」表示不做多收退款。</para>
    /// </summary>
    [Collection("Integration")]
    public class ReceivableLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZARMODEL";

        private const string Fg  = "FLOW-ARM-FG";
        private const string So  = "FLOW-ARM-SO";
        private const string Cat = "FLOW-ARM-CAT";
        private static readonly DateOnly D = new(2095, 5, 10);

        private const decimal Price   = 300m;
        private const decimal TaxRate = 0.05m;
        private const decimal Gross   = Price * (1m + TaxRate);   // 每件含稅 315，數量取整數金額就沒有捨入

        private readonly ITestOutputHelper _out;

        public ReceivableLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "應收序列成品"));
            using var scope = NewScope(out var db);
            // 出貨單／銷退單表單沒有倉儲欄位，過帳一律回退到類別預設倉儲（見 O2CShipmentReturnFlowTests）
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Cat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Cat, CategoryName = "應收序列類別", CostMethod = CostMethod.MovingAverage,
                    IsActive = true, DefaultWarehouseCode = Wh, DefaultLocationCode = Loc,
                });
            await db.SaveChangesAsync();
            (await db.Products.IgnoreQueryFilters().SingleAsync(p => p.ProductCode == Fg)).CategoryCode = Cat;
            await db.SaveChangesAsync();

            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = "FLOW-ARM-OPEN", LineNo = 1, ProductCode = Fg, LocationCode = Loc, LotNo = "",
                Qty = 100_000m, TypeCode = "PI", TypeCategory = "Purchase", MovementDate = D,
                UomCode = Uom, StockUomCode = Uom, UnitPrice = 100m,
                UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted = () => { },
            });
            await db.SaveChangesAsync();

            var so = new SalesOrder
            {
                SalesOrderId = So, OrderDate = D, CompanyCode = Customer, CreatedBy = UserId,
                ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = Plant, CreatedByRoleId = RoleId,
            };
            so.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = So, LineNo = 1, ProductCode = Fg, OrderQty = 100_000m, UomCode = Uom,
                StockQty = 100_000m, StockUomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
            });
            db.SalesOrders.Add(so);
            await db.SaveChangesAsync();

            if (!await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == Fx))
                db.Currencies.Add(new SmartFactory.MES.Modules.BAS.Models.Currency { CurrencyCode = Fx, CurrencyName = "應收序列外幣", Symbol = "F$", DecimalPlaces = 2, IsActive = true });
            for (var d = D; d <= D.AddDays(200); d = d.AddDays(1))
                if (!await db.CurrencyRates.AsNoTracking().AnyAsync(r => r.CurrencyCode == Fx && r.RateDate == d))
                    db.CurrencyRates.Add(new SmartFactory.MES.Modules.BAS.Models.CurrencyRate { CurrencyCode = Fx, RateDate = d, BuyRate = SellRate(d) - 0.3m, SellRate = SellRate(d) });
            await db.SaveChangesAsync();
            Svc<ICurrencyService>(scope).InvalidateCache();
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        /// <summary>
        /// 已知缺陷、修法待裁決時放這裡（預設略過，其餘操作照常守門）。2026-10-22 第一輪找到的 5 項已全部改成守門擋下、清空；
        /// 之後再找到新的，先列進來、修好再拿掉。AR_MODEL_SKIP=none 全開重現。
        /// </summary>
        private static readonly Dictionary<string, string> KnownDefects = new()
        {
        };

        private static readonly HashSet<string> Skip =
            Environment.GetEnvironmentVariable("AR_MODEL_SKIP") is { } env
                ? env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x != "none").ToHashSet()
                : KnownDefects.Keys.ToHashSet();

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Ship { public string Id = ""; public decimal Qty; public bool Void; public string InvId = ""; }
        private sealed class Inv { public string Id = ""; public string Gs = ""; public decimal Total, Received, Credited; public bool Void; }
        private sealed class Ret { public string Id = ""; public string Gs = ""; public decimal Qty; public bool Void; public decimal ReducedInvoice; public string? NoteId; }
        private sealed class Note { public string Id = ""; public string InvId = ""; public decimal Gross; public bool Posted, Void; }
        private sealed class Rcpt { public string Id = ""; public string InvId = ""; public decimal Amt; public bool Void; }

        private sealed class Model
        {
            public readonly List<Ship> Ships = new();
            public readonly Dictionary<string, Inv> Invs = new();
            public readonly List<Ret> Rets = new();
            public readonly Dictionary<string, Note> Notes = new();
            public readonly List<Rcpt> Rcpts = new();

            public decimal Outstanding(Inv i) => i.Total - i.Received - i.Credited;
            public decimal Returnable(Ship s) => s.Qty - Rets.Where(r => r.Gs == s.Id && !r.Void).Sum(r => r.Qty);

            /// <summary>
            /// 這張退貨已經反映到應收上：未收款發票當場改小，或折讓單已確認。
            /// 已收款發票的退貨在折讓確認前只開了草稿——那是單據流程的中間狀態，
            /// 由 RETURN_SUBLEDGER_UNREVERSED 稽核盯著，不是帳錯。
            /// </summary>
            public bool Reflected(Ret r) => r.ReducedInvoice != 0m
                || (r.NoteId != null && Notes.TryGetValue(r.NoteId, out var n) && n.Posted && !n.Void);

            /// <summary>經濟事實：發票沒作廢的出貨，實際賣出去的量（出貨−已反映到應收的有效退貨）。</summary>
            public decimal SoldQty => Ships.Where(s => !s.Void && !Invs[s.InvId].Void)
                .Sum(s => s.Qty - Rets.Where(r => r.Gs == s.Id && !r.Void && Reflected(r)).Sum(r => r.Qty));
        }

        private sealed record Outcome(bool Accepted, string Detail);

        /// <summary>這一輪是否為完整倉儲模式（確認後要上架／揀貨）。</summary>
        private bool _full;

        // ── 外幣情境 ───────────────────────────────────────────────
        private const string Fx = "ZZF";
        private bool _fx;
        /// <summary>這一步的單據日期：外幣情境每步換一天（匯率跟著變），其餘固定 D。</summary>
        private DateOnly _date = D;
        /// <summary>某日賣出匯率（銷售與收款都走賣匯）；買匯刻意不同，取錯型別會現形。</summary>
        private static decimal SellRate(DateOnly d) => 30m + (d.DayNumber % 9) * 0.5m;

        [Theory]
        [InlineData("true")]   // 簡化模式：確認當下過帳
        [InlineData("false")]  // 完整倉儲模式（ADM 與系統預設）：確認後經上架／揀貨才過帳
        [InlineData("fx")]     // 外幣（簡化模式）：每一步換一天、匯率跟著變，驗本幣帳與匯兌損益
        public async Task 隨機出貨收款退貨與作廢序列_每一步應收子帳總帳與經濟事實都一致(string simplified)
        {
            var seeds = Env("AR_MODEL_SEEDS", 20);
            var steps = Env("AR_MODEL_STEPS", 30);
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
                ("出貨", 3), ("收款", 3), ("銷退", 2), ("確認折讓", 1.5),
                ("作廢收款", 1), ("作廢發票", 1), ("作廢銷退", 1), ("作廢折讓", 0.7), ("作廢出貨", 0.7),
            };
            kinds = kinds.Where(k => !Skip.Contains(k.Kind)).ToArray();

            for (var step = 1; step <= steps; step++)
            {
                var kind = m.Ships.Count == 0 ? "出貨" : Pick(rng, kinds);
                _date = _fx ? D.AddDays(step) : D;
                // 發票狀態（Open／部分收款／已收）由收款進度決定、不跟折讓走；退貨走「直接回沖」或「開折讓」、
                // 發票能不能作廢，系統都看它。分支依資料庫當下狀態，金額仍由模型自己算。
                var status = await InvoiceStatusesAsync();
                string label;
                Func<Task<Outcome>> act;
                Func<Outcome, string?> expectAndCommit;   // 回傳 null＝與模型一致；否則為不一致的說明

                switch (kind)
                {
                    case "出貨":
                    {
                        var q = (decimal)rng.Next(1, 20);
                        label = $"出貨 ×{q}";
                        act = () => ShipAsync(q);
                        expectAndCommit = o =>
                        {
                            if (!o.Accepted) return $"出貨被拒絕：{o.Detail}";
                            var (gs, inv) = SplitPair(o.Detail);
                            m.Ships.Add(new Ship { Id = gs, Qty = q, InvId = inv });
                            m.Invs[inv] = new Inv { Id = inv, Gs = gs, Total = q * Gross };
                            return null;
                        };
                        break;
                    }
                    case "收款":
                    {
                        var live = m.Invs.Values.Where(i => !i.Void || !Skip.Contains("收款作廢發票")).ToList();
                        if (live.Count == 0) continue;
                        var inv = live[rng.Next(live.Count)];
                        var outstanding = m.Outstanding(inv);
                        decimal amt;
                        if (outstanding < 0m && Skip.Contains("退款")) continue;
                        if (outstanding < 0m) amt = rng.NextDouble() < 0.8 ? outstanding : outstanding - 1m;   // 多收的退款
                        else if (outstanding == 0m) amt = 10m;                                               // 已收清還收：要擋
                        else amt = rng.NextDouble() < 0.12 ? outstanding + 1m                                 // 超收：要擋
                                 : rng.NextDouble() < 0.5 ? outstanding : Math.Max(1m, Math.Floor(outstanding * (decimal)rng.NextDouble()));
                        label = $"收款 {inv.Id} {amt}（未結 {outstanding}{(inv.Void ? "，發票已作廢" : "")}）";
                        var shouldAccept = !inv.Void && amt != 0m
                            && (amt > 0m ? amt <= outstanding : outstanding < 0m && amt >= outstanding);
                        act = () => ReceiveAsync(inv.Id, amt);
                        expectAndCommit = o =>
                        {
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "應拒絕卻被接受（" + (inv.Void ? "發票已作廢" : "超過未結") + "）";
                            if (o.Accepted) { inv.Received += amt; m.Rcpts.Add(new Rcpt { Id = o.Detail, InvId = inv.Id, Amt = amt }); }
                            return null;
                        };
                        break;
                    }
                    case "銷退":
                    {
                        var cands = m.Ships.Where(s => !s.Void && m.Returnable(s) > 0).ToList();
                        if (cands.Count == 0) continue;
                        // 一半機率挑已收款的：折讓單那條路徑才走得到（否則退貨幾乎都落在未收款發票上）
                        var paidCands = cands.Where(s => status[s.InvId] != ARInvoiceStatus.Open && !m.Invs[s.InvId].Void).ToList();
                        if (paidCands.Count > 0 && rng.NextDouble() < 0.5) cands = paidCands;
                        var s = cands[rng.Next(cands.Count)];
                        var max = m.Returnable(s);
                        var q = rng.NextDouble() < 0.1 ? max + 1 : Math.Max(1, Math.Floor(max * (decimal)rng.NextDouble()));
                        var inv = m.Invs[s.InvId];
                        label = $"銷退 {s.Id} ×{q}（可退 {max}）";
                        act = () => ReturnAsync(s.Id, q);
                        expectAndCommit = o =>
                        {
                            var shouldAccept = q <= max;
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "退貨量超過可退量卻被接受";
                            if (!o.Accepted) return null;
                            var ret = new Ret { Id = o.Detail, Gs = s.Id, Qty = q };
                            m.Rets.Add(ret);
                            if (inv.Void) return null;                        // 發票已作廢：應收沒有東西可沖
                            if (status[inv.Id] == ARInvoiceStatus.Open)       // 未收款：直接把發票改小
                            {
                                inv.Total -= q * Gross; ret.ReducedInvoice = q * Gross;
                            }
                            else                                              // 已（部分）收款：開折讓單草稿
                            {
                                ret.NoteId = "?";                            // 實際單號在 commit 後由資料庫補
                            }
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
                        act = () => Try(scope => Svc<IARCreditNoteService>(scope).ConfirmAsync(n.Id));
                        expectAndCommit = o =>
                        {
                            var shouldAccept = !inv.Void;
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "發票已作廢卻仍可確認折讓";
                            if (o.Accepted) { n.Posted = true; inv.Credited += n.Gross; }
                            return null;
                        };
                        break;
                    }
                    case "作廢收款":
                    {
                        var live = m.Rcpts.Where(r => !r.Void).ToList();
                        if (live.Count == 0) continue;
                        var r = live[rng.Next(live.Count)];
                        label = $"作廢收款 {r.Id}（{r.InvId} {r.Amt}）";
                        act = () => Try(scope => Svc<IFinPaymentService>(scope).VoidARReceiptAsync(r.Id));
                        // 多收退款後再作廢原收款，已收會變負數：要先作廢退款那張
                        var rInv = m.Invs[r.InvId];
                        var mustReject = r.Amt > 0m && rInv.Received - r.Amt < 0m;
                        expectAndCommit = o =>
                        {
                            if (o.Accepted == mustReject)
                                return mustReject ? "已退款卻可作廢原收款（已收會變負數）" : $"應接受卻被拒絕：{o.Detail}";
                            if (!o.Accepted) return null;
                            r.Void = true; m.Invs[r.InvId].Received -= r.Amt;
                            return null;
                        };
                        break;
                    }
                    case "作廢發票":
                    {
                        var live = m.Invs.Values.Where(i => !i.Void && (i.Credited == 0m || !Skip.Contains("作廢有折讓的發票"))).ToList();
                        if (live.Count == 0) continue;
                        var inv = live[rng.Next(live.Count)];
                        // 收過款就得先沖收款單；全額折讓後狀態會是「已收」，同樣擋
                        // 折讓單有自己的分錄，作廢發票只沖發票那幾張；已確認的折讓不先作廢，總帳就留一筆沖不掉的折讓
                        var shouldAccept = inv.Received == 0m && inv.Credited == 0m && status[inv.Id] == ARInvoiceStatus.Open;
                        label = $"作廢發票 {inv.Id}（{status[inv.Id]}、已收 {inv.Received}、已折讓 {inv.Credited}）";
                        act = () => Try(scope => Svc<IARInvoiceService>(scope).VoidAsync(inv.Id));
                        expectAndCommit = o =>
                        {
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : (inv.Credited != 0m ? "有已確認折讓的發票卻可作廢" : "已收款發票卻可作廢");
                            if (o.Accepted) inv.Void = true;
                            return null;
                        };
                        break;
                    }
                    case "作廢銷退":
                    {
                        var live = m.Rets.Where(r => !r.Void).ToList();
                        if (live.Count == 0) continue;
                        var r = live[rng.Next(live.Count)];
                        var inv = m.Invs[m.Ships.Single(s => s.Id == r.Gs).InvId];
                        label = $"作廢銷退 {r.Id}（{r.Gs} ×{r.Qty}，當初回沖發票 {r.ReducedInvoice}、折讓單 {r.NoteId ?? "-"}）";
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
                                if (n.Posted && !inv.Void) inv.Credited -= n.Gross;
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
                        act = () => Try(scope => Svc<IARCreditNoteService>(scope).VoidAsync(n.Id, "序列測試"));
                        expectAndCommit = o =>
                        {
                            if (!o.Accepted) return $"應接受卻被拒絕：{o.Detail}";
                            if (n.Posted && !inv.Void) inv.Credited -= n.Gross;
                            n.Void = true;
                            if (m.Rets.Any(r => r.NoteId == n.Id && !r.Void) && !inv.Void)
                                ExpectInvariantViolation("RETURN_SUBLEDGER_UNREVERSED",
                                    "序列中作廢了退貨仍有效的折讓單：退貨的應收從此沒沖，要重開折讓單才會消失，稽核照實報它");
                            return null;
                        };
                        break;
                    }
                    case "作廢出貨":
                    {
                        var live = m.Ships.Where(s => !s.Void).ToList();
                        if (live.Count == 0) continue;
                        var s = live[rng.Next(live.Count)];
                        var inv = m.Invs[s.InvId];
                        var activeRets = m.Rets.Count(r => r.Gs == s.Id && !r.Void);
                        label = $"作廢出貨 {s.Id}（發票 {inv.Id}{(inv.Void ? " 已作廢" : $" 未作廢、已收 {inv.Received}")}、有效退貨 {activeRets} 張）";
                        act = () => VoidShipmentAsync(s.Id);
                        expectAndCommit = o =>
                        {
                            // 發票還活著、或還有未作廢的退貨就擋：作廢只沖庫存，應收應付與退貨不會跟著走
                            var shouldAccept = inv.Void && !m.Rets.Any(x => x.Gs == s.Id && !x.Void);
                            if (o.Accepted != shouldAccept)
                                return shouldAccept ? $"應接受卻被拒絕：{o.Detail}" : "發票未作廢或有有效退貨，作廢卻被放行";
                            if (!o.Accepted) return null;
                            s.Void = true;
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

            // 收尾：還在草稿的折讓單確認掉（流程的最後一步），再驗十項帳務不變量（含總帳存貨＝明細帳）
            foreach (var n in m.Notes.Values.Where(n => !n.Posted && !n.Void && !m.Invs[n.InvId].Void).ToList())
            {
                var o = await Try(scope => Svc<IARCreditNoteService>(scope).ConfirmAsync(n.Id));
                history.Add($"收尾 確認折讓 {n.Id} → {(o.Accepted ? "接受" : $"拒絕：{o.Detail}")}");
                Assert.True(o.Accepted, Fail(steps, $"收尾確認折讓被拒絕：{o.Detail}"));
                n.Posted = true; m.Invs[n.InvId].Credited += n.Gross;
            }
            var last = await CheckAsync(m);
            Assert.True(last == null, Fail(steps, last ?? ""));
            try { await AssertLedgerInvariantsAsync(); }
            catch (Xunit.Sdk.XunitException ex) { Assert.Fail(Fail(steps, ex.Message)); }
        }

        /// <summary>
        /// 出貨已過帳後作廢發票，依來源重新開立（2026-10-22）：新發票＝出貨量 − 已過帳退貨量，
        /// 「出貨未開立應收發票」稽核清掉、銷貨收入回到實際賣出的量；已有未作廢發票時不得再開。
        /// </summary>
        [Fact]
        public async Task 作廢發票後依來源重開_開票量扣已過帳退貨且稽核清除()
        {
            await SetSimplifiedAsync("true");
            try
            {
                var ship = await ShipAsync(10m);
                Assert.True(ship.Accepted, ship.Detail);
                var (gs, inv) = SplitPair(ship.Detail);
                Assert.True((await ReturnAsync(gs, 3m)).Accepted);              // 未收款：發票直接改小成 7
                Assert.False((await Try(scope => Svc<IARInvoiceService>(scope).ReissueFromSourceAsync(inv))).Accepted);   // 未作廢不得重開
                Assert.True((await Try(scope => Svc<IARInvoiceService>(scope).VoidAsync(inv))).Accepted);

                string newId;
                using (var scope = NewScope(out var db))
                    newId = (await Svc<IARInvoiceService>(scope).ReissueFromSourceAsync(inv)).ARInvoiceId;
                Assert.NotEqual(inv, newId);
                Assert.False((await Try(scope => Svc<IARInvoiceService>(scope).ReissueFromSourceAsync(inv))).Accepted);   // 已有新發票

                using (var scope = NewScope(out var db))
                {
                    var n = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.ARInvoiceId == newId);
                    Assert.Equal(7m * Gross, n.TotalAmount);
                    var gl = await GlNetByAccountAsync(db, Plant);
                    Assert.Equal(-7m * Price, gl.GetValueOrDefault(StandardAccounts.SalesRevenueCode));
                    Assert.Empty((await Svc<SmartFactory.MES.Modules.ACT.Services.IDataAuditService>(scope).AuditShipmentInvoiceGapAsync(Plant)).Where(f => f.DocId == gs));
                }
            }
            finally { await SetSimplifiedAsync(null); }
        }

        private static string Pick(Random rng, (string Kind, double Weight)[] kinds)
        {
            var x = rng.NextDouble() * kinds.Sum(k => k.Weight);
            foreach (var k in kinds) { if ((x -= k.Weight) < 0) return k.Kind; }
            return kinds[^1].Kind;
        }

        private static (string, string) SplitPair(string s) { var p = s.Split('|'); return (p[0], p[1]); }

        private async Task<Dictionary<string, ARInvoiceStatus>> InvoiceStatusesAsync()
        {
            using var scope = NewScope(out var db);
            return await db.ARInvoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.PlantId == Plant)
                .ToDictionaryAsync(i => i.ARInvoiceId, i => i.Status);
        }

        /// <summary>銷退開出來的折讓單號只有資料庫知道：把「?」換成真單號，並記下它的含稅金額。</summary>
        private async Task SyncNotesAsync(Model m)
        {
            var pending = m.Rets.Where(r => r.NoteId == "?").ToList();
            if (pending.Count == 0) return;
            using var scope = NewScope(out var db);
            foreach (var r in pending)
            {
                var note = await db.ARCreditNotes.IgnoreQueryFilters().AsNoTracking().Include(n => n.Lines)
                    .FirstOrDefaultAsync(n => n.SourceDocType == "SalesReturn" && n.SourceDocId == r.Id);
                if (note == null) { r.NoteId = null; continue; }
                r.NoteId = note.ARCreditNoteId;
                Assert.True(note.TotalAmount == r.Qty * Gross,
                    $"銷退 {r.Id} ×{r.Qty} 開出的折讓單 {note.ARCreditNoteId} 金額 {note.TotalAmount}，應為 {r.Qty * Gross}");
                var invId = note.Lines.Select(l => l.ARInvoiceId).Distinct().Single();
                m.Notes[note.ARCreditNoteId] = new Note { Id = note.ARCreditNoteId, InvId = invId, Gross = note.TotalAmount };
            }
        }

        /// <summary>每一步之後：模型 vs 資料庫、子帳 vs 總帳、總帳 vs 經濟事實。不一致回傳說明。</summary>
        private async Task<string?> CheckAsync(Model m)
        {
            using var scope = NewScope(out var db);
            var invs = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.PlantId == Plant).ToListAsync();
            foreach (var mi in m.Invs.Values)
            {
                var di = invs.SingleOrDefault(i => i.ARInvoiceId == mi.Id);
                if (di == null) return $"發票 {mi.Id} 不見了";
                if ((di.Status == ARInvoiceStatus.Void) != mi.Void) return $"發票 {mi.Id} 作廢狀態 {di.Status}，模型 {(mi.Void ? "已作廢" : "未作廢")}";
                if (di.TotalAmount != mi.Total) return $"發票 {mi.Id} 總額 {di.TotalAmount}，模型 {mi.Total}";
                if (di.ReceivedAmount != mi.Received) return $"發票 {mi.Id} 已收 {di.ReceivedAmount}，模型 {mi.Received}";
                if (di.CreditedAmount != mi.Credited) return $"發票 {mi.Id} 已折讓 {di.CreditedAmount}，模型 {mi.Credited}";
            }

            var gl = await GlNetByAccountAsync(db, Plant);
            decimal G(string code) => gl.GetValueOrDefault(code);
            if (_fx) return await CheckFxAsync(m, db, invs, G);

            var subledger = invs.Where(i => i.Status != ARInvoiceStatus.Void).Sum(i => i.TotalAmount - i.ReceivedAmount - i.CreditedAmount);
            if (G(StandardAccounts.AccountsReceivableCode) != subledger)
                return $"總帳應收 {G(StandardAccounts.AccountsReceivableCode)} ≠ 應收子帳 {subledger}";

            var bank = await db.ARReceipts.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.PlantId == Plant && r.Status == ARReceiptStatus.Received).SumAsync(r => (decimal?)r.AmountLocal) ?? 0m;
            if (G(StandardAccounts.CashAndBankCode) != bank)
                return $"總帳銀行 {G(StandardAccounts.CashAndBankCode)} ≠ 已確認收款 {bank}";

            var sold = m.SoldQty;
            if (-G(StandardAccounts.SalesRevenueCode) != sold * Price)
                return $"銷貨收入 {-G(StandardAccounts.SalesRevenueCode)} ≠ 實際賣出 {sold} × {Price} = {sold * Price}";
            if (-G(StandardAccounts.OutputVatCode) != sold * Price * TaxRate)
                return $"銷項稅額 {-G(StandardAccounts.OutputVatCode)} ≠ 實際賣出 {sold} × {Price * TaxRate} = {sold * Price * TaxRate}";
            return null;
        }

        /// <summary>
        /// 外幣情境的本幣檢查（容許四捨五入）：發票匯率＝出貨日賣匯；應收總帳＝未結外幣 × 發票匯率；
        /// 收入與銷項稅＝實際賣出 × 價 × 發票匯率；銀行＝收款外幣 × 收款日賣匯；匯兌損益＝核銷外幣 ×（收款匯率 − 發票匯率）。
        /// </summary>
        private async Task<string?> CheckFxAsync(Model m, ApplicationDbContext db, List<ARInvoice> invs, Func<string, decimal> G)
        {
            const decimal tol = 1m;
            foreach (var s0 in m.Ships)
            {
                var di = invs.Single(i => i.ARInvoiceId == s0.InvId);
                var shipDate = await db.GoodsShipments.IgnoreQueryFilters().AsNoTracking().Where(g => g.GoodsShipmentId == s0.Id).Select(g => g.ShipDate).SingleAsync();
                if (di.CurrencyCode != Fx || di.ExchangeRate != SellRate(shipDate))
                    return $"發票 {di.ARInvoiceId} 幣別 {di.CurrencyCode} 匯率 {di.ExchangeRate}，應為 {Fx} @出貨日賣匯 {SellRate(shipDate)}";
            }
            var rate = invs.ToDictionary(i => i.ARInvoiceId, i => i.ExchangeRate);

            var arExpected = invs.Where(i => i.Status != ARInvoiceStatus.Void).Sum(i => (i.TotalAmount - i.ReceivedAmount - i.CreditedAmount) * i.ExchangeRate);
            if (Math.Abs(G(StandardAccounts.AccountsReceivableCode) - arExpected) > tol)
                return $"總帳應收（本幣）{G(StandardAccounts.AccountsReceivableCode)} ≠ 未結外幣 × 發票匯率 {arExpected}";

            decimal revenue = 0m;
            foreach (var s0 in m.Ships.Where(x => !x.Void && !m.Invs[x.InvId].Void))
            {
                var sold = s0.Qty - m.Rets.Where(r => r.Gs == s0.Id && !r.Void && m.Reflected(r)).Sum(r => r.Qty);
                revenue += sold * Price * rate[s0.InvId];
            }
            if (Math.Abs(-G(StandardAccounts.SalesRevenueCode) - revenue) > tol)
                return $"銷貨收入（本幣）{-G(StandardAccounts.SalesRevenueCode)} ≠ 實際賣出 × 價 × 發票匯率 {revenue}";
            if (Math.Abs(-G(StandardAccounts.OutputVatCode) - revenue * TaxRate) > tol)
                return $"銷項稅額（本幣）{-G(StandardAccounts.OutputVatCode)} ≠ {revenue * TaxRate}";

            var rcpts = await db.ARReceipts.IgnoreQueryFilters().AsNoTracking().Include(r => r.Allocations)
                .Where(r => r.PlantId == Plant && r.Status == ARReceiptStatus.Received).ToListAsync();
            foreach (var r in rcpts)
                if (r.ExchangeRate != SellRate(r.ReceiptDate) || Math.Abs(r.AmountLocal - r.Amount * r.ExchangeRate) > 0.5m)
                    return $"收款 {r.ARReceiptId} 匯率 {r.ExchangeRate}／本幣 {r.AmountLocal}，應為 收款日賣匯 {SellRate(r.ReceiptDate)}／{r.Amount * SellRate(r.ReceiptDate)}";
            var bank = rcpts.Sum(r => r.AmountLocal);
            if (Math.Abs(G(StandardAccounts.CashAndBankCode) - bank) > tol)
                return $"總帳銀行 {G(StandardAccounts.CashAndBankCode)} ≠ 已確認收款本幣 {bank}";

            var fxExpected = rcpts.SelectMany(r => r.Allocations.Select(a => a.AllocatedAmount * (r.ExchangeRate - rate[a.ARInvoiceId]))).Sum();
            var fxActual = -(G(StandardAccounts.FxGainCode) + G(StandardAccounts.FxLossCode));
            if (Math.Abs(fxActual - fxExpected) > tol)
                return $"匯兌損益淨額 {fxActual} ≠ 核銷外幣 ×（收款匯率 − 發票匯率）{fxExpected}";
            return null;
        }

        // ── 操作（走真入口） ─────────────────────────────────────────────

        private async Task<HashSet<string>> IdsAsync<T>(Func<ApplicationDbContext, IQueryable<string>> q)
        {
            using var scope = NewScope(out var db);
            return (await q(db).ToListAsync()).ToHashSet();
        }

        private async Task<Outcome> ShipAsync(decimal q)
        {
            // 存檔前後比對多出來的那一張——CreatedAt 同一毫秒內會排錯
            var before = await IdsAsync<GoodsShipment>(db => db.GoodsShipments.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsShipmentId));
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewShipmentController(scope, db);
                var vm = new GoodsShipmentFormVm
                {
                    ShipDate = _date, CompanyCode = Customer, SalesOrderId = So, CreatedBy = UserId,
                    Lines =
                    {
                        new GoodsShipmentLineVm
                        {
                            LineNo = 1, SalesOrderId = So, SoLineNo = 1, ProductCode = Fg, ShipQty = q,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = Price, CurrencyCode = _fx ? Fx : null, ExchangeRate = 1m, TaxRate = TaxRate,
                            LineAmount = q * Price, TaxAmount = q * Price * TaxRate, TotalAmount = q * Gross,
                        },
                    },
                };
                var saved = await ctrl.Save(vm);
                if (saved is not ContentResult) return new(false, ModelErrors(ctrl));
            }
            var gs = (await IdsAsync<GoodsShipment>(db => db.GoodsShipments.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.GoodsShipmentId)))
                .Except(before).Single();
            using (var scope = NewScope(out var db))
            {
                var r = await NewShipmentController(scope, db).Confirm(gs);
                if (r is not OkObjectResult) return new(false, $"確認：{(r as ObjectResult)?.Value}");
            }
            if (_full && !(await PickAsync("GS", gs, q)).Accepted) return new(false, "完整模式揀貨失敗");
            using (var scope = NewScope(out var db))
            {
                var inv = await db.ARInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.SourceDocType == "GoodsShipment" && i.SourceDocId == gs);
                return new(true, $"{gs}|{inv.ARInvoiceId}");
            }
        }

        private async Task<Outcome> ReceiveAsync(string invId, decimal amt)
        {
            try
            {
                using var scope = NewScope(out var db);
                var fin = Svc<IFinPaymentService>(scope);
                // 外幣收款：表單預設帶入當日賣匯（送入值優先），這裡照樣帶
                var rec = await fin.CreateARReceiptAsync(new ARReceiptRequest(Customer, _date, PaymentMethod.Wire,
                    _fx ? Fx : null, _fx ? SellRate(_date) : 1m, amt, Bank, null, null,
                    new List<PaymentAllocationLine> { new(invId, amt) }), UserId, Plant);
                await fin.ConfirmARReceiptAsync(rec.ARReceiptId);
                return new(true, rec.ARReceiptId);
            }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }

        private async Task<Outcome> ReturnAsync(string gs, decimal q)
        {
            var before = await IdsAsync<SalesReturn>(db => db.SalesReturns.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId));
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewReturnController(scope, db);
                var vm = new SalesReturnFormVm
                {
                    ReturnDate = _date, CompanyCode = Customer, GoodsShipmentId = gs, CreatedBy = UserId,
                    Lines =
                    {
                        new SalesReturnLineVm
                        {
                            LineNo = 1, GoodsShipmentId = gs, ShipmentLineNo = 1, ProductCode = Fg, ReturnQty = q,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = Price, CurrencyCode = _fx ? Fx : null, ExchangeRate = 1m, TaxRate = TaxRate,
                            LineAmount = q * Price, TaxAmount = q * Price * TaxRate, TotalAmount = q * Gross,
                        },
                    },
                };
                var saved = await ctrl.Save(vm);
                if (saved is not ContentResult) return new(false, ModelErrors(ctrl));
            }
            var sr = (await IdsAsync<SalesReturn>(db => db.SalesReturns.IgnoreQueryFilters().Where(d => d.PlantId == Plant).Select(d => d.SalesReturnId)))
                .Except(before).Single();
            using (var scope = NewScope(out var db))
            {
                var r = await NewReturnController(scope, db).Confirm(sr);
                if (r is not OkObjectResult)
                {
                    // 確認被擋：把草稿刪掉，否則它會一直佔著可退量
                    await db.SalesReturns.IgnoreQueryFilters().Where(d => d.SalesReturnId == sr).ExecuteDeleteAsync();
                    return new(false, $"確認：{(r as ObjectResult)?.Value}");
                }
            }
            if (_full)
            {
                var put = await PutAwayAsync("GR", sr, q);   // 完整模式：銷退上架時才過帳並回沖應收
                if (!put.Accepted) return put;
            }
            return new(true, sr);
        }

        private async Task<Outcome> VoidReturnAsync(string sr)
        {
            using var scope = NewScope(out var db);
            var r = await NewReturnController(scope, db).Void(sr, "序列測試");
            return r is OkObjectResult ? new(true, "") : new(false, $"{(r as ObjectResult)?.Value}");
        }

        private async Task<Outcome> VoidShipmentAsync(string gs)
        {
            using var scope = NewScope(out var db);
            var r = await NewShipmentController(scope, db).Void(gs, "序列測試");
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


        private GoodsShipmentController NewShipmentController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new GoodsShipmentController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IInventoryService>(), sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<ICurrencyService>(), sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IARInvoiceService>(), sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IInventoryReservationService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }

        private SalesReturnController NewReturnController(IServiceScope scope, ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new SalesReturnController(
                db, sp.GetRequiredService<IUserContext>(), sp.GetRequiredService<IPermissionService>(),
                sp.GetRequiredService<IDateTimeService>(), sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDocNumberService>(), sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IUomService>(), sp.GetRequiredService<ICurrencyService>(),
                sp.GetRequiredService<IPlantParamService>(), sp.GetRequiredService<ISamplingPlanService>(),
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IARInvoiceService>(), sp.GetRequiredService<IARCreditNoteService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = sp } },
            };
        }
    }
}
