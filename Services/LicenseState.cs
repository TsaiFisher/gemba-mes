using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartFactory.MES.Services;

/// <summary>
/// 授權執行狀態（SECURITY-OPERATIONS.md §18.8）。數值越大越嚴格，<see cref="Unlocked"/> 例外。
/// </summary>
public enum LicenseState
{
    /// <summary>正常。</summary>
    Normal = 0,
    /// <summary>提醒：即將到期。管理員橫幅、/health Degraded。</summary>
    Warning = 1,
    /// <summary>唯讀：可登入、查詢、報表；不能新增、修改、過帳。</summary>
    ReadOnly = 2,
    /// <summary>停用：唯讀滿 30 天，只顯示授權失效頁。</summary>
    Disabled = 3,
    /// <summary>終止聲明生效：永久解除所有授權檢查（§18.9）。</summary>
    Unlocked = 4,
}

/// <summary>某一時點的授權狀態與理由。</summary>
public sealed record LicenseStatus(
    LicenseState State,
    string Reason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ReadOnlySince,
    DateTimeOffset? DisabledAt)
{
    /// <summary>是否禁止寫入（唯讀與停用）。</summary>
    public bool IsWriteBlocked => State is LicenseState.ReadOnly or LicenseState.Disabled;
}

/// <summary>
/// 判定狀態所需的輸入。全部來自已驗簽的資料（授權檔、撤銷清單、終止聲明），
/// 所以「從哪一天開始唯讀」可由資料推導，不需另外持久化。
/// </summary>
public sealed record LicenseStateInputs(
    DateTimeOffset ExpiresAt,
    string BindingMode,
    DateTimeOffset? RevokedAt = null,
    string? RevokedReason = null,
    bool SunsetActive = false,
    DateTimeOffset? LeaseNotAfter = null,
    DateTimeOffset? LeaseNoticeAt = null,
    string? LeaseNoticeStatus = null,
    string? LeaseWarning = null,
    DateTimeOffset? LeaseServerTime = null,
    HostCheckResult HostCheck = HostCheckResult.NotRequired,
    DeploymentKind HostKind = DeploymentKind.Unknown,
    bool OnlineCheckOptIn = false)
{
    /// <summary>授權檔簽章要求線上租約的綁定方式（§18.5：offline 以外沒有租約一律不放行；TPM 為第二期）。</summary>
    public bool LeaseRequiredBySignature => BindingMode is LicenseBindingModes.Lease or LicenseBindingModes.Ci;

    /// <summary>
    /// offline 授權跑在 VM／容器（§18.15.8）：主機識別碼在 VM 設定檔裡可改，比對通過也不能證明不是複製品，
    /// 一律要線上確認。依據是執行期偵測，<b>不讀資料庫</b>——系統參數改成停用也無效。
    /// </summary>
    public bool LeaseForcedByHost => BindingMode == LicenseBindingModes.Offline
                                     && HostKind is DeploymentKind.Vm or DeploymentKind.Container;

    /// <summary>offline 授權由客戶以系統參數自行啟用線上確認（§18.15.8）。只能加嚴，不能免除上面兩項。</summary>
    public bool LeaseOptedIn => BindingMode == LicenseBindingModes.Offline && OnlineCheckOptIn;

    /// <summary>實際生效：簽章要求 OR 執行期偵測到 VM／容器 OR 系統參數啟用。</summary>
    public bool LeaseRequired => LeaseRequiredBySignature || LeaseForcedByHost || LeaseOptedIn;
}

/// <summary>
/// 狀態判定（純函式，時間由呼叫端傳入）。
///
/// <para><b>單向加嚴</b>（§18.8，沿用 §15.2 約束 3）：只有已驗簽的撤銷能提前轉唯讀；
/// 連不到、看不懂一律不在這裡出現，只等期限自然到期。</para>
/// </summary>
public static class LicenseStateEvaluator
{
    /// <summary>唯讀之後多久轉停用。</summary>
    public static readonly TimeSpan DisableAfter = TimeSpan.FromDays(30);

    /// <summary>離線授權提早提醒（換發要人工往返）。</summary>
    public const int WarnDaysOffline = 30;

