using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.ACT.ViewModels;

/// <summary>
/// 試算表的一列＝一個科目。
///
/// <para><b>期初 ＋ 本期借 − 本期貸 ＝ 期末</b>（借方為正的科目；貸方為正的反向）。
/// 這個等式對每一列都必須成立，而且全表的「本期借方合計」必須等於「本期貸方合計」
/// ——後者才是試算表存在的理由。</para>
/// </summary>
public class TrialBalanceRowVm
{
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;

    /// <summary>科目性質；主檔查無時由代碼首碼推定（見 <c>AccountTypeMap.FromLeadingDigit</c>）。</summary>
    public AccountType? AccountType { get; set; }

    /// <summary>
    /// 期初餘額＝起期**之前**的累計，已依科目性質定好正負號（借方為正的科目借餘為正）。
    ///
    /// <para>⚠️ 損益類科目也有期初餘額，因為本系統**刻意不做期末結轉**——4xxx／5xxx
    /// 不會被歸零轉進保留盈餘。所以跨年度查詢時，損益科目的期初是「有史以來的累計」，
    /// 不是 0。這不是錯誤，是這套帳的設計，資產負債表上那張「本期損益（未結轉）」
    /// 卡片表達的是同一件事。</para>
    /// </summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>本期借方發生額（原始金額，不帶正負號慣例）。</summary>
    public decimal PeriodDebit { get; set; }

    /// <summary>本期貸方發生額（原始金額，不帶正負號慣例）。</summary>
    public decimal PeriodCredit { get; set; }

    /// <summary>期末餘額＝期初 ± 本期發生，正負號慣例同 <see cref="OpeningBalance"/>。</summary>
    public decimal ClosingBalance { get; set; }
}

/// <summary>
/// 試算表頁面模型。
///
/// <para><b>合計不是裝飾</b>：<see cref="TotalDebit"/> 與 <see cref="TotalCredit"/> 相等，
/// 是「這段期間的傳票整體平衡」的唯一整表證據。既有的
/// <c>DataAuditService.AuditJournalBalanceAsync</c> 驗的是**逐傳票**平衡，
/// 而每張傳票都平衡**不等於**科目餘額是對的——漏過帳、重複過帳、科目代碼寫錯，
/// 逐傳票那一層全部看不到。</para>
/// </summary>
public class TrialBalanceVm
{
    public string FromPeriod { get; set; } = string.Empty;
    public string ToPeriod   { get; set; } = string.Empty;

    public List<TrialBalanceRowVm> Rows { get; set; } = new();

    public decimal TotalDebit  => Rows.Sum(r => r.PeriodDebit);
    public decimal TotalCredit => Rows.Sum(r => r.PeriodCredit);

    /// <summary>借貸差額；非 0 即代表這段期間的分錄整體不平衡，畫面必須顯著標示。</summary>
    public decimal Difference => TotalDebit - TotalCredit;

    public bool IsBalanced => Difference == 0m;
}
