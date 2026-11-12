using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public interface IErpSyncService
    {
        /// <summary>標記拋轉成功（Pending/Failed → Synced）。T＝可拋轉單據（APInvoice/ARInvoice…）。</summary>
        Task MarkSyncedAsync<T>(string docId, string erpVoucherNo,
            decimal? erpVoucherAmount = null, CancellationToken ct = default) where T : class, IErpSyncDocument;

        /// <summary>標記拋轉失敗，遞增重試次數；超過上限時鎖定（停止重試）</summary>
        Task MarkFailedAsync<T>(string docId, string errorMessage, CancellationToken ct = default)
            where T : class, IErpSyncDocument;

        /// <summary>手動排除（Excluded），不再自動拋轉</summary>
        Task MarkExcludedAsync<T>(string docId, CancellationToken ct = default)
            where T : class, IErpSyncDocument;

        // ── 相容包裝（§5.4 既有 AP 呼叫慣例；泛型化前的簽章，等同 T=APInvoice）──
        Task MarkSyncedAsync(string invoiceId, string erpVoucherNo,
            decimal? erpVoucherAmount = null, CancellationToken ct = default);
        Task MarkFailedAsync(string invoiceId, string errorMessage, CancellationToken ct = default);
        Task MarkExcludedAsync(string invoiceId, CancellationToken ct = default);

        /// <summary>查詢超過指定小時仍未成功同步的 APInvoice（ERP 拋轉延遲稽核）</summary>
        Task<IList<APInvoice>> GetPendingOverdueAsync(int overdueHours = 24, CancellationToken ct = default);

        /// <summary>查詢超過指定小時仍未成功同步的 ARInvoice（§9 P-3 對稱擴大）</summary>
        Task<IList<ARInvoice>> GetPendingOverdueArAsync(int overdueHours = 24, CancellationToken ct = default);
    }

    public class ErpSyncService : IErpSyncService
    {
        /// <summary>最大重試次數（超過後不再自動重試，需人工介入）§5.4 BVA</summary>
        public const int MaxErpRetryCount = 3;

        /// <summary>拋轉尾差容忍度預設（±0.01 元歸入尾差調整科目）§5.4 BVA；
        /// §9 P-7 可經 audit_amount_tolerance 系統參數覆寫。</summary>
        public const decimal RoundingTolerance = 0.01m;

        private readonly ApplicationDbContext _db;
        private readonly ISystemParamService? _sysParams;

        public ErpSyncService(ApplicationDbContext db, ISystemParamService? sysParams = null)
        {
            _db = db;
            _sysParams = sysParams;
        }

        /// <summary>§9 P-7：尾差容忍值取系統參數，未設定/不合法時退回預設。</summary>
        private async Task<decimal> GetRoundingToleranceAsync()
            => _sysParams != null
               && decimal.TryParse(await _sysParams.GetAsync(ProjectZSystemParamKeys.AuditAmountTolerance), out var v)
               && v > 0
                ? v : RoundingTolerance;

        public async Task MarkSyncedAsync<T>(string docId, string erpVoucherNo,
            decimal? erpVoucherAmount = null, CancellationToken ct = default) where T : class, IErpSyncDocument
        {
            var doc = await FindAsync<T>(docId, ct);

            // §5.4 BVA：雙向金額尾差容忍
            if (erpVoucherAmount.HasValue)
            {
                var tolerance = await GetRoundingToleranceAsync();
                var diff = Math.Abs(doc.TotalAmount - erpVoucherAmount.Value);
                if (diff > tolerance)
                    throw new InvalidOperationException(
                        PzText.Get("Fin_ErpAmountMismatchFmt", typeof(T).Name, docId, doc.TotalAmount, erpVoucherAmount, diff, tolerance));
            }

            doc.ErpSyncStatus   = ErpSyncStatus.Synced;
            doc.ErpVoucherNo    = erpVoucherNo;
            doc.ErpSyncTime     = DateTime.UtcNow;
            doc.ErpErrorMessage = null;
            doc.UpdatedAt       = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
        }

        public async Task MarkFailedAsync<T>(string docId, string errorMessage, CancellationToken ct = default)
            where T : class, IErpSyncDocument
        {
            var doc = await FindAsync<T>(docId, ct);

            if (doc.ErpSyncStatus == ErpSyncStatus.Excluded)
                throw new InvalidOperationException(PzText.Get("Fin_ErpExcludedFmt", typeof(T).Name, docId));

            doc.ErpRetryCount++;
            doc.ErpErrorMessage = errorMessage;
            doc.UpdatedAt       = DateTime.UtcNow;

            // §5.4 EP Invalid：超過最大重試次數時鎖定為 Failed，停止重試
            doc.ErpSyncStatus = doc.ErpRetryCount >= MaxErpRetryCount
                ? ErpSyncStatus.Failed      // 永久失敗，需人工介入
                : ErpSyncStatus.Failed;     // 暫時失敗，仍可重試

            await _db.SaveChangesAsync(ct);
        }

        public async Task MarkExcludedAsync<T>(string docId, CancellationToken ct = default)
            where T : class, IErpSyncDocument
        {
            var doc = await FindAsync<T>(docId, ct);
            doc.ErpSyncStatus = ErpSyncStatus.Excluded;
            doc.UpdatedAt     = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        // ── 相容包裝：既有 AP 呼叫端/測試不需改 ────────────────────────────

        public Task MarkSyncedAsync(string invoiceId, string erpVoucherNo,
            decimal? erpVoucherAmount = null, CancellationToken ct = default)
            => MarkSyncedAsync<APInvoice>(invoiceId, erpVoucherNo, erpVoucherAmount, ct);

        public Task MarkFailedAsync(string invoiceId, string errorMessage, CancellationToken ct = default)
            => MarkFailedAsync<APInvoice>(invoiceId, errorMessage, ct);

        public Task MarkExcludedAsync(string invoiceId, CancellationToken ct = default)
            => MarkExcludedAsync<APInvoice>(invoiceId, ct);

        public async Task<IList<APInvoice>> GetPendingOverdueAsync(
            int overdueHours = 24, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddHours(-overdueHours);
            return await _db.APInvoices
                .Where(i => i.Status != APInvoiceStatus.Void
                         && i.ErpSyncStatus != ErpSyncStatus.Synced
                         && i.ErpSyncStatus != ErpSyncStatus.Excluded
                         && i.CreatedAt < cutoff)
                .OrderBy(i => i.CreatedAt)
                .ToListAsync(ct);
        }

        public async Task<IList<ARInvoice>> GetPendingOverdueArAsync(
            int overdueHours = 24, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddHours(-overdueHours);
            return await _db.ARInvoices
                .Where(i => i.Status != ARInvoiceStatus.Void
                         && i.ErpSyncStatus != ErpSyncStatus.Synced
                         && i.ErpSyncStatus != ErpSyncStatus.Excluded
                         && i.CreatedAt < cutoff)
                .OrderBy(i => i.CreatedAt)
                .ToListAsync(ct);
        }

        public bool IsLocked(IErpSyncEntity entity)
            => entity.ErpSyncStatus == ErpSyncStatus.Failed
               && entity.ErpRetryCount >= MaxErpRetryCount;

        private async Task<T> FindAsync<T>(string docId, CancellationToken ct) where T : class, IErpSyncDocument
            => await _db.Set<T>().FindAsync(new object[] { docId }, ct)
               ?? throw new InvalidOperationException(PzText.Get("Fin_ErpDocNotFoundFmt", typeof(T).Name, docId));
    }
}
