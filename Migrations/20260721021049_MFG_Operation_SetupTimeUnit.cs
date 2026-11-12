using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_Operation_SetupTimeUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SetupTimeUnit",
                table: "WorkOrderOperations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SetupTimeUnit",
                table: "RoutingOperations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 既有工序的準備工時原本共用 TimeUnit（單一時間單位）。拆出 SetupTimeUnit 後，
            // 既有列必須回填為原 TimeUnit 以保留原本的換算行為，否則原設「時/秒」的準備工時
            // 會被預設值 0 靜默改變計算結果。新列由應用層寫入（預設分），不受此影響。
            migrationBuilder.Sql(
                "UPDATE \"RoutingOperations\" SET \"SetupTimeUnit\" = \"TimeUnit\";");
            migrationBuilder.Sql(
                "UPDATE \"WorkOrderOperations\" SET \"SetupTimeUnit\" = \"TimeUnit\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SetupTimeUnit",
                table: "WorkOrderOperations");

            migrationBuilder.DropColumn(
                name: "SetupTimeUnit",
                table: "RoutingOperations");
        }
    }
}
