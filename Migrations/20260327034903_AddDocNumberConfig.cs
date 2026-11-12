using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddDocNumberConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocNumberConfigs",
                columns: table => new
                {
                    DocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DocTypeName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DateFormat = table.Column<int>(type: "integer", nullable: false),
                    SeqLength = table.Column<int>(type: "integer", nullable: false),
                    Separator = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    ResetCycle = table.Column<int>(type: "integer", nullable: false),
                    IsSystemType = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocNumberConfigs", x => x.DocType);
                });

            migrationBuilder.CreateTable(
                name: "DocNumberSequences",
                columns: table => new
                {
                    DocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Period = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    LastSeq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocNumberSequences", x => new { x.DocType, x.PlantId, x.Period });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocNumberConfigs");

            migrationBuilder.DropTable(
                name: "DocNumberSequences");
        }
    }
}
