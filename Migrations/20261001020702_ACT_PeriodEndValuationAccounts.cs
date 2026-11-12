using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 期末評價入帳的四個系統科目（無 schema 變更，純資料）。
    ///
    /// <para>起因：外幣重評、NRV、ECL 原本只寫評估表，月結步驟 9 只是打勾。
    /// `PeriodEndAdjustmentService` 把它們入總帳後需要備抵與損失科目：
    /// 1179 備抵損失-應收帳款、1419 備抵存貨跌價損失、5240 存貨跌價損失、6250 預期信用減損損失
    /// （外幣重評沿用既有 4190／5199）。</para>
    ///
    /// <para>與 `ACT_LoanAndSampleAccounts` 同一套 `ON CONFLICT DO UPDATE` 語意：
    /// 撞到既有同代碼只補系統旗標與性質，**不動名稱也不動 AllowManualEntry**。</para>
    /// </summary>
    public partial class ACT_PeriodEndValuationAccounts : Migration
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
                    ('1179', '備抵損失-應收帳款', 'Loss allowance - accounts receivable', 1),
                    ('1419', '備抵存貨跌價損失', 'Allowance for inventory valuation loss', 1),
                    ('5240', '存貨跌價損失', 'Inventory write-down loss', 5),
                    ('6250', '預期信用減損損失', 'Expected credit impairment loss', 6)
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
            // 只刪這四個代碼。⚠️ 有損：升級前就自建過同代碼的，ON CONFLICT 之後已無從分辨，會一起刪掉。
            migrationBuilder.Sql(
                @"DELETE FROM ""ChartOfAccounts"" WHERE ""AccountCode"" IN ('1179', '1419', '5240', '6250');");
        }
    }
}
