using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_InventoryReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReservedQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceDocId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceLineSeq = table.Column<int>(type: "integer", nullable: true),
                    RequiredDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryReservations", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[,]
                {
                    { "MrpRun", 2, "MRP執行單", true, true, "MRP", 1, "-", 5 },
                    { "ProductionBatch", 2, "生產批", true, true, "PB", 1, "-", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_PlantId_ProductCode_Status",
                table: "InventoryReservations",
                columns: new[] { "PlantId", "ProductCode", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_SourceType_SourceDocId",
                table: "InventoryReservations",
                columns: new[] { "SourceType", "SourceDocId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryReservations");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MrpRun");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "ProductionBatch");
        }
    }
}
