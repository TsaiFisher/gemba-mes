using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.ViewModels;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.ACT.Reporting;

/// <summary>
/// 試算表的請求。期間是**會計期間代碼區間**（yyyyMM），與資產負債表／損益表同一種形狀。
/// </summary>
public class TrialBalanceRequest : ReportRequestBase
{
    /// <summary>起期（含）。留空＝從有史以來，期初餘額因而為 0。</summary>
    public string? FromPeriod { get; set; }

    /// <summary>迄期（含）。</summary>
    public string? ToPeriod { get; set; }

    /// <summary>是否列出本期與期初都為 0 的科目。預設不列——那是一整頁的零。</summary>
    public bool IncludeZeroRows { get; set; }
}

/// <summary>
/// 試算表（ACT）。
///
/// <para><b>它補的缺口</b>：<c>DataAuditService.AuditJournalBalanceAsync</c> 驗的是
/// **逐傳票**借貸平衡，而「每張傳票都平衡」不等於「科目餘額是對的」——漏過帳、重複過帳、
/// 科目代碼打錯，在逐傳票那一層全部看不見。傳票要匯出到外部會計系統之前，
/// 需要的是科目層級的對帳表，而在這支之前系統沒有。</para>
///
/// <para><b>刻意不做年度結轉</b>：本系統是管理日記帳，帳務主體在外部 ERP，
/// 期末結轉屬於總帳的職責，在這裡做會與外部 ERP 各出一套結轉分錄。
/// 因此損益科目**不會**在年度交界被歸零，它們的期初餘額是有史以來的累計——
/// 與資產負債表上「本期損益（未結轉）」那張卡片表達的是同一件事。</para>
/// </summary>
public class TrialBalanceTool
    : IReportTool<TrialBalanceRequest, TrialBalanceVm>, IReportToolDescriptor
{
    private readonly ApplicationDbContext _db;
    private readonly IAccountClassifier   _accounts;

    public TrialBalanceTool(ApplicationDbContext db, IAccountClassifier accounts)
    {
        _db       = db;
        _accounts = accounts;
    }

    public string ToolKey     => "TrialBalance";
    public string Description =>
        "試算表：依會計期間區間列出每個科目的期初餘額、本期借貸發生額與期末餘額，"
        + "並給出全表借貸方合計是否相等。含金額。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>（<c>TrialBalanceController</c>）。</summary>
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>整張表都是科目餘額 → 屬 §八「含金額＝機密」。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>
    /// 期間由 <c>FromPeriod</c>／<c>ToPeriod</c> 直接表達（會計期間代碼），
    /// **不經 <c>IRelativePeriodResolver</c>**——與資產負債表／損益表兩支工具同一個理由：
    /// 會計期間可自訂起訖，把它攤成日期再轉回來會在自訂期間的廠別指到錯的期別。
    /// </summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(TrialBalanceRequest);
    public Type ResponseType => typeof(TrialBalanceVm);

    public async Task<TrialBalanceVm> ExecuteAsync(
        TrialBalanceRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var from    = request.FromPeriod ?? "";
        var to      = request.ToPeriod   ?? "";
        var plantId = request.PlantId ?? "";

        if (string.IsNullOrWhiteSpace(to))
            return new TrialBalanceVm { FromPeriod = from, ToPeriod = to };

        // IgnoreQueryFilters ＋ 明確廠別條件：理由同 BalanceSheetService——
        // JournalEntry 是 IPlantEntity，全域過濾會把「不是操作者當下這一廠」的分錄
        // 整批濾掉，而這裡的 plantId 就是明確的廠別閘門，資料範圍不因此放寬。
        var q = _db.JournalLines.IgnoreQueryFilters()
            .Where(l => l.JournalEntry!.PlantId == plantId
                     && string.Compare(l.JournalEntry.PeriodId, to) <= 0);

        // 期初與本期在同一次查詢裡分流：兩次查資料庫會在「哪些廠、哪個期間」這兩個
        // 條件上慢慢分岔，而分岔的結果是期初與本期對不起來、期末卻照樣算得出數字。
        var raw = await q
            .GroupBy(l => new
            {
                l.AccountCode,
                l.AccountName,
                IsOpening = string.IsNullOrEmpty(from)
                            ? false
                            : string.Compare(l.JournalEntry!.PeriodId, from) < 0,
            })
            .Select(g => new
            {
                g.Key.AccountCode,
                g.Key.AccountName,
                g.Key.IsOpening,
                Debit  = g.Sum(x => x.Debit),
                Credit = g.Sum(x => x.Credit),
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var accountTypes = await _accounts.LoadAsync(ct);

        var rows = raw
            .GroupBy(r => r.AccountCode)
            .Select(g =>
            {
                // 同一科目在不同傳票上的名稱理論上一致；取非空的第一個，避免歷史空值蓋掉名稱。
                var name = g.Select(x => x.AccountName)
                            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
                var type = accountTypes.Resolve(g.Key);

                var openDebit   = g.Where(x => x.IsOpening).Sum(x => x.Debit);
                var openCredit  = g.Where(x => x.IsOpening).Sum(x => x.Credit);
                var periodDebit = g.Where(x => !x.IsOpening).Sum(x => x.Debit);
                var periodCredit= g.Where(x => !x.IsOpening).Sum(x => x.Credit);

                // 正負號由科目性質決定，不再各自寫一份 switch（同 BalanceSheetService）。
                // 分類不出來的科目一律當借方為正——它們在報表上本來就進不了任何一段，
                // 這裡至少讓它出現在試算表上被看見，而不是靜默消失。
                var debitNormal = type?.IsDebitNormal() ?? true;

                decimal Signed(decimal d, decimal c) => debitNormal ? d - c : c - d;

                var opening = Signed(openDebit, openCredit);
                var closing = Signed(openDebit + periodDebit, openCredit + periodCredit);

                return new TrialBalanceRowVm
                {
                    AccountCode    = g.Key,
                    AccountName    = name,
                    AccountType    = type,
                    OpeningBalance = opening,
                    PeriodDebit    = periodDebit,
                    PeriodCredit   = periodCredit,
                    ClosingBalance = closing,
                };
            })
            .Where(r => request.IncludeZeroRows
                     || r.OpeningBalance != 0 || r.PeriodDebit != 0
                     || r.PeriodCredit != 0 || r.ClosingBalance != 0)
            .OrderBy(r => r.AccountCode, StringComparer.Ordinal)
            .ToList();

        return new TrialBalanceVm { FromPeriod = from, ToPeriod = to, Rows = rows };
    }

    /// <summary>匯出欄位（§16.1 S-5）。畫面匯出與排程匯出共用這一份。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<TrialBalanceRowVm>("TrialBalance.AccountCode",    r => r.AccountCode),
        Column<TrialBalanceRowVm>("TrialBalance.AccountName",    r => r.AccountName),
        Column<TrialBalanceRowVm>("TrialBalance.OpeningBalance", r => r.OpeningBalance),
        Column<TrialBalanceRowVm>("TrialBalance.PeriodDebit",    r => r.PeriodDebit),
        Column<TrialBalanceRowVm>("TrialBalance.PeriodCredit",   r => r.PeriodCredit),
        Column<TrialBalanceRowVm>("TrialBalance.ClosingBalance", r => r.ClosingBalance),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列。回應是包裝型別（含合計），讓 Runner 自己攤平會產出
    /// 零筆的空檔案——契約註解點名的那個形狀。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<TrialBalanceRequest>(ToolKey), user, ct))
           .Rows.Cast<object>().ToList();
}