    /// <summary>租約／TPM／CI 授權的提醒門檻（§18.13 #14）。</summary>
    public const int WarnDaysOther = 14;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public static LicenseStatus Evaluate(LicenseStateInputs i, DateTimeOffset now)
    {
        if (i.SunsetActive)
            return new LicenseStatus(LicenseState.Unlocked,
                "授權服務已終止，所有授權檢查已永久解除。", null, null, null);

        // 唯讀起點：到期或撤銷，取早者
        DateTimeOffset? since = null;
        string? reason = null;
        if (now >= i.ExpiresAt)
        {
            since = i.ExpiresAt;
            reason = $"授權已於 {i.ExpiresAt:yyyy-MM-dd} 到期。";
        }
        if (i.RevokedAt is { } revokedAt && now >= revokedAt && (since is null || revokedAt < since))
        {
            since = revokedAt;
            reason = $"授權已於 {revokedAt:yyyy-MM-dd} 被撤銷（原因：{i.RevokedReason}）。";
        }

        if (i.LeaseRequired)
        {
            // 已簽章的 revoked／replaced：提前轉唯讀（單向加嚴；conflict 不算，§18.8）
            if (i.LeaseNoticeAt is { } nAt && (since is null || nAt < since))
            {
                since = nAt;
                reason = i.LeaseNoticeStatus == "replaced"
                    ? $"授權伺服器於 {nAt:yyyy-MM-dd} 通知：此授權已轉移到另一台機器。"
                    : $"授權伺服器於 {nAt:yyyy-MM-dd} 通知：此授權已被撤銷。";
            }
            if (i.LeaseNotAfter is { } leaseEnd && now >= leaseEnd && (since is null || leaseEnd < since))
            {
                since = leaseEnd;
                reason = $"授權確認已於 {leaseEnd:yyyy-MM-dd} 到期（超過期限未確認，或確認被拒）。";
            }
            // 時鐘回撥（§18.6）：本機時間比最近一份租約的伺服器時間早超過 1 天，視同租約到期——
            // 否則把時鐘調回去就能讓租約永遠不到期
            if (i.LeaseServerTime is { } st && now < st - TimeSpan.FromDays(1))
                return new LicenseStatus(LicenseState.ReadOnly,
                    $"本機時鐘（{now:yyyy-MM-dd HH:mm}）早於授權伺服器時間（{st:yyyy-MM-dd HH:mm}）超過 1 天，請校正系統時間。",
                    i.ExpiresAt, null, null);

            if (since is null && i.LeaseNotAfter is null && (i.LeaseRequiredBySignature || i.LeaseForcedByHost))
                // 從未取得租約：沒有可推導的起點，唯讀但不排停用日——連上伺服器即恢復。
                // 只限簽章要求與 VM 強制；系統參數自選的確認從未取得時只提醒（見下方警告），
                // 「連不上不轉唯讀、期限到了才唯讀」（§18.15.8）
                return new LicenseStatus(LicenseState.ReadOnly,
                    (i.LeaseForcedByHost && !i.LeaseRequiredBySignature
                        ? "偵測到本機為虛擬機器／容器，虛擬環境必須完成線上授權確認。"
                        : "尚未完成授權確認。") +
                    $"請管理員到「授權狀態」頁按「確認授權」（若設定為自動續租，請確認本機可連線至授權伺服器）。{(i.LeaseWarning is null ? "" : "\n" + i.LeaseWarning)}\n\n{LicenseSupport.HowToObtain}",
                    i.ExpiresAt, null, null);
        }

        if (since is { } s)
        {
            var disabledAt = s + DisableAfter;
            return now >= disabledAt
                ? new LicenseStatus(LicenseState.Disabled,
                    $"{reason}唯讀已滿 {DisableAfter.TotalDays:F0} 天，系統已停用。\n\n{LicenseSupport.HowToObtain}",
                    i.ExpiresAt, s, disabledAt)
                : new LicenseStatus(LicenseState.ReadOnly,
                    $"{reason}系統已轉為唯讀，{disabledAt:yyyy-MM-dd} 起停用。\n\n{LicenseSupport.HowToObtain}",
                    i.ExpiresAt, s, disabledAt);
        }

        var warnings = new List<string>();
        var warnDays = i.BindingMode == LicenseBindingModes.Offline ? WarnDaysOffline : WarnDaysOther;
        var daysLeft = (i.ExpiresAt - now).TotalDays;
        if (daysLeft < warnDays)
            warnings.Add($"授權將於 {i.ExpiresAt:yyyy-MM-dd} 到期（剩 {Math.Ceiling(daysLeft):F0} 天），{LicenseSupport.RenewShort}。");
        // 門檻：14 天與「租約長度一半」取短者。90 天租約 → 14 天；伺服器對個別授權縮短的租約
        // （例如 2 小時）若照 14 天算，會一取得就永遠在提醒
        if (i.LeaseRequired && i.LeaseNotAfter is { } notAfter
            && notAfter - now < Min(TimeSpan.FromDays(WarnDaysOther), (notAfter - (i.LeaseServerTime ?? notAfter.AddDays(-90))) / 2))
            warnings.Add($"授權確認將於 {notAfter:yyyy-MM-dd} 到期，屆時轉為唯讀。請管理員到「授權狀態」頁按「確認授權」（若設定為自動續租，請確認本機可連線至授權伺服器）。");
        if (i.LeaseRequired && i.LeaseNotAfter is null)
            warnings.Add("已啟用線上授權確認，但尚未完成確認。請管理員到「授權狀態」頁按「確認授權」。");
        if (i.LeaseRequired && i.LeaseWarning is not null)
            warnings.Add(i.LeaseWarning);
        if (warnings.Count > 0)
            return new LicenseStatus(LicenseState.Warning, string.Join("\n", warnings), i.ExpiresAt, null, null);

        return new LicenseStatus(LicenseState.Normal, "授權有效。", i.ExpiresAt, null, null);
    }
}

