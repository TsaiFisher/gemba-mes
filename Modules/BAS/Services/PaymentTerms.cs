using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 依往來廠商的付款條件計算應收／應付的到期日。
    ///
    /// <para><b>為什麼要有這支</b>：原本自動產生的應收／應付發票一律「單據日＋30 天」，
    /// 主檔上的結帳日從來沒被讀過。到期日不只是畫面上的日期——帳齡分析、呆帳提列與
    /// 預期信用損失（ECL）都依它分桶，到期日錯，呆帳金額就跟著錯，而且不會有任何錯誤訊息。</para>
    ///
    /// <para><b>規則</b>（2026-10-02 與使用者確認）：</para>
    /// <list type="bullet">
    /// <item>沒有結帳日：到期日＝單據日＋天數。</item>
    /// <item>有結帳日（月結）：先找單據日所屬那一期的結帳日——當月結帳日若還沒過（單據日 ≤ 結帳日）就是當月，
    ///   否則是下個月；結帳日大於該月天數時取月底（31 日在 2 月＝2/28 或 2/29）。到期日＝該結帳日＋天數。
    ///   例：結帳日 5、天數 30 → 6/6～7/5 的單據在 7/5 結帳，到期 8/4。</item>
    /// <item>天數沒填沿用 30 天（與修改前相同，未設定付款條件的往來廠商行為不變）。</item>
    /// </list>
    /// </summary>
    public static class PaymentTerms
    {
        public const int DefaultCreditDays = 30;

        public static DateOnly DueDate(DateOnly documentDate, int? closingDay, int? creditDays)
        {
            var days = creditDays ?? DefaultCreditDays;
            if (closingDay is not (>= 1 and <= 31)) return documentDate.AddDays(days);

            return ClosingDate(documentDate, closingDay.Value).AddDays(days);
        }

        /// <summary>單據日所屬那一期的結帳日。</summary>
        public static DateOnly ClosingDate(DateOnly documentDate, int closingDay)
        {
            var thisMonth = OnDay(documentDate.Year, documentDate.Month, closingDay);
            if (documentDate <= thisMonth) return thisMonth;

            var next = new DateOnly(documentDate.Year, documentDate.Month, 1).AddMonths(1);
            return OnDay(next.Year, next.Month, closingDay);
        }

        /// <summary>依往來廠商主檔計算；查無主檔時沿用單據日＋30 天。</summary>
        public static async Task<DateOnly> DueDateAsync(
            ApplicationDbContext db, string? companyCode, DateOnly documentDate, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(companyCode)) return DueDate(documentDate, null, null);

            var terms = await db.Companies.AsNoTracking()
                .Where(c => c.CompanyCode == companyCode)
                .Select(c => new { c.PaymentTermDays, c.PaymentCreditDays })
                .FirstOrDefaultAsync(ct);
            return DueDate(documentDate, terms?.PaymentTermDays, terms?.PaymentCreditDays);
        }

        private static DateOnly OnDay(int year, int month, int day) =>
            new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
    }
}
