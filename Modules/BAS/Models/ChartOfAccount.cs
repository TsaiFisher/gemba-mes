using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 會計科目主檔（全公司共用，不分廠）。
    ///
    /// <para><b>沿革</b>：這張表原本叫 <c>InvoiceAccount</c>「發票科目主檔」，只涵蓋手動 AP／AR
    /// 發票可選的費用／收入科目，並且在類別註解裡明文寫著「這不是完整的會計科目表，刻意不是」。
    /// 當時的理由成立：系統自動分錄的科目由程式決定，做成可維護的主檔等於讓使用者改存貨科目。</para>
    ///
    /// <para><b>2026-09-21 升級為完整科目表</b>，但那個理由沒有被推翻——它變成
    /// <see cref="IsSystem"/> 與 <see cref="AllowManualEntry"/> 兩個旗標：
    /// 系統科目照樣由 <c>StandardAccounts</c> 決定、以種子寫入、代碼與性質不可改，
    /// 使用者能改的只有名稱與備註；而「哪些科目可以手動選用」由旗標表達，不是由「這張表裡有沒有」表達。</para>
    ///
    /// <para><b>為什麼不另開一張</b>：另開等於同時存在兩份科目定義，
    /// 而本檔原版的註解早就寫過那個後果——「科目是哪來的」會有兩個答案。</para>
    /// </summary>
    public class ChartOfAccount
    {
        /// <summary>
        /// 科目代碼。既是主鍵，也是分錄上留下的痕跡：<c>JournalLine.AccountCode</c> 存的是
        /// 當下寫進去的字串，改代碼會讓歷史傳票指向一個不存在的科目，所以建立後不可修改。
        /// </summary>
        [Key, MaxLength(20)]
        public string AccountCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string AccountName { get; set; } = string.Empty;

        /// <summary>英文名稱；未填時英文介面退回 <see cref="AccountName"/>。</summary>
        [MaxLength(100)]
        public string? AccountNameEn { get; set; }

        /// <summary>科目性質，決定借貸方向與落在哪一張報表的哪一段。</summary>
        public AccountType AccountType { get; set; } = AccountType.OperatingExpense;

        /// <summary>
        /// 系統科目：由 <c>StandardAccounts</c> 定義、程式自動分錄會用到。
        /// 不可刪除、代碼與性質不可修改——改了之後自動分錄產生的傳票會落在錯誤的報表段落，
        /// 而那不會有任何錯誤訊息。
        /// </summary>
        public bool IsSystem { get; set; }

        /// <summary>
        /// 可否在手動輸入的畫面（手動 AP／AR 發票、手動傳票）選用。
        /// 系統科目預設為否：存貨、暫估應付這些科目的餘額是自動分錄維護的，
        /// 手動打一筆進去就會與子帳對不起來（`WO_COST_JOURNAL_DRIFT` 之類的稽核會叫，但那是事後）。
        /// </summary>
        public bool AllowManualEntry { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 科目性質。
    ///
    /// <para><b>數值刻意對齊科目代碼的首碼</b>（1xxx 資產、2xxx 負債、4xxx 收入、5xxx 銷貨成本…）。
    /// 這不是巧合湊出來的：在主檔存在之前，<c>BalanceSheetService</c> 與 <c>ProfitLossService</c>
    /// 就是**直接讀代碼第一個字元**來分類的。數值對齊之後，把分類改為讀主檔這件事
    /// 對既有資料是恆等變換——不必逐筆判斷「這個科目本來被算在哪一段」。</para>
    ///
    /// <para>⚠️ 原 <c>InvoiceAccountType</c> 是 <c>Expense = 1, Revenue = 2</c>，
    /// 與新編號衝突（1 現在是資產）。升級的 migration 內含資料轉換，見
    /// <c>BAS_ChartOfAccountMaster</c>。</para>
    /// </summary>
    public enum AccountType
    {
        /// <summary>資產（1xxx）。借方為正。</summary>
        Asset = 1,

        /// <summary>負債（2xxx）。貸方為正。</summary>
        Liability = 2,

        /// <summary>權益（3xxx）。貸方為正。</summary>
        Equity = 3,

        /// <summary>營業收入（4xxx）。貸方為正。</summary>
        Revenue = 4,

        /// <summary>銷貨成本（5xxx）。借方為正。</summary>
        Cogs = 5,

        /// <summary>營業費用（6xxx）。借方為正。</summary>
        OperatingExpense = 6,

        /// <summary>營業外收入（7xxx）。貸方為正。</summary>
        OtherIncome = 7,

        /// <summary>營業外費用（8xxx）。借方為正。</summary>
        OtherExpense = 8,
    }

    public static class AccountTypeExtensions
    {
        /// <summary>落在資產負債表（1–3）而非損益表（4–8）。</summary>
        public static bool IsBalanceSheet(this AccountType t)
            => t is AccountType.Asset or AccountType.Liability or AccountType.Equity;

        /// <summary>
        /// 借方為正（資產、成本、費用）。貸方為正的是負債、權益、收入。
        /// 報表的正負號一律由這個判斷來，不再各自寫一份 switch。
        /// </summary>
        public static bool IsDebitNormal(this AccountType t)
            => t is AccountType.Asset or AccountType.Cogs
                 or AccountType.OperatingExpense or AccountType.OtherExpense;

        /// <summary>可作為手動 AP 發票借方的費用類科目。</summary>
        public static bool IsExpenseLike(this AccountType t)
            => t is AccountType.Cogs or AccountType.OperatingExpense or AccountType.OtherExpense;

        /// <summary>可作為手動 AR 發票貸方的收入類科目。</summary>
        public static bool IsRevenueLike(this AccountType t)
            => t is AccountType.Revenue or AccountType.OtherIncome;
    }
}
