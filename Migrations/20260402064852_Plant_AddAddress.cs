using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Plant_AddAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Plants"" ADD COLUMN IF NOT EXISTS ""AddressEn"" character varying(300)");
            migrationBuilder.Sql(@"ALTER TABLE ""Plants"" ADD COLUMN IF NOT EXISTS ""AddressZh"" character varying(300)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressEn",
                table: "Plants");

            migrationBuilder.DropColumn(
                name: "AddressZh",
                table: "Plants");
        }
    }
}
