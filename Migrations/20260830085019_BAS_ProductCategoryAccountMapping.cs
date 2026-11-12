using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_ProductCategoryAccountMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpenseAccountCode",
                table: "ProductCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpenseAccountName",
                table: "ProductCategories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevenueAccountCode",
                table: "ProductCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevenueAccountName",
                table: "ProductCategories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpenseAccountCode",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "ExpenseAccountName",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "RevenueAccountCode",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "RevenueAccountName",
                table: "ProductCategories");
        }
    }
}
