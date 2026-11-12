using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 月結隨機序列（2026-10-24）：外幣應收／應付開票、部分收付款、日期推進，穿插月結
    /// （外幣重評＋ECL＋步驟 9 入帳→開始月結→關帳→開下一期）、反關帳上一期後重做月結、
    /// 對已關帳期間開單（必須擋下）。
    ///
    /// <para><b>每一步都檢查</b>：
    /// ① 應收／應付控制科目「全部分錄」＝未結 × 入帳匯率（期末重評＋次期初迴轉必須淨零）；
    /// ② 每個已月結期間：只計 ≤ 期末日的分錄，控制科目＝期末當時未結 × 期末匯率；
    /// ③ 匯兌損益總額＝只有已實現部分；
    /// ④ 每個已月結期間：ECL 評估的應收曝險＝期末當時未結本幣，備抵科目期末餘額＝評估額；
    /// ⑤ 重跑步驟 9 不產生新分錄；每張傳票平衡。</para>
    ///
    /// <para>規模：MEM_MODEL_SEEDS（預設 12）、MEM_MODEL_STEPS（預設 40）。</para>
    /// </summary>
    [Collection("Integration")]
    public class MonthEndLedgerModelTests : FlowTestBase
    {
        protected override string Plant => "ZZMEMODEL";

        private const string Fx = "ZZM";
        private const string RevAcc = "4990", ExpAcc = "6990";
        private static readonly DateOnly First = new(2097, 1, 1);
        private static decimal BuyRate(DateOnly d) => 30m + (d.DayNumber * 7 % 13) * 0.2m;
        private static decimal SellRate(DateOnly d) => BuyRate(d) + 0.3m;

        private readonly ITestOutputHelper _out;

        public MonthEndLedgerModelTests(TestWebApplicationFactory factory, ITestOutputHelper output) : base(factory)
        {
            _out = output;
            ResetAllAsync().GetAwaiter().GetResult();
        }

        private async Task ResetAllAsync()
        {
            await ResetAsync();
            using var scope = NewScope(out var db);
            await db.FxRevaluationEntries.IgnoreQueryFilters().Where(e => e.PlantId == Plant).ExecuteDeleteAsync();
            await db.EclAllowances.IgnoreQueryFilters().Where(e => e.PlantId == Plant).ExecuteDeleteAsync();
            await db.AccountingPeriods.IgnoreQueryFilters().Where(p => p.PlantId == Plant).ExecuteDeleteAsync();
            db.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId = "209701", PlantId = Plant, Year = 2097, Month = 1,
                StartDate = First, EndDate = new DateOnly(2097, 1, 31), Status = PeriodStatus.Open,
            });
            foreach (var (code, name, type) in new[] { (RevAcc, "月結序列收入", AccountType.Revenue), (ExpAcc, "月結序列費用", AccountType.OperatingExpense) })
                if (!await db.Set<ChartOfAccount>().AnyAsync(a => a.AccountCode == code))
                    db.Set<ChartOfAccount>().Add(new ChartOfAccount { AccountCode = code, AccountName = name, AccountType = type, AllowManualEntry = true, IsActive = true });
            if (!await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == Fx))
                db.Currencies.Add(new Currency { CurrencyCode = Fx, CurrencyName = "月結序列外幣", Symbol = "M$", DecimalPlaces = 2, IsActive = true });
            var have = (await db.CurrencyRates.AsNoTracking().Where(r => r.CurrencyCode == Fx).Select(r => r.RateDate).ToListAsync()).ToHashSet();
            for (var d = First; d <= First.AddDays(400); d = d.AddDays(1))
                if (!have.Contains(d))
                    db.CurrencyRates.Add(new CurrencyRate { CurrencyCode = Fx, RateDate = d, BuyRate = BuyRate(d), SellRate = SellRate(d) });
            await db.SaveChangesAsync();
            Svc<ICurrencyService>(scope).InvalidateCache();
        }

        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        // ── 參考模型 ─────────────────────────────────────────────────────

        private sealed class Inv
        {
            public string Id = "";
            public bool Ar, Fx;
            public DateOnly Date;
            public decimal Rate, Total;
            public readonly List<(DateOnly Date, decimal Amt, decimal Rate)> Settled = new();
            public decimal OpenAt(DateOnly d) => Total - Settled.Where(s => s.Date <= d).Sum(s => s.Amt);
            public decimal Open => Total - Settled.Sum(s => s.Amt);
        }

        private sealed class Period
        {
            public string Id = "";
            public DateOnly Start, End;
            public bool Closed, MonthEnded;
        }

        [Fact]
        public async Task 隨機開票收付與月結反關帳_每一步控制科目重評迴轉匯損與備抵都與參考模型一致()
        {
            var seeds = Env("MEM_MODEL_SEEDS", 12);
            var steps = Env("MEM_MODEL_STEPS", 40);
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
            var invs = new List<Inv>();
            var periods = new List<Period> { new() { Id = "209701", Start = First, End = new DateOnly(2097, 1, 31) } };
            var cur = periods[0];
            var date = First.AddDays(rng.Next(0, 5));
            var history = new List<string>();
            string Fail(int step, string why) => $"種子 {seed} 第 {step} 步：{why}\n序列：\n  " + string.Join("\n  ", history);

            for (var step = 1; step <= steps; step++)
            {
                var open = invs.Where(i => i.Open > 0m).ToList();
                var roll = rng.NextDouble();
                var kind = roll < 0.18 ? "開應收" : roll < 0.32 ? "開應付" : roll < 0.46 ? "收款" : roll < 0.58 ? "付款"
                    : roll < 0.76 ? "推進日期" : roll < 0.88 ? "月結" : roll < 0.94 ? "反關帳重做" : "對已關帳期間開單";
                if (kind == "收款" && !open.Any(i => i.Ar)) kind = "開應收";
                if (kind == "付款" && !open.Any(i => !i.Ar)) kind = "開應付";
                var closedPrev = periods.LastOrDefault(p => p.Closed);
                if (kind is "反關帳重做" or "對已關帳期間開單" && closedPrev == null) kind = "推進日期";

                string label; string? mismatch = null; var accepted = true;
                switch (kind)
                {
                    case "開應收":
                    case "開應付":
                    {
                        var ar = kind == "開應收";
                        var fx = rng.NextDouble() < 0.75;
                        var amt = rng.Next(10, 400) * 5m;
                        var rate = fx ? (ar ? SellRate(date) : BuyRate(date)) : 1m;
                        label = $"{kind} {date:MM-dd} {amt}{(fx ? $" {Fx}@{rate}" : "")}";
                        var o = await InvoiceAsync(ar, date, fx, rate, amt);
                        if (o.Error != null) mismatch = $"開票被拒絕：{o.Error}";
                        else invs.Add(new Inv { Id = o.Id!, Ar = ar, Fx = fx, Date = date, Rate = rate, Total = amt });
                        break;
                    }
                    case "收款":
                    case "付款":
                    {
                        var ar = kind == "收款";
                        var cands = open.Where(i => i.Ar == ar).ToList();
                        var inv = cands[rng.Next(cands.Count)];
                        var amt = rng.NextDouble() < 0.4 ? inv.Open : Math.Max(5m, Math.Round(inv.Open * (decimal)rng.NextDouble() / 5m) * 5m);
                        amt = Math.Min(amt, inv.Open);
                        var rate = inv.Fx ? (ar ? SellRate(date) : BuyRate(date)) : 1m;
                        label = $"{kind} {inv.Id} {date:MM-dd} {amt}{(inv.Fx ? $" @{rate}" : "")}";
                        var err = await SettleAsync(ar, inv, date, rate, amt);
                        if (err != null) mismatch = $"{kind}被拒絕：{err}";
                        else inv.Settled.Add((date, amt, rate));
                        break;
                    }
                    case "推進日期":
                    {
                        var to = date.AddDays(rng.Next(1, 9));
                        if (to > cur.End) to = cur.End;
                        label = $"推進日期 {date:MM-dd}→{to:MM-dd}";
                        date = to;
                        break;
                    }
                    case "月結":
                    {
                        label = $"月結 {cur.Id}";
                        date = cur.End;
                        var err = await MonthEndAsync(cur, rerunCheck: rng.NextDouble() < 0.5);
                        if (err != null) { mismatch = err; break; }
                        cur.Closed = cur.MonthEnded = true;
                        var next = await NextPeriodAsync();
                        periods.Add(next);
                        cur = next;
                        date = next.Start.AddDays(rng.Next(0, 3));
                        break;
                    }
                    case "反關帳重做":
                    {
                        var p = closedPrev!;
                        label = $"反關帳 {p.Id} 後重做月結";
                        var err = await ReopenAndRedoAsync(p);
                        if (err != null) mismatch = err;
                        break;
                    }
                    default: // 對已關帳期間開單
                    {
                        var p = closedPrev!;
                        var d = p.End.AddDays(-rng.Next(0, 10));
                        label = $"對已關帳期間 {p.Id} 開單 {d:MM-dd}";
                        var o = await InvoiceAsync(true, d, false, 1m, 100m);   // 擋下才是對的（計入統計）
                        if (o.Error == null) mismatch = $"已關帳期間 {p.Id} 仍可開單 {o.Id}";
                        break;
                    }
                }

                stats[kind] = stats.GetValueOrDefault(kind) + (accepted ? 1 : 0);
                history.Add($"{step}. {label}");
                mismatch ??= await CheckAsync(invs, periods.Where(p => p.MonthEnded).ToList());
                if (mismatch != null) Assert.Fail(Fail(step, mismatch));
            }

            await AssertLedgerInvariantsAsync();
        }

        // ── 檢查 ─────────────────────────────────────────────────────────

        private async Task<string?> CheckAsync(List<Inv> invs, List<Period> monthEnded)
        {
            using var scope = NewScope(out var db);
            var lines = await db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.JournalEntry!.PlantId == Plant)
                .Select(l => new { l.JournalEntry!.JournalId, l.JournalEntry.EntryDate, l.AccountCode, Net = l.Debit - l.Credit })
                .ToListAsync();
            var unbalanced = lines.GroupBy(l => l.JournalId).FirstOrDefault(g => Math.Abs(g.Sum(x => x.Net)) > 0.0001m);
            if (unbalanced != null) return $"傳票 {unbalanced.Key} 借貸不平 {unbalanced.Sum(x => x.Net)}";

            decimal Gl(string acc, DateOnly? asOf = null) =>
                lines.Where(l => l.AccountCode == acc && (asOf == null || l.EntryDate <= asOf)).Sum(l => l.Net);
            decimal Tol(int n) => 0.02m + 0.01m * n;

            // ① 全部分錄：控制科目＝未結 × 入帳匯率
            var arBook = invs.Where(i => i.Ar).Sum(i => i.Open * i.Rate);
            var apBook = invs.Where(i => !i.Ar).Sum(i => i.Open * i.Rate);
            if (Math.Abs(Gl(StandardAccounts.AccountsReceivableCode) - arBook) > Tol(invs.Count))
                return $"應收控制科目 {Gl(StandardAccounts.AccountsReceivableCode):0.####} ≠ 未結×入帳匯率 {arBook:0.####}（重評與迴轉沒有淨零？）";
            if (Math.Abs(-Gl(StandardAccounts.AccountsPayableCode) - apBook) > Tol(invs.Count))
                return $"應付控制科目 {-Gl(StandardAccounts.AccountsPayableCode):0.####} ≠ 未結×入帳匯率 {apBook:0.####}";

            // ③ 匯兌損益只剩已實現
            var realized = invs.Sum(i => i.Settled.Sum(s => s.Amt * (i.Ar ? s.Rate - i.Rate : i.Rate - s.Rate)));
            var fxNet = -(Gl(StandardAccounts.FxGainCode) + Gl(StandardAccounts.FxLossCode));
            if (Math.Abs(fxNet - realized) > Tol(invs.Sum(i => i.Settled.Count)))
                return $"匯兌損益淨額 {fxNet:0.####} ≠ 已實現 {realized:0.####}";

            foreach (var p in monthEnded)
            {
                var endRate = BuyRate(p.End);
                // ② 期末當時：控制科目＝期末未結 × 期末匯率（外幣）／入帳匯率（本幣）
                var arEnd = invs.Where(i => i.Ar && i.Date <= p.End).Sum(i => i.OpenAt(p.End) * (i.Fx ? endRate : i.Rate));
                var apEnd = invs.Where(i => !i.Ar && i.Date <= p.End).Sum(i => i.OpenAt(p.End) * (i.Fx ? endRate : i.Rate));
                var n = invs.Count(i => i.Date <= p.End);
                if (Math.Abs(Gl(StandardAccounts.AccountsReceivableCode, p.End) - arEnd) > Tol(n))
                    return $"{p.Id} 期末應收 {Gl(StandardAccounts.AccountsReceivableCode, p.End):0.####} ≠ 期末未結×期末匯率 {arEnd:0.####}";
                if (Math.Abs(-Gl(StandardAccounts.AccountsPayableCode, p.End) - apEnd) > Tol(n))
                    return $"{p.Id} 期末應付 {-Gl(StandardAccounts.AccountsPayableCode, p.End):0.####} ≠ 期末未結×期末匯率 {apEnd:0.####}";

                // ④ ECL：曝險＝期末當時未結本幣（入帳匯率）；備抵期末餘額＝評估額
                var ecl = await db.EclAllowances.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.PlantId == Plant && e.PeriodId == p.Id).ToListAsync();
                var exposure = invs.Where(i => i.Ar && i.Date <= p.End).Sum(i => Math.Max(0m, i.OpenAt(p.End) * i.Rate));
                if (Math.Abs(ecl.Sum(e => e.ArBalance) - exposure) > Tol(n))
                    return $"{p.Id} ECL 曝險 {ecl.Sum(e => e.ArBalance):0.####} ≠ 期末當時未結 {exposure:0.####}";
                var allowance = -Gl(StandardAccounts.ReceivableLossAllowanceCode, p.End);
                if (Math.Abs(allowance - ecl.Sum(e => e.EclAmount)) > 0.01m)
                    return $"{p.Id} 備抵期末餘額 {allowance:0.####} ≠ ECL 評估 {ecl.Sum(e => e.EclAmount):0.####}";
            }
            return null;
        }

        // ── 操作 ─────────────────────────────────────────────────────────

        private sealed record Created(string? Id, string? Error);

        private async Task<Created> InvoiceAsync(bool ar, DateOnly date, bool fx, decimal rate, decimal amt)
        {
            try
            {
                using var scope = NewScope(out var db);
                var local = Math.Round(amt * rate, 2, MidpointRounding.AwayFromZero);
                if (ar)
                {
                    var inv = new ARInvoice
                    {
                        CustomerCode = Customer, PlantId = Plant, InvoiceDate = date, DueDate = date.AddDays(30),
                        CurrencyCode = fx ? Fx : null, ExchangeRate = rate, TotalAmount = amt, AmountLocal = local,
                    };
                    inv.Lines.Add(new ARInvoiceLine { LineNo = 1, AccountCode = RevAcc, Qty = 1m, UnitPrice = amt, LineAmount = amt });
                    var c = await Svc<IARInvoiceService>(scope).CreateManualAsync(inv, DocumentApprovalStatus.Approved);
                    return new(c.ARInvoiceId, null);
                }
                else
                {
                    var inv = new APInvoice
                    {
                        SupplierCode = Supplier, PlantId = Plant, InvoiceDate = date, DueDate = date.AddDays(30),
                        CurrencyCode = fx ? Fx : null, ExchangeRate = rate, TotalAmount = amt, AmountLocal = local,
                    };
                    inv.Lines.Add(new APInvoiceLine { LineNo = 1, AccountCode = ExpAcc, Qty = 1m, UnitPrice = amt, LineAmount = amt });
                    var c = await Svc<IAPInvoiceService>(scope).CreateManualAsync(inv, DocumentApprovalStatus.Approved);
                    return new(c.APInvoiceId, null);
                }
            }
            catch (InvalidOperationException ex) { return new(null, ex.Message); }
        }

        private async Task<string?> SettleAsync(bool ar, Inv inv, DateOnly date, decimal rate, decimal amt)
        {
            try
            {
                using var scope = NewScope(out var db);
                var fin = Svc<IFinPaymentService>(scope);
                var alloc = new List<PaymentAllocationLine> { new(inv.Id, amt) };
                if (ar)
                {
                    var r = await fin.CreateARReceiptAsync(new ARReceiptRequest(Customer, date, PaymentMethod.Wire,
                        inv.Fx ? Fx : null, rate, amt, Bank, null, null, alloc), UserId, Plant);
                    await fin.ConfirmARReceiptAsync(r.ARReceiptId);
                }
                else
                {
                    var p = await fin.CreateAPPaymentAsync(new APPaymentRequest(Supplier, date, PaymentMethod.Wire,
                        inv.Fx ? Fx : null, rate, amt, Bank, null, null, alloc), UserId, Plant);
                    await fin.ConfirmAPPaymentAsync(p.APPaymentId);
                }
                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }

        /// <summary>重評＋ECL＋步驟 9 入帳（可選：重跑一次必須不產生新分錄）→ 開始月結 → 關帳（含本廠關帳前稽核）。</summary>
        private async Task<string?> MonthEndAsync(Period p, bool rerunCheck)
        {
            try
            {
                using (var scope = NewScope(out var db))
                {
                    await Svc<IFxRevaluationService>(scope).RunRevaluationAsync(p.Id, Plant, new Dictionary<string, decimal> { [Fx] = BuyRate(p.End) });
                    await Svc<IEclService>(scope).ComputeEclAsync(p.Id, Plant, p.End);
                    await Svc<IPeriodEndAdjustmentService>(scope).PostAsync(p.Id, Plant);
                }
                if (rerunCheck)
                {
                    using var scope = NewScope(out var db);
                    var before = await db.JournalEntries.IgnoreQueryFilters().CountAsync(e => e.PlantId == Plant);
                    await Svc<IPeriodEndAdjustmentService>(scope).PostAsync(p.Id, Plant);
                    var after = await db.JournalEntries.IgnoreQueryFilters().CountAsync(e => e.PlantId == Plant);
                    if (after != before) return $"{p.Id} 步驟 9 重跑又產生 {after - before} 張分錄（應只補差額、沒有差額不開）";
                }
                using (var scope = NewScope(out var db))
                {
                    var periods = Svc<IAccountingPeriodService>(scope);
                    await periods.StartClosingAsync(p.Id, Plant, UserId);
                    await periods.ClosePeriodAsync(p.Id, Plant, UserId);
                }
                return null;
            }
            catch (InvalidOperationException ex) { return $"{p.Id} 月結失敗：{ex.Message}"; }
        }

        private async Task<Period> NextPeriodAsync()
        {
            using var scope = NewScope(out var db);
            var n = await Svc<IAccountingPeriodService>(scope).CreateNextPeriodAsync(Plant);
            return new Period { Id = n.PeriodId, Start = n.StartDate, End = n.EndDate };
        }

        private async Task<string?> ReopenAndRedoAsync(Period p)
        {
            try
            {
                using (var scope = NewScope(out var db))
                    await Svc<IAccountingPeriodService>(scope).ReopenPeriodAsync(p.Id, Plant, UserId);
            }
            catch (InvalidOperationException ex) { return $"{p.Id} 反關帳失敗：{ex.Message}"; }
            return await MonthEndAsync(p, rerunCheck: true);
        }
    }
}
