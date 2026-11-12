using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public class EclService : IEclService
    {
        private readonly ApplicationDbContext _db;

        public EclService(ApplicationDbContext db) => _db = db;

        public async Task<IList<EclAllowance>> ComputeEclAsync(
            string periodId, string plantId, DateOnly asOfDate,
            CancellationToken ct = default)
        {
            // 刪除本期間舊評估
            var old = await _db.EclAllowances
                .Where(e => e.PeriodId == periodId && e.PlantId == plantId)
                .ToListAsync(ct);
            _db.EclAllowances.RemoveRange(old);

            var lossRates = await GetLossRatesAsync(plantId, ct);

            // 取基準日當下未結的 AR Invoice（基準日之後才開立的不算）。
            // 未結以基準日當時計（見 OpenItemsAsOf）：現在已收訖的在基準日可能還沒收，
            // 基準日之後才收的款／開的折讓要加回去——否則反關帳重做上一期，下一期的收款會讓上期備抵少提。
            var openInvoices = await _db.ARInvoices
                .Where(i => i.PlantId == plantId
                         && i.Status != ARInvoiceStatus.Void
                         && i.InvoiceDate <= asOfDate)
                .AsNoTracking().ToListAsync(ct);
            var settledAfter = await OpenItemsAsOf.ArSettledAfterAsync(_db, plantId, asOfDate, ct);

            // 依帳齡區間彙總 AR 本幣餘額
            var bucketBalances = new Dictionary<string, decimal>();
            foreach (var inv in openInvoices)
            {
                var bucket = BucketOf(lossRates, asOfDate.DayNumber - inv.DueDate.DayNumber).AgingBucket;
                var exposure = Math.Max(0m, OutstandingLocal(inv) + settledAfter.GetValueOrDefault(inv.ARInvoiceId) * inv.ExchangeRate);
                bucketBalances[bucket] = bucketBalances.GetValueOrDefault(bucket) + exposure;
            }

            var results = new List<EclAllowance>();
            foreach (var lr in lossRates)
            {
                var balance = bucketBalances.GetValueOrDefault(lr.AgingBucket, 0m);
                var ecl     = Math.Round(balance * lr.LossRate, 4);
                var row = new EclAllowance
                {
                    PeriodId    = periodId,
                    AgingBucket = lr.AgingBucket,
                    ArBalance   = balance,
                    LossRate    = lr.LossRate,
                    EclAmount   = ecl,
                    AssessedAt  = DateTime.UtcNow,
                    PlantId     = plantId,
                };
                results.Add(row);
                _db.EclAllowances.Add(row);
            }

            await _db.SaveChangesAsync(ct);
            return results;
        }

        public async Task<IReadOnlyList<EclLossRate>> GetLossRatesAsync(string plantId, CancellationToken ct = default)
        {
            // 只有本廠設定，沒有「全公司設定」這一層：EclLossRate 是 IPlantEntity，
            // 存檔時 ApplyGlobalValues 會把 PlantId 填成當下廠別，全域查詢過濾也只放行本廠——
            // PlantId 為 null 的列建不出來、也查不到（CLAUDE.md 簽核設定那條是同一件事）。
            // 原本的 `|| r.PlantId == null` 因此是死碼，拿掉以免讓人以為有全公司預設可設。
            var list = await _db.EclLossRates
                .Where(r => r.PlantId == plantId)
                .OrderBy(r => r.AgingDaysFrom)
                .AsNoTracking().ToListAsync(ct);
            return list.Count > 0 ? list : GetDefaultLossRates();
        }

        /// <summary>
        /// 未結餘額一律以**本幣**計：本幣 = 外幣 × 匯率（currency.md 換算方向），
        /// 與 AR 帳齡表同一口徑。原本用 TotalAmount − ReceivedAmount，那是交易幣，
        /// 外幣發票的原幣金額會直接與台幣相加。
        /// </summary>
        public static decimal OutstandingLocal(ARInvoice inv)
            => inv.AmountLocal - (inv.ReceivedAmount + inv.CreditedAmount) * inv.ExchangeRate;   // 折讓也要扣

        /// <summary>
        /// 信用風險曝險＝未結本幣但不得為負：折讓超過剩餘未收（客戶多付）是我方欠客戶，
        /// 沒有信用損失可言，也不能拿來抵減別張發票的備抵。
        /// </summary>
        public static decimal ExposureLocal(ARInvoice inv) => Math.Max(0m, OutstandingLocal(inv));

        /// <summary>
        /// 期末評估的基準日：期間的最後一天；期間還沒結束（跑當期）就用今天——不能拿未來的日子算逾期。
        /// 原本一律用今天：回頭補跑上個月的 ECL，會用今天的帳齡去算上個月的準備。
        /// </summary>
        public static DateOnly AsOfFor(DateOnly? periodEnd, DateOnly today)
            => periodEnd is { } end && end < today ? end : today;

        /// <summary>逾期天數（到期日起算；未到期為 ≤ 0）落在哪個區間；超出所有設定時歸最後一區。</summary>
        public static EclLossRate BucketOf(IReadOnlyList<EclLossRate> rates, int daysPastDue)
            => rates.FirstOrDefault(r => daysPastDue >= r.AgingDaysFrom
                                      && (r.AgingDaysTo < 0 || daysPastDue <= r.AgingDaysTo))
               ?? rates[^1];

        private static List<EclLossRate> GetDefaultLossRates() => new()
        {
            new EclLossRate { AgingBucket = "Current",   AgingDaysFrom = int.MinValue, AgingDaysTo =   0, LossRate = 0.005m },
            new EclLossRate { AgingBucket = "1-30",      AgingDaysFrom = 1,  AgingDaysTo =  30, LossRate = 0.02m  },
            new EclLossRate { AgingBucket = "31-60",     AgingDaysFrom = 31, AgingDaysTo =  60, LossRate = 0.05m  },
            new EclLossRate { AgingBucket = "61-90",     AgingDaysFrom = 61, AgingDaysTo =  90, LossRate = 0.10m  },
            new EclLossRate { AgingBucket = "90+",       AgingDaysFrom = 91, AgingDaysTo =  -1, LossRate = 0.30m  },
        };
    }
}