/// <summary>目前授權狀態。單例；寫入路徑（唯讀攔截）在每次存檔時讀它。</summary>
public interface ILicenseStateService
{
    /// <summary>依目前時間即時判定——到期那一刻就轉唯讀，不必等看門狗下一輪。</summary>
    LicenseStatus Current { get; }

    /// <summary>最近一次驗證通過的授權（狀態頁顯示用）；尚未驗證或終止解除後沒有授權檔時為 null。</summary>
    LicenseContext? License => null;

    /// <summary>以驗證通過的授權更新輸入（啟動時與看門狗每輪呼叫）。保留既有撤銷與終止狀態。</summary>
    void SetLicense(LicenseContext license);

    /// <summary>已驗簽的撤銷。只能加嚴：一旦記錄就不會被之後的「沒抓到清單」清掉。</summary>
    void SetRevoked(DateTimeOffset revokedAt, string reason);

    /// <summary>已驗簽的終止聲明。永久，不可逆。</summary>
    void SetSunset();

    /// <summary>取得（或續得）已驗簽租約。清除先前的續租警告。</summary>
    void SetLease(DateTimeOffset notAfter, DateTimeOffset serverTime) { }

    /// <summary>
    /// 已驗簽的 revoked／replaced 通知：提前轉唯讀，且只能加嚴——之後收到新租約也不會清除
    /// （伺服器端是終局狀態，要恢復須由我方處理後重新匯入授權或重啟）。
    /// </summary>
    void SetLeaseNotice(string status, DateTimeOffset at) { }

    /// <summary>不影響寫入的續租問題（conflict、seat-taken、連不上）。null 清除。</summary>
    void SetLeaseWarning(string? warning) { }

    /// <summary>目前的判定輸入（狀態頁與續租用）；尚未驗證授權時為 null。</summary>
    LicenseStateInputs? Inputs => null;

    /// <summary>實際是否需要線上確認（簽章、VM 偵測、系統參數三者取 OR，§18.15.8）。</summary>
    bool LeaseRequired => Inputs?.LeaseRequired ?? false;

    /// <summary>系統參數「線上授權確認」（資料庫）。只影響 offline 授權，且不能免除簽章要求與 VM 強制。</summary>
    void SetOnlineCheckOptIn(bool enabled) { }
}

