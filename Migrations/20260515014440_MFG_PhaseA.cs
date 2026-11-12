using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_PhaseA : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Routings",
                columns: table => new
                {
                    RoutingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RoutingName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BatchSize = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    BufferDays = table.Column<int>(type: "integer", nullable: false),
                    AvgLeadTime = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routings", x => x.RoutingId);
                });

            migrationBuilder.CreateTable(
                name: "WorkCalendars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    WorkCenterId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    MachineCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TM_WKS = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    TM_WKO = table.Column<decimal>(type: "numeric(6,2)", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCalendars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoutingOperations",
                columns: table => new
                {
                    RoutingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    OperationName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WorkCenterId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TM_SETUP = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TM_UNIT = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    CD_BOR = table.Column<int>(type: "integer", nullable: false),
                    QT_BOR = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsOutsource = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresInspection = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingOperations", x => new { x.RoutingId, x.OperationSeq });
                    table.ForeignKey(
                        name: "FK_RoutingOperations_Routings_RoutingId",
                        column: x => x.RoutingId,
                        principalTable: "Routings",
                        principalColumn: "RoutingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoutingBoms",
                columns: table => new
                {
                    RoutingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    BomSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Yield = table.Column<decimal>(type: "numeric(10,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingBoms", x => new { x.RoutingId, x.OperationSeq, x.BomSeq });
                    table.ForeignKey(
                        name: "FK_RoutingBoms_RoutingOperations_RoutingId_OperationSeq",
                        columns: x => new { x.RoutingId, x.OperationSeq },
                        principalTable: "RoutingOperations",
                        principalColumns: new[] { "RoutingId", "OperationSeq" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Routings_PlantId_RoutingId",
                table: "Routings",
                columns: new[] { "PlantId", "RoutingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCalendars_PlantId_WorkCenterId_MachineCode_Date",
                table: "WorkCalendars",
                columns: new[] { "PlantId", "WorkCenterId", "MachineCode", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoutingBoms");

            migrationBuilder.DropTable(
                name: "WorkCalendars");

            migrationBuilder.DropTable(
                name: "RoutingOperations");

            migrationBuilder.DropTable(
                name: "Routings");
        }
    }
}
