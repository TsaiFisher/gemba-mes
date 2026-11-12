using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class ORD_SalesOrderLineCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "SalesOrders");

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "SalesOrderLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "SalesOrderLines",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 1m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "SalesOrderLines");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "SalesOrderLines");

            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "SalesOrders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }
    }
}
