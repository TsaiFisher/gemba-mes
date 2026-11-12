namespace SmartFactory.MES.Modules.RPS.Contracts;

/// <summary>
/// 既有報表畫面「字串日期」的解析語意，**原樣保留**在這一個地方。
///
/// 為什麼要有這個類別：抽 <see cref="IReportTool{TRequest,TResponse}"/> 時，
/// 字串日期不得滲入工具契約（§5.2.1 實作提醒），但畫面的查詢列送上來的就是字串，
/// 且它現行的解析語意有兩個「不直覺但已在線上跑」的行為。把它們集中在這裡，
/// 工具本身就能是乾淨的強型別，而這些歷史語意也只有一處可看、可改、可測。
///
/// 現行語意（三支有日期區間的報表 SalesRanking／OrderOverdue／ReturnRate 完全一致）：
///
/// 1. **起日解析失敗 → <c>default(DateOnly)</c>（0001-01-01）**，等於不限起日。
///    查詢列留白就是這條路徑，這是使用者實際依賴的行為。
/// 2. **迄日解析失敗 → 今天**。所以留白迄日時，**未來日期的單會靜默消失**
///    （預約單、預計出貨日在未來的訂單都查不到）。
///
/// ⚠️ 第 2 點的「今天」取的是**伺服器時區**而非廠別時區
/// （<c>IDateTimeService.GetLocalDate</c>），跨時區部署時會差一天。這是刻意保留的現況：
/// 改掉它是行為變更，必須單獨決議與驗證，不可混在「抽成 Service」這種應該行為不變的
/// 重構裡——那樣沒有人會發現數字變了。新寫的查詢一律走 <see cref="RelativePeriod"/>。
/// </summary>
public static class LegacyQueryPeriod
{
    /// <inheritdoc cref="LegacyQueryPeriod"/>
    /// <param name="fallbackTo">
    /// 迄日解析失敗時採用的日期。**刻意做成必填參數而不在此讀時鐘**：
    /// 這是 R22 明文禁止的伺服器時鐘取業務日，既有三支報表 Controller 已在
    /// <c>baseline-R22.txt</c> 認列該筆技術債。若在這個共用檔內部呼叫
    /// <c>DateTime.Today</c>，等於把那筆債從「三支已認列的畫面」擴散成
    /// 一個新的、所有未來呼叫端都會沾到的共用違規點。時鐘讀取留在 Controller 邊界。
    /// </param>
    public static DateRange Parse(string? fromDate, string? toDate, DateOnly fallbackTo)
    {
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = fallbackTo;
        return new DateRange(start, end);
    }
}
