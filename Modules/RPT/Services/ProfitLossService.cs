using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public class ProfitLossService : IProfitLossService
    {
        private readonly ApplicationDbContext _context;
        private readonly Modules.BAS.Services.IAccountClassifier _accounts;

        public ProfitLossService(
            ApplicationDbContext context,
            Modules.BAS.Services.IAccountClassifier accounts)
        {
            _context  = context;
            _accounts = accounts;
        }

        public Task<ProfitLossReport> GetReportAsync(
            string fromPeriod, string toPeriod, string plantId, CancellationToken ct = default)
            => GetConsolidatedReportAsync(fromPeriod, toPeriod, new[] { plantId }, ct);

        public async Task<ProfitLossReport> GetConsolidatedReportAsync(
            string fromPeriod, string toPeriod, IReadOnlyCollection<string> plantIds,
            CancellationToken ct = default)
        {
            // 見 BalanceSheetService 同處註解：空集合不等於「全公司」。
            if (plantIds.Count == 0)
                return new ProfitLossReport { FromPeriod = fromPeriod, ToPeriod = toPeriod };

            var plantSet = plantIds.ToList();

            // IgnoreQueryFilters：JournalEntry 是 IPlantEntity，全域過濾會把
            // 「不是操作者當下這一廠」的分錄整批濾掉——跨廠加總於是靜靜等於本廠數字，
            // 畫面照常渲染、沒有任何錯誤。資料範圍不因此放寬：下面的 plantSet 條件
            // 就是明確的廠別閘門，而 JournalEntry 只掛廠別過濾、不掛 IDataAuthEntity
            // 的層級權限過濾，兩者不會被一起關掉。
            var lines = await _context.JournalLines
                .IgnoreQueryFilters()
                .Where(l => plantSet.Contains(l.JournalEntry!.PlantId!)
                         && string.Compare(l.JournalEntry.PeriodId, fromPeriod) >= 0
                         && string.Compare(l.JournalEntry.PeriodId, toPeriod)   <= 0)
                .GroupBy(l => new { l.AccountCode, l.AccountName })
                .Select(g => new {
                    g.Key.AccountCode,
                    g.Key.AccountName,
                    TotalDebit  = g.Sum(x => x.Debit),
                    TotalCredit = g.Sum(x => x.Credit)
                })
                .AsNoTracking()
                .ToListAsync(ct);

            // 分類改讀科目主檔（原本直接讀科目代碼第一個字元）；主檔查無退回首碼，
            // 對既有資料是恆等變換。見 AccountTypeMap.FromLeadingDigit。
            var accountTypes = await _accounts.LoadAsync(ct);

            var report = new ProfitLossReport { FromPeriod = fromPeriod, ToPeriod = toPeriod };

            foreach (var g in lines)
            {
                var type = accountTypes.Resolve(g.AccountCode);
                if (type is null || type.Value.IsBalanceSheet()) continue;   // 1–3 不進損益表

                // 借方為正／貸方為正由科目性質決定，不再各自寫一份 switch。
                var amount = type.Value.IsDebitNormal()
                    ? g.TotalDebit - g.TotalCredit
                    : g.TotalCredit - g.TotalDebit;

                // 分段名稱維持原字串：ProfitLossReport 的欄位與畫面的 resx 鍵都以它為準。
                string category;
                switch (type.Value)
                {
                    case Modules.BAS.Models.AccountType.Revenue:
                        category = "Revenue";
                        report.Revenue += amount;
                        break;
                    case Modules.BAS.Models.AccountType.Cogs:
                        category = "Cogs";
                        report.Cogs += amount;
                        break;
                    case Modules.BAS.Models.AccountType.OperatingExpense:
                        category = "OperatingExpenses";
                        report.OperatingExpenses += amount;
                        break;
                    case Modules.BAS.Models.AccountType.OtherIncome:
                        category = "OtherIncome";
                        report.OtherIncome += amount;
                        break;
                    case Modules.BAS.Models.AccountType.OtherExpense:
                        category = "OtherExpenses";
                        report.OtherExpenses += amount;
                        break;
                    default:
                        continue;
                }

                if (amount != 0)
                    report.Lines.Add(new ProfitLossLine {
                        AccountCode = g.AccountCode,
                        AccountName = g.AccountName,
                        Category    = category,
                        Amount      = amount
                    });
            }

            report.Lines.Sort((a, b) => string.Compare(a.AccountCode, b.AccountCode, StringComparison.Ordinal));
            return report;
        }
    }
}
