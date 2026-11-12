using System.Security;

namespace SmartFactory.MES.Services;

/// <summary>
/// 主機識別碼閘門（SECURITY-OPERATIONS.md §18.15.9，2026-10-17 起）：授權有列主機識別碼時，
/// <b>讀不到或不符一律不解密連線字串、不啟動主系統</b>，只開授權頁（主專案的 <c>LicenseBlockedHost</c>）。
///
/// <para>改版前是「不符轉唯讀、讀不到只提醒」——唯讀仍可登入看到全部資料，而複製出來的環境
/// 連 Security DLL 一起帶走，擋不住。資料是客戶最重要的資產，被複製的環境不應顯示任何資料。</para>
///
/// <para>以 <c>&lt;Compile Include Link&gt;</c> 共用於主專案與 Security DLL：兩邊擋下時拋同一種形狀的例外，
/// 啟動流程以 <see cref="ReasonOf"/> 辨識（DLL 的例外經反射包裝，型別不能拿來判斷）。</para>
/// </summary>
public static class LicenseHostGate
{
    /// <summary>例外 <c>Data</c> 的鍵：值為 <see cref="Mismatch"/> 或 <see cref="Unavailable"/>。</summary>
    public const string DataKey = "SmartFactory.LicenseHostGate";

    /// <summary>讀得到主機識別碼，但不在授權清單內：授權不屬於這台電腦。</summary>
    public const string Mismatch = "mismatch";

    /// <summary>讀不到主機識別碼：無法確認是授權機器。也要擋——否則停用讀取管道（例如關掉 WSL interop）就能繞過。</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// 比對；授權沒有列主機時不呼叫 <paramref name="detect"/>（WSL 上它會啟動 reg.exe）並回 null。
    /// 通過時回傳偵測結果，不通過拋 <see cref="SecurityException"/>。
    /// </summary>
    public static HostIdentityResult? Enforce(string[]? hostIds, Func<HostIdentityResult> detect)
    {
        if (hostIds is not { Length: > 0 }) return null;
        var host = detect();
        if (host.HostId is null)
            throw Blocked(Unavailable,
                "無法讀取主機識別碼，無法確認本機是授權機器——系統不啟動、不連線資料庫。\n" + host.Problem);
        if (!hostIds.Contains(host.HostId, StringComparer.OrdinalIgnoreCase))
            throw Blocked(Mismatch,
                "此授權不屬於這台電腦（主機識別碼不符）——系統不啟動、不連線資料庫。\n" +
                "若已換電腦或主機板，請以授權頁顯示的申請碼重新申請授權。");
        return host;
    }

    public static SecurityException Blocked(string reason, string message)
    {
        var ex = new SecurityException(message);
        ex.Data[DataKey] = reason;
        return ex;
    }

    /// <summary>從例外（含 TargetInvocationException 等包裝）取出閘門原因；不是閘門擋的回 null。</summary>
    public static string? ReasonOf(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex.Data[DataKey] is string reason) return reason;
        return null;
    }
}
