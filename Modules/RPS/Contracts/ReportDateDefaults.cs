namespace SmartFactory.MES.Modules.RPS.Contracts;

/// <summary>
/// 「期間只是篩選」那一類報表的查詢列預設值。
///
/// <para><b>要解決的是兩個都沒有訊號的行為</b>（見 <see cref="LegacyQueryPeriod"/>）：
/// 起日留白會被解析成 <c>default(DateOnly)</c>＝0001-01-01，等於不限起日、掃遍全部歷史；
/// 迄日留白退回「今天」，於是預交日或出貨日在未來的單會靜默消失。兩者畫面上都看不出來。</para>
///
/// <para><b>為什麼是預設值而不是必填</b>：這一類報表留白時數字仍然正確，只是範圍含糊且查詢變慢——
/// 與稼動率／庫存週轉率那種「期間本身就是分母」的情況不同，後者留白是**錯的數字**，必須擋。</para>
///
/// <para><b>今日一律由呼叫端傳入</b>，且必須來自 <c>IDateTimeService.GetLocalDate()</c>
/// 而非 <c>DateTime.Today</c>：後者是伺服器時區，與廠別時區不同時會整整差一天（R22）。
/// 刻意做成必填參數，本檔內不讀時鐘，免得替所有呼叫端製造一個新的共用違規點。</para>
/// </summary>
public static class ReportDateDefaults
{
    /// <summary>起日留白時回溯的天數（含今日共 30 天）。</summary>
    public const int DefaultDays = 30;

    /// <summary>
    /// 補齊查詢列的起訖日。已填的原樣保留——使用者明確給的期間永遠優先。
    /// </summary>
    /// <param name="today">廠別當地今日（<c>IDateTimeService.GetLocalDate()</c>）。</param>
    public static (string FromDate, string ToDate) Apply(
        string? fromDate, string? toDate, DateOnly today, int days = DefaultDays)
    {
        var to = string.IsNullOrWhiteSpace(toDate) ? today : ParseOr(toDate, today);

        // AddDays 在接近 DateOnly.MinValue 時會擲 ArgumentOutOfRangeException。正式環境走不到，
        // 但測試裡沒設定的 IDateTimeService 替身回的就是 default(DateOnly)＝0001-01-01——
        // 那會變成一個與本函式無關的例外，掩蓋掉測試真正要驗的東西。
        var back    = to.DayNumber - (days - 1);
        var earlier = back < DateOnly.MinValue.DayNumber ? DateOnly.MinValue : DateOnly.FromDayNumber(back);

        var from = string.IsNullOrWhiteSpace(fromDate) ? earlier : ParseOr(fromDate, earlier);
        return (from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
    }

    // 解析不掉的字串（使用者手改網址、舊書籤）視同未填，而不是悄悄變成 0001-01-01。
    private static DateOnly ParseOr(string? raw, DateOnly fallback)
        => DateOnly.TryParse(raw, out var d) ? d : fallback;
}
