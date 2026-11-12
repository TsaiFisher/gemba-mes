using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseRequestLineSourceMrpRunId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceMrpRunId",
                table: "PurchaseRequestLines",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // 既有資料回填：表頭已標來源 MRP 執行單的請購單，其明細一律視為該次 MRP 產生。
            // 不回填的話，本欄位上線前既有的請購單明細全為 null，重跑對帳會判定「沒有任何
            // MRP 來源明細」而完全不收斂——PR260700005 這類既存單據將永遠不被納入對帳。
            // （這些單據表頭本就宣告來自 MRP，明細層無更精確的來源可推斷。）
            migrationBuilder.Sql(@"
                UPDATE ""PurchaseRequestLines"" l
                SET ""SourceMrpRunId"" = p.""SourceMrpRunId""
                FROM ""PurchaseRequests"" p
                WHERE l.""PurchaseRequestId"" = p.""PurchaseRequestId""
                  AND p.""SourceMrpRunId"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceMrpRunId",
                table: "PurchaseRequestLines");
        }
    }
}
