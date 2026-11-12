using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_RoutingOperation_ContainerCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseContainerIn",
                table: "RoutingOperations");

            migrationBuilder.DropColumn(
                name: "UseContainerOut",
                table: "RoutingOperations");

            migrationBuilder.AddColumn<string>(
                name: "ContainerInCode",
                table: "RoutingOperations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContainerOutCode",
                table: "RoutingOperations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContainerInCode",
                table: "RoutingOperations");

            migrationBuilder.DropColumn(
                name: "ContainerOutCode",
                table: "RoutingOperations");

            migrationBuilder.AddColumn<bool>(
                name: "UseContainerIn",
                table: "RoutingOperations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UseContainerOut",
                table: "RoutingOperations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
