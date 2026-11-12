using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 科目 → 科目性質。財務報表用它決定一筆分錄落在哪一段、正負號是哪一邊。
    /// </summary>
    public interface IAccountClassifier
    {
        /// <summary>
        /// 一次載入整張科目主檔。刻意不提供「查單一科目」的方法：
        /// 報表一次要分類數百個科目，逐筆查就是 N+1，而那種慢法在測試資料上看不出來。
        /// </summary>
        Task<AccountTypeMap> LoadAsync(CancellationToken ct = default);
    }

    /// <summary>科目代碼 → 性質的查表結果（已 materialize，不再碰資料庫）。</summary>
    public sealed class AccountTypeMap
    {
        private readonly IReadOnlyDictionary<string, AccountType> _map;

        public AccountTypeMap(IReadOnlyDictionary<string, AccountType> map) => _map = map;

        /// <summary>主檔沒有這個科目時的退路，見 <see cref="FromLeadingDigit"/>。</summary>
        public AccountType? Resolve(string? accountCode)
        {
            if (string.IsNullOrEmpty(accountCode)) return null;
            return _map.TryGetValue(accountCode, out var t) ? t : FromLeadingDigit(accountCode);
        }

        /// <summary>
        /// 主檔查無時，依科目代碼首碼判斷。
        ///
        /// <para><b>這不是「順便做的容錯」，是刻意保留的相容行為</b>：在科目主檔存在之前，
        /// <c>BalanceSheetService</c> 與 <c>ProfitLossService</c> 就是直接讀第一個字元分類的。
        /// 歷史傳票上的科目代碼是**當下寫進去的字串**，不保證現在的主檔裡還有
        /// （科目可以被刪、可以在主檔建立之前就用過）。沒有這條退路，那些分錄會從報表上
        /// 整批消失——而且是靜默消失，總計不平衡也沒有任何訊息。</para>
        /// </summary>
        public static AccountType? FromLeadingDigit(string accountCode)
            => accountCode.Length > 0 && accountCode[0] is >= '1' and <= '8'
                ? (AccountType)(accountCode[0] - '0')
                : null;
    }

    public sealed class AccountClassifier : IAccountClassifier
    {
        private readonly ApplicationDbContext _db;

        public AccountClassifier(ApplicationDbContext db) => _db = db;

        public async Task<AccountTypeMap> LoadAsync(CancellationToken ct = default)
        {
            // 停用的科目照樣要能分類：停用只影響「還能不能選它」，
            // 不影響「以前用它記過的帳現在該算在哪一段」。
            var map = await _db.Set<ChartOfAccount>().AsNoTracking()
                .Select(a => new { a.AccountCode, a.AccountType })
                .ToDictionaryAsync(a => a.AccountCode, a => a.AccountType, ct);
            return new AccountTypeMap(map);
        }
    }
}
