using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_InventoryCount_Sheet_And_Items : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryCountSheets",
                columns: table => new
                {
                    SheetId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CountMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCodeFrom = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WhCodeTo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocCodeFrom = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocCodeTo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ProdCodeFrom = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ProdCodeTo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SnapshotAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SheetStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClosedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountSheets", x => x.SheetId);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCountItems",
                columns: table => new
                {
                    ItemId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SheetId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SeqNo = table.Column<int>(type: "integer", nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BookQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    FirstCountQty = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    FirstCountedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    FirstCountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecountQty = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    RecountedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    RecountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ItemStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AuditNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountItems", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_InventoryCountItems_InventoryCountSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "InventoryCountSheets",
                        principalColumn: "SheetId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[] { "InventoryCountSheet", 2, "盤點清冊", true, true, "IC", 1, "-", 5 });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountItems_SheetId_ProductCode_LocationCode_LotNo",
                table: "InventoryCountItems",
                columns: new[] { "SheetId", "ProductCode", "LocationCode", "LotNo" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountItems_SheetId_SeqNo",
                table: "InventoryCountItems",
                columns: new[] { "SheetId", "SeqNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountSheets_PlantId_CreatedAt",
                table: "InventoryCountSheets",
                columns: new[] { "PlantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountSheets_PlantId_SheetStatus",
                table: "InventoryCountSheets",
                columns: new[] { "PlantId", "SheetStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCountItems");

            migrationBuilder.DropTable(
                name: "InventoryCountSheets");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "InventoryCountSheet");
        }
    }
}
