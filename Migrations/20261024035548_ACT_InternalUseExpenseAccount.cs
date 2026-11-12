using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// 系統科目 5260 內部領用費用（2026-10-24 使用者裁決）：內部訂單領用（NO）借記、退料（NR）貸回
    /// （InventoryPostingService.BuildJournalLines）。分錄科目要在科目主檔存在（參照欄位檢查），否則領用過帳直接失敗。
    /// 既有已過帳的領用仍在 5210 報廢損失，不回溯重分類。
    /// </summary>
    public partial class ACT_InternalUseExpenseAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""ChartOfAccounts""
                    (""AccountCode"", ""AccountName"", ""AccountNameEn"", ""AccountType"",
                     ""IsSystem"", ""AllowManualEntry"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ('5260', '內部領用費用', 'Internal use expense', 5,
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
            // ⚠️ 有損：升級前就自建過 5260 的，ON CONFLICT 之後已無從分辨，會一起刪掉。
            migrationBuilder.Sql(@"DELETE FROM ""ChartOfAccounts"" WHERE ""AccountCode"" = '5260';");
        }
    }
}
