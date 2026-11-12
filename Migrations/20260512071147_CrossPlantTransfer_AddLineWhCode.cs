using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class CrossPlantTransfer_AddLineWhCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromWhCode",
                table: "CrossPlantTransferLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToWhCode",
                table: "CrossPlantTransferLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromWhCode",
                table: "CrossPlantTransferLines");

            migrationBuilder.DropColumn(
                name: "ToWhCode",
                table: "CrossPlantTransferLines");
        }
    }
}