public sealed class LicenseStateService : ILicenseStateService
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private LicenseStateInputs? _inputs;
    private LicenseContext? _license;

    public LicenseContext? License => Volatile.Read(ref _license);

    public LicenseStateInputs? Inputs => Volatile.Read(ref _inputs);

    public bool LeaseRequired => Inputs?.LeaseRequired ?? false;

    public LicenseStateService(TimeProvider time) => _time = time;

    public LicenseStatus Current
    {
        get
        {
            var inputs = Volatile.Read(ref _inputs);
            // 尚未驗證過授權（啟動中）：不擋。啟動流程本身會在驗證失敗時中止。
            return inputs is null
                ? new LicenseStatus(LicenseState.Normal, "授權尚未驗證。", null, null, null)
                : LicenseStateEvaluator.Evaluate(inputs, _time.GetUtcNow());
        }
    }

    public void SetLicense(LicenseContext license)
    {
        lock (_gate)
        {
            // 同一張授權（看門狗每輪重設）保留租約資訊；換了授權（匯入）則舊租約不再適用
            var sameLicense = _license is { } old && old.LicenseId == license.LicenseId;
            _license = license;
            var prev = _inputs;
            _inputs = new LicenseStateInputs(
                license.ExpiresAt,
                string.IsNullOrEmpty(license.BindingMode) ? LicenseBindingModes.Offline : license.BindingMode,
                prev?.RevokedAt, prev?.RevokedReason, prev?.SunsetActive ?? false,
                sameLicense ? prev?.LeaseNotAfter : null,
                sameLicense ? prev?.LeaseNoticeAt : null,
                sameLicense ? prev?.LeaseNoticeStatus : null,
                sameLicense ? prev?.LeaseWarning : null,
                sameLicense ? prev?.LeaseServerTime : null,
                license.HostCheck, license.HostKind,
                prev?.OnlineCheckOptIn ?? false);
        }
    }

    public void SetRevoked(DateTimeOffset revokedAt, string reason)
    {
        lock (_gate)
        {
            var prev = _inputs ?? throw new InvalidOperationException("尚未設定授權，無法記錄撤銷。");
            if (prev.RevokedAt is { } existing && existing <= revokedAt) return;
            _inputs = prev with { RevokedAt = revokedAt, RevokedReason = reason };
        }
    }

    public void SetLease(DateTimeOffset notAfter, DateTimeOffset serverTime)
    {
        lock (_gate)
        {
            var prev = _inputs ?? throw new InvalidOperationException("尚未設定授權，無法記錄租約。");
            // 伺服器時間只取高水位：舊租約（例如從備份還原的 lease.json）不能把它往回拉
            var high = prev.LeaseServerTime is { } h && h > serverTime ? h : serverTime;
            _inputs = prev with { LeaseNotAfter = notAfter, LeaseWarning = null, LeaseServerTime = high };
        }
    }

    public void SetLeaseNotice(string status, DateTimeOffset at)
    {
        lock (_gate)
        {
            var prev = _inputs ?? throw new InvalidOperationException("尚未設定授權，無法記錄通知。");
            if (prev.LeaseNoticeAt is { } existing && existing <= at) return;
            _inputs = prev with { LeaseNoticeAt = at, LeaseNoticeStatus = status };
        }
    }

    public void SetLeaseWarning(string? warning)
    {
        lock (_gate)
        {
            if (_inputs is { } prev) _inputs = prev with { LeaseWarning = warning };
        }
    }

    public void SetOnlineCheckOptIn(bool enabled)
    {
        lock (_gate)
        {
            if (_inputs is { } prev) _inputs = prev with { OnlineCheckOptIn = enabled };
        }
    }

    public void SetSunset()
    {
        lock (_gate)
        {
            // 終止聲明不需要授權檔（新安裝也免授權），所以可以在沒有輸入時設定
            _inputs = (_inputs ?? new LicenseStateInputs(DateTimeOffset.MaxValue, LicenseBindingModes.Offline))
                with { SunsetActive = true };
        }
    }
}

/// <summary>
/// /health 的授權項目：硬失敗（驗證例外、撤銷清單寬限用盡）沿用看門狗旗標；
/// 其餘依狀態——唯讀／停用 Unhealthy、提醒 Degraded。
/// </summary>
public sealed class LicenseHealthCheck(ILicenseStateService state)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!LicenseWatchdogService.IsHealthy)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                LicenseWatchdogService.LastError ?? "授權驗證失敗"));

        var status = state.Current;
        return Task.FromResult(status.State switch
        {
            LicenseState.ReadOnly or LicenseState.Disabled =>
                HealthCheckResult.Unhealthy(status.Reason),
            LicenseState.Warning =>
                HealthCheckResult.Degraded(status.Reason),
            _ => HealthCheckResult.Healthy(status.Reason),
        });
    }
}
