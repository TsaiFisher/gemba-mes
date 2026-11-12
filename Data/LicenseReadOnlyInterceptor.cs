using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Data;

/// <summary>授權唯讀／停用時嘗試寫入業務資料。</summary>
public sealed class LicenseReadOnlyException(string message) : InvalidOperationException(message);

/// <summary>唯讀期間仍允許寫入的實體型別。部署專案可用 <c>services.Configure&lt;LicenseReadOnlyOptions&gt;</c> 加列。</summary>
public sealed class LicenseReadOnlyOptions
{
    /// <summary>
    /// 預設只放行「登入得進來、改得了密碼、留得下稽核」所需的型別：
    /// 登入失敗計數與安全戳記（ApplicationUser）、2FA 金鑰與復原碼（UserToken）、
    /// 外部登入、密碼歷史、以及這些變更本身的稽核紀錄。
    /// 管理員改使用者資料雖然也寫 ApplicationUser，但會先被 <c>LicenseReadOnlyMiddleware</c> 擋在 HTTP 層。
    /// </summary>
    public HashSet<Type> ExemptEntityTypes { get; } =
    [
        typeof(ApplicationUser),
        typeof(IdentityUserToken<string>),
        typeof(IdentityUserLogin<string>),
        typeof(PasswordHistory),
        typeof(AuditLog),
    ];
}

/// <summary>
/// 授權唯讀的資料庫層強制（SECURITY-OPERATIONS.md §18.8）。HTTP 層的
/// <c>LicenseReadOnlyMiddleware</c> 是第一道；這裡是保證——背景排程、匯入、
/// 手機 API、任何繞過 middleware 的寫入最後都要經過 DbContext。
///
/// <para><b>兩條路都要擋：</b></para>
/// <list type="number">
///   <item><b>SaveChanges</b>：有任何非豁免實體的新增／修改／刪除就整批拒絕。</item>
///   <item><b>SQL 指令</b>：<c>ExecuteUpdate</c>／<c>ExecuteDelete</c>／<c>ExecuteSqlRaw</c> 不經過
///         SaveChanges。唯讀期間凡是寫入類 SQL 一律拒絕，除非它屬於一次已放行的 SaveChanges
///         （以 DbContext 實例追蹤；AsyncLocal 在 async 攔截器內設定不會回流到呼叫端，不能用）。</item>
/// </list>
///
/// <para>啟動時的 migration 與系統種子在 <see cref="ILicenseStateService.SetLicense"/> 之前執行，
/// 狀態為「尚未驗證」不擋，因此唯讀中重啟仍能正常升級 schema。</para>
/// </summary>
public sealed class LicenseReadOnlyInterceptor(ILicenseStateService state, LicenseReadOnlyOptions options)
    : ISaveChangesInterceptor, IDbCommandInterceptor
{
    // 正在進行、且已判定放行的 SaveChanges（只有豁免實體）
    private readonly ConditionalWeakTable<DbContext, object> _allowedSaves = new();

    // ── SaveChanges ──────────────────────────────────────────────────────

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Release(eventData.Context);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => Release(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    public void SaveChangesCanceled(DbContextEventData eventData) => Release(eventData.Context);

    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    private void Check(DbContext? context)
    {
        if (context is null) return;
        var status = state.Current;
        if (!status.IsWriteBlocked) return;

        var blocked = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Metadata.ClrType)
            .Where(t => !options.ExemptEntityTypes.Contains(t))
            .Distinct()
            .ToList();

        if (blocked.Count > 0)
            throw new LicenseReadOnlyException(Message(status) +
                $" [{string.Join(", ", blocked.Select(t => t.Name))}]");

        _allowedSaves.AddOrUpdate(context, this);
    }

    private void Release(DbContext? context)
    {
        if (context is not null) _allowedSaves.Remove(context);
    }

    // ── SQL 指令 ─────────────────────────────────────────────────────────

    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        CheckCommand(command, eventData);
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CheckCommand(command, eventData);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        CheckCommand(command, eventData);
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        CheckCommand(command, eventData);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        CheckCommand(command, eventData);
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        CheckCommand(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void CheckCommand(DbCommand command, CommandEventData eventData)
    {
        if (!IsWriteSql(command.CommandText)) return;
        var status = state.Current;
        if (!status.IsWriteBlocked) return;
        if (eventData.Context is { } ctx && _allowedSaves.TryGetValue(ctx, out _)) return;

        throw new LicenseReadOnlyException(Message(status));
    }

    private static readonly Regex WriteKeyword = new(
        @"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|ALTER|DROP|COPY|CALL|GRANT|REVOKE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingComments = new(
        @"^(\s|--[^\n]*\n|/\*.*?\*/)*", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// 是否為寫入類 SQL（純函式，供測試）。以第一個關鍵字判斷；<c>WITH</c> 開頭的 CTE
    /// 內可夾帶寫入，整句出現寫入關鍵字即視為寫入。寧可誤擋查詢（唯讀期間只影響這一句），
    /// 不可漏放寫入。
    /// </summary>
    internal static bool IsWriteSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var body = LeadingComments.Replace(sql, "", 1).TrimStart();
        var first = body.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?
            .TrimEnd('(', ';').ToUpperInvariant();

        return first switch
        {
            "SELECT" or "SHOW" or "EXPLAIN" or "VALUES" => false,
            "WITH" => WriteKeyword.IsMatch(body),
            // BEGIN／COMMIT／SAVEPOINT／SET 等交易與工作階段指令不改資料
            "BEGIN" or "COMMIT" or "ROLLBACK" or "SAVEPOINT" or "RELEASE" or "SET" or "START" => false,
            _ => true,
        };
    }

    // 例外訊息可能經由控制器的錯誤處理呈現給一般使用者，走資源檔
    private static string Message(LicenseStatus status) => CoreText.Get(
        status.State == LicenseState.Disabled ? "Msg_LicenseDisabledBlocked" : "Msg_LicenseReadOnlyBlocked");
}
