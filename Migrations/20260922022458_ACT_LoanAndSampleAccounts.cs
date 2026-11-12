using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 借入物料與樣品費兩個系統科目的種子（無 schema 變更，純資料）。
    ///
    /// <para>起因：`BuildJournalLines` 原本沒有 `Loan`／`Sample` 分支，六個異動類別
    /// （IB/BR/IL/LR/CI/CR）全部落到 fallback——借入貸「進貨暫估應付」、
    /// 還料與借出借「報廢損失」。補上正確分支後需要這兩個科目存在，
    /// 否則自動分錄會過帳到一個畫面上查不到的代碼（`ChartOfAccountTests`
    /// 的種子守門正是為此存在）。</para>
    ///
    /// <para>與 `BAS_ChartOfAccountMaster` 同一套 `ON CONFLICT DO UPDATE` 語意：
    /// 撞到既有同代碼只補系統旗標與性質，**不動名稱也不動 AllowManualEntry**
    /// ——使用者可能早就自建 2170 並在用。</para>
    /// </summary>
    public partial class ACT_LoanAndSampleAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""ChartOfAccounts""
                    (""AccountCode"", ""AccountName"", ""AccountNameEn"", ""AccountType"",
                     ""IsSystem"", ""AllowManualEntry"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                SELECT v.code, v.name_zh, v.name_en, v.acct_type,
                       TRUE, FALSE, TRUE, NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC'
                  FROM (VALUES
                    ('2170', '其他應付款-借入物料', 'Other payables - borrowed materials', 2),
                    ('5230', '樣品費', 'Sample expense', 5)
                  ) AS v(code, name_zh, name_en, acct_type)
                ON CONFLICT (""AccountCode"") DO UPDATE
                   SET ""IsSystem""      = TRUE,
                       ""AccountType""   = EXCLUDED.""AccountType"",
                       ""AccountNameEn"" = COALESCE(""ChartOfAccounts"".""AccountNameEn"", EXCLUDED.""AccountNameEn""),
                       ""UpdatedAt""     = NOW() AT TIME ZONE 'UTC';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 只刪這兩個代碼，不像 BAS_ChartOfAccountMaster 的 Down 那樣掃掉所有 IsSystem。
            // ⚠️ 仍是有損的：若使用者升級前就自建過 2170，ON CONFLICT 之後已無從分辨
            // 「本來就有」與「這次插入」，這一步會一起刪掉。
            migrationBuilder.Sql(
                @"DELETE FROM ""ChartOfAccounts"" WHERE ""AccountCode"" IN ('2170', '5230');");
        }
    }
}
