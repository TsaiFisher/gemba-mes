using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public class BalanceSheetService : IBalanceSheetService
    {
        private readonly ApplicationDbContext _context;
        private readonly Modules.BAS.Services.IAccountClassifier _accounts;

        public BalanceSheetService(
            ApplicationDbContext context,
            Modules.BAS.Services.IAccountClassifier accounts)
        {
            _context  = context;
            _accounts = accounts;
        }

        public Task<BalanceSheetReport> GetReportAsync(
            string asOfPeriod, string plantId, CancellationToken ct = default)
            => GetConsolidatedReportAsync(asOfPeriod, new[] { plantId }, ct);

        public async Task<BalanceSheetReport> GetConsolidatedReportAsync(
            string asOfPeriod, IReadOnlyCollection<string> plantIds, CancellationToken ct = default)
        {
            // 空集合回空報表，而不是「不加廠別條件」——後者會變成「全公司」，
            // 也就是把一個明顯的呼叫端錯誤（忘了傳廠別）靜默翻譯成資料範圍擴大。
            if (plantIds.Count == 0) return new BalanceSheetReport { AsOfPeriod = asOfPeriod };

            var plantSet = plantIds.ToList();

            // Cumulative balance: all journal entries up to and including asOfPeriod
            // IgnoreQueryFilters：JournalEntry 是 IPlantEntity，全域過濾會把
            // 「不是操作者當下這一廠」的分錄整批濾掉——跨廠加總於是靜靜等於本廠數字，
            // 畫面照常渲染、沒有任何錯誤。資料範圍不因此放寬：下面的 plantSet 條件
            // 就是明確的廠別閘門，而 JournalEntry 只掛廠別過濾、不掛 IDataAuthEntity
            // 的層級權限過濾，兩者不會被一起關掉。
            var lines = await _context.JournalLines
                .IgnoreQueryFilters()
                .Where(l => plantSet.Contains(l.JournalEntry!.PlantId!)
                         && string.Compare(l.JournalEntry.PeriodId, asOfPeriod) <= 0)
                .GroupBy(l => new { l.AccountCode, l.AccountName })
                .Select(g => new {
                    g.Key.AccountCode,
                    g.Key.AccountName,
                    TotalDebit  = g.Sum(x => x.Debit),
                    TotalCredit = g.Sum(x => x.Credit)
                })
                .AsNoTracking()
                .ToListAsync(ct);

            // 分類改讀科目主檔（原本直接讀科目代碼第一個字元）。主檔查無時退回首碼，
            // 所以對既有資料這是恆等變換——見 AccountTypeMap.FromLeadingDigit。
            var accountTypes = await _accounts.LoadAsync(ct);

            var report = new BalanceSheetReport { AsOfPeriod = asOfPeriod };
            var assets      = new BalanceSheetSection { Category = "Assets" };
            var liabilities = new BalanceSheetSection { Category = "Liabilities" };
            var equity      = new BalanceSheetSection { Category = "Equity" };

            foreach (var g in lines.OrderBy(l => l.AccountCode))
            {
                var type = accountTypes.Resolve(g.AccountCode);
                BalanceSheetSection? section = type switch
                {
                    Modules.BAS.Models.AccountType.Asset     => assets,
                    Modules.BAS.Models.AccountType.Liability => liabilities,
                    Modules.BAS.Models.AccountType.Equity    => equity,
                    _                                        => null,   // 損益類與無法分類者不進資產負債表
                };
                if (section == null) continue;

                // 借方為正／貸方為正由科目性質決定，不再各自寫一份 switch。
                var balance = type!.Value.IsDebitNormal()
                    ? g.TotalDebit - g.TotalCredit
                    : g.TotalCredit - g.TotalDebit;

                if (balance != 0)
                    section.Items.Add(new BalanceSheetItem {
                        AccountCode = g.AccountCode,
                        AccountName = g.AccountName,
                        Balance     = balance
                    });
            }

            assets.Total      = assets.Items.Sum(i => i.Balance);
            liabilities.Total = liabilities.Items.Sum(i => i.Balance);
            equity.Total      = equity.Items.Sum(i => i.Balance);

            report.TotalAssets      = assets.Total;
            report.TotalLiabilities = liabilities.Total;
            report.TotalEquity      = equity.Total;
            report.Sections.AddRange(new[] { assets, liabilities, equity });
            return report;
        }
    }
}
