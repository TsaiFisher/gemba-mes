using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 重工成本分開統計（mfg-domain.md §16.4b）：工單實際成本結算加 ReworkLoss 欄位，並種入系統科目
    /// 5250 重工損失（與 `ACT_PeriodEndValuationAccounts` 同一套 `ON CONFLICT DO UPDATE` 語意：
    /// 撞到既有同代碼只補系統旗標與性質，不動名稱也不動 AllowManualEntry）。
    /// </summary>
    public partial class MFG_ReworkLossAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReworkLoss",
                table: "WorkOrderActualCosts",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(@"
                INSERT INTO ""ChartOfAccounts""
                    (""AccountCode"", ""AccountName"", ""AccountNameEn"", ""AccountType"",
                     ""IsSystem"", ""AllowManualEntry"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ('5250', '重工損失', 'Rework loss', 5,
                        TRUE, FALSE, TRUE, NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC')
                ON CONFLICT (""AccountCode"") DO UPDATE
                   SET ""IsSystem""      = TRUE,
                       ""AccountType""   = EXCLUDED.""AccountType"",
                       ""AccountNameEn"" = COALESCE(""ChartOfAccounts"".""AccountNameEn"", EXCLUDED.""AccountNameEn""),
                       ""UpdatedAt""     = NOW() AT TIME ZONE 'UTC';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ⚠️ 有損：升級前就自建過 5250 的，ON CONFLICT 之後已無從分辨，會一起刪掉。
            migrationBuilder.Sql(@"DELETE FROM ""ChartOfAccounts"" WHERE ""AccountCode"" = '5250';");

            migrationBuilder.DropColumn(
                name: "ReworkLoss",
                table: "WorkOrderActualCosts");
        }
    }
}
