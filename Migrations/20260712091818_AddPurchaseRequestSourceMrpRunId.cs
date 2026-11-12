using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseRequestSourceMrpRunId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceMrpRunId",
                table: "PurchaseRequests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // 註：MrpRun.xmin 對應 PostgreSQL 系統既有欄位（非新增實體欄位），
            // 不可下 AddColumn/DropColumn DDL（xmin 為保留系統欄位名，實際下會衝突）；
            // UseXminAsConcurrencyToken() 僅是讓 EF model 認知既有系統欄位，本次遷移對它零 DDL。

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_SourceMrpRunId",
                table: "PurchaseRequests",
                column: "SourceMrpRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_SourceMrpRunId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "SourceMrpRunId",
                table: "PurchaseRequests");
        }
    }
}
