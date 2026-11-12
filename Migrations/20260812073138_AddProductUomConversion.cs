using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddProductUomConversion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductUomConversions",
                columns: table => new
                {
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FromUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ToUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Numerator = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Denominator = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUomConversions", x => new { x.ProductCode, x.FromUomCode, x.ToUomCode });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductUomConversions");
        }
    }
}
