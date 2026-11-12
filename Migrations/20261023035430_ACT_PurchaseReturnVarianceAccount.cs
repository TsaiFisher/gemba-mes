using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 系統科目 5152 進貨退出價差（2026-10-23）：進貨退出時存貨照現值出、進貨暫估應付照原進貨單價沖，
    /// 差額落此科目（InventoryPostingService.BuildPurchaseReturnLinesAsync）。分錄科目要在科目主檔存在
    /// （參照欄位檢查），否則退貨過帳直接失敗。
    /// </summary>
    public partial class ACT_PurchaseReturnVarianceAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""ChartOfAccounts""
                    (""AccountCode"", ""AccountName"", ""AccountNameEn"", ""AccountType"",
                     ""IsSystem"", ""AllowManualEntry"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ('5152', '進貨退出價差', 'Purchase return price variance', 5,
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
            // ⚠️ 有損：升級前就自建過 5152 的，ON CONFLICT 之後已無從分辨，會一起刪掉。
            migrationBuilder.Sql(@"DELETE FROM ""ChartOfAccounts"" WHERE ""AccountCode"" = '5152';");
        }
    }
}
