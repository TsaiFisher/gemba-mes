using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_ContainerUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContainerUnitHistories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContainerUnitId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    DocNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerUnitHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContainerUnits",
                columns: table => new
                {
                    ContainerUnitId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ContainerCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    BoundAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BoundDocNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerUnits", x => x.ContainerUnitId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerUnitHistories_ContainerUnitId",
                table: "ContainerUnitHistories",
                column: "ContainerUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerUnits_PlantId_ContainerCode",
                table: "ContainerUnits",
                columns: new[] { "PlantId", "ContainerCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerUnits_PlantId_ProductCode_LotNo",
                table: "ContainerUnits",
                columns: new[] { "PlantId", "ProductCode", "LotNo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerUnitHistories");

            migrationBuilder.DropTable(
                name: "ContainerUnits");
        }
    }
}
