using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PUR_AddApprovalStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestId",
                table: "PurchaseRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "PurchaseRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestId",
                table: "PurchaseOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "PurchaseOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestId",
                table: "GoodsReturns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "GoodsReturns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestId",
                table: "GoodsReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "GoodsReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "Separator",
                table: "DocNumberConfigs",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(5)",
                oldMaxLength: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalRequestId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestId",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestId",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestId",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "GoodsReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "Separator",
                table: "DocNumberConfigs",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(5)",
                oldMaxLength: 5,
                oldNullable: true);
        }
    }
}
