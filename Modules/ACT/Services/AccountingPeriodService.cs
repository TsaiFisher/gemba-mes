using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public class AccountingPeriodService : IAccountingPeriodService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDataAuditService? _audit;

        /// <summary>audit 為選配（未註冊時跳過關帳前稽核，既有測試直接建構不受影響）。</summary>
        public AccountingPeriodService(ApplicationDbContext db, IDataAuditService? audit = null)
        {
            _db    = db;
            _audit = audit;
        }

        // 一律 IgnoreQueryFilters：要檢查的是「單據所屬廠別」的期間，不是操作者當下切在哪個廠。
        // 跨廠調撥收貨時兩者本來就不同，靠全域過濾器會查到錯的廠（或查不到而靜默放行）。
        public async Task<AccountingPeriod?> GetByDateAsync(
            DateOnly date, string plantId, CancellationToken ct = default)
            => await _db.AccountingPeriods.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.PlantId == plantId
                                       && p.StartDate <= date && p.EndDate >= date, ct);

        public async Task<AccountingPeriod?> GetByIdAsync(
            string periodId, string plantId, CancellationToken ct = default)
            => await _db.AccountingPeriods.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.PeriodId == periodId && p.PlantId == plantId, ct);

        public async Task AssertCanPostAsync(
            DateOnly movementDate, string plantId, CancellationToken ct = default)
        {
            var period = await GetByDateAsync(movementDate, plantId, ct);
            if (period == null) return;  // 無期間設定時允許過帳（寬鬆模式）
            if (period.Status == PeriodStatus.Closed)
                throw new InvalidOperationException(
                    PzText.Get("Act_PeriodClosedNoPostFmt", plantId, period.PeriodId, period.ClosedAt, period.ClosedBy));
            if (period.Status == PeriodStatus.Closing)
                throw new InvalidOperationException(
                    PzText.Get("Act_PeriodClosingNoPostFmt", plantId, period.PeriodId));
        }

        public async Task StartClosingAsync(
            string periodId, string plantId, string userId, CancellationToken ct = default)
        {
            var p = await GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_PeriodNotFoundFmt", plantId, periodId));
            if (p.Status != PeriodStatus.Open && p.Status != PeriodStatus.Reopened)
                throw new InvalidOperationException(PzText.Get("Act_PeriodCannotStartCloseFmt", plantId, periodId, p.Status));
            p.Status    = PeriodStatus.Closing;
            p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task ClosePeriodAsync(
            string periodId, string plantId, string userId, CancellationToken ct = default)
        {
            var p = await GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_PeriodNotFoundFmt", plantId, periodId));
            if (p.Status != PeriodStatus.Closing)
                throw new InvalidOperationException(PzText.Get("Act_PeriodNotClosingFmt", plantId, periodId));

            // 關帳前資料一致性稽核（business_data_flow_reconciliation.md §4）：任何異常即拒絕關帳
            if (_audit != null)
            {
                // 稽核也必須限定本廠：期間分廠之後，甲廠關帳不該被乙廠的異常擋住。
                var audit = await _audit.RunPreClosingAuditAsync(periodId, plantId, ct);
                if (!audit.IsPassed)
                {
                    // 只報擋關帳的筆數與首筆。原本報 Failures.Count 會把提示類一起算進去，
                    // 操作者照著數字去修，修完剩下的那幾筆卻怎麼修都不會消失。
                    var blocking = audit.BlockingFailures;
                    var first = blocking[0];
                    throw new InvalidOperationException(
                        PzText.Get("Act_PreCloseAuditFailedFmt", plantId, periodId, blocking.Count, first.AuditType, first.DocId, first.ErrorDescription));
                }
            }

            p.Status    = PeriodStatus.Closed;
            p.ClosedAt  = DateTime.UtcNow;
            p.ClosedBy  = userId;
            p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task CancelClosingAsync(
            string periodId, string plantId, string userId, CancellationToken ct = default)
        {
            var p = await GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_PeriodNotFoundFmt", plantId, periodId));
            if (p.Status != PeriodStatus.Closing)
                throw new InvalidOperationException(PzText.Get("Act_PeriodNotClosingCannotCancelFmt", plantId, periodId, p.Status));

            // 曾經關過帳的期間退回 Reopened 而不是 Open：「這個期間關過帳」是稽核軌跡，
            // 不能被一次取消月結抹掉，否則之後看不出它是重開帳後的第二輪月結。
            p.Status    = p.ClosedAt.HasValue ? PeriodStatus.Reopened : PeriodStatus.Open;
            p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task ReopenPeriodAsync(
            string periodId, string plantId, string userId, CancellationToken ct = default)
        {
            var p = await GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_PeriodNotFoundFmt", plantId, periodId));
            if (p.Status != PeriodStatus.Closed)
                throw new InvalidOperationException(PzText.Get("Act_PeriodNotClosedCannotReopenFmt", plantId, periodId));
            p.Status    = PeriodStatus.Reopened;
            p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<AccountingPeriod> CreateNextPeriodAsync(
            string plantId, CancellationToken ct = default)
        {
            // 接續的是「本廠」最後一期，不是全公司最後一期——各廠的月結進度本來就可以不同。
            var last = await _db.AccountingPeriods.IgnoreQueryFilters()
                .Where(p => p.PlantId == plantId)
                .OrderByDescending(p => p.PeriodId)
                .FirstOrDefaultAsync(ct);

            int year, month;
            if (last == null)
            {
                year  = DateTime.UtcNow.Year;
                month = DateTime.UtcNow.Month;
            }
            else
            {
                var next = new DateTime(last.Year, last.Month, 1).AddMonths(1);
                year  = next.Year;
                month = next.Month;
            }

            var periodId  = $"{year:D4}{month:D2}";
            var startDate = new DateOnly(year, month, 1);
            var endDate   = startDate.AddMonths(1).AddDays(-1);

            var period = new AccountingPeriod
            {
                PeriodId  = periodId,
                PlantId   = plantId,
                Year      = year,
                Month     = month,
                StartDate = startDate,
                EndDate   = endDate,
                Status    = PeriodStatus.Open,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.AccountingPeriods.Add(period);
            await _db.SaveChangesAsync(ct);
            return period;
        }
    }
}
