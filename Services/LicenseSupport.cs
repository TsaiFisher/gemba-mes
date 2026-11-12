namespace SmartFactory.MES.Services;

/// <summary>
/// 授權相關錯誤訊息共用的「該找誰」文案。
///
/// <para><b>為什麼要抽出來：</b>原本所有訊息一律寫「請聯繫 SmartFactory」——
/// 使用者通常是透過導入顧問拿到系統，看不懂那是誰、也不知道要去哪，
/// 而且出現在他最需要指引的那一刻（手上還沒有授權的時候）。</para>
///
/// <para>只有一條路：受管授權（免費、需登記，SECURITY-OPERATIONS.md §18）。
/// 2026-10 曾有的「社群授權」已取消，不要再加回第二條路。</para>
///
/// <para><b>⚠️ 這段文字有第二份複本：</b><c>tools/get-fingerprint.sh</c>。
/// 那支腳本是「機器上沒有 .NET runtime」時的備援，是 shell，引用不到這個常數。
/// 改這裡的話請一併 grep <c>smartfactory-licensing</c>。</para>
/// </summary>
internal static class LicenseSupport
{
    /// <summary>
    /// 例外訊息用。使用者看到它的時機是系統起不來，所以給出找誰與去哪查。
    /// </summary>
    internal const string HowToObtain =
        "請在要執行系統的設備上執行 SmartFactory.MES.MachineId 取得申請碼，開啟印出的登記網址申請\n" +
        "（或將申請碼交給您的授權提供者／導入顧問）。\n" +
        "申請方式：https://github.com/TsaiFisher/smartfactory-licensing";

    /// <summary>
    /// 日誌用的短版，**刻意不含網址**。到期提醒每 24 小時寫一次
    /// （<see cref="LicenseWatchdogService"/>），塞兩行網址進去只會洗版，
    /// 而會看日誌的人本來就知道要找誰。
    /// </summary>
    internal const string RenewShort = "請儘速換發授權";

    /// <summary>永久授權的到期日哨兵值（SECURITY-OPERATIONS.md §18.15.1）：不改簽章格式，到期判斷自然失效。</summary>
    internal static readonly DateTimeOffset PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    internal static bool IsPerpetual(DateTimeOffset expiresAt) => expiresAt.Year >= 9999;

    /// <summary>畫面與日誌顯示到期日：永久授權顯示「永久」。</summary>
    internal static string FormatExpiry(DateTimeOffset expiresAt)
        => IsPerpetual(expiresAt) ? "永久" : expiresAt.ToString("yyyy-MM-dd");
}
