using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>
/// 絕對日期區間 → 會計期別（<c>AccountingPeriod.PeriodId</c>，格式 <c>YYYYMM</c>）。
///
/// <para><b>為什麼需要這一層</b>：損益表與資產負債表的 service 收的是**期別字串**，
/// 而排程契約（<see cref="ReportRequestBase"/>）講的是**日期區間**。兩者之間必須有一次轉換，
/// 而轉換不可以用 <c>range.From.ToString("yyyyMM")</c> 這種字串格式化打發——
/// 會計期間是資料（`AccountingPeriod` 表，依廠別分開），**不保證等於日曆月**。
/// 用日曆月硬湊，在會計行事曆與日曆不一致的廠會安靜地查到隔壁期的數字。</para>
///
/// <para>⚠️ <b>查無涵蓋期間時拋例外，不退回日曆月</b>——與
/// <c>RelativePeriodResolver.FiscalAsync</c> 同一個判斷（該處註解：「刻意不退回日曆月，
/// 以免產出與帳務口徑不符的數字」）。排程收到例外會寫進 <c>ReportRun.ErrorMessage</c>；
/// 退回日曆月則會寄出一份看起來正常、口徑卻不對的報表。</para>
///
/// <para>⚠️ 刻意<b>不</b>繞過廠別查詢過濾器：期間依廠別分開之後，要的是本廠的會計行事曆。
/// 排程路徑必須在 scope 內設好 <c>CurrentPlantId</c>，否則這裡會查到 0 筆並拋例外——
/// 那是對的，總比靜默取到別廠的期別好。</para>
/// </summary>
public sealed class FiscalPeriodMapper
{
    private readonly ApplicationDbContext _db;

    public FiscalPeriodMapper(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// 區間涵蓋到的所有會計期別，回傳最早與最晚那一個。
    /// 區間只要與某一期有交集就算涵蓋（<c>StartDate &lt;= range.To &amp;&amp; range.From &lt;= EndDate</c>）。
    /// </summary>
    public async Task<(string From, string To)> RangeAsync(DateRange range, CancellationToken ct = default)
    {
        var ids = await PeriodIdsAsync(range, ct);
        return (ids[0], ids[^1]);
    }

    /// <summary>
    /// 「截至」單一期別：取區間**迄日**所落在的那一期。
    ///
    /// <para>用迄日而不是起日是刻意的：資產負債表是時點報表，
    /// 相對期間「上個月」要的是「截至上月月底」的餘額，不是月初的。</para>
    /// </summary>
    public async Task<string> AsOfAsync(DateRange range, CancellationToken ct = default)
        => (await PeriodIdsAsync(range, ct))[^1];

    private async Task<List<string>> PeriodIdsAsync(DateRange range, CancellationToken ct)
    {
        var ids = await _db.AccountingPeriods
            .AsNoTracking()
            .Where(p => p.StartDate <= range.To && range.From <= p.EndDate)
            .OrderBy(p => p.PeriodId)
            .Select(p => p.PeriodId)
            .ToListAsync(ct);

        if (ids.Count == 0)
            throw new InvalidOperationException(
                PzText.Get("Rpt_NoPeriodOverlapFmt", range));

        return ids;
    }
}
