using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_PhaseE_MobileAPI_MachineStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineStatusOrders",
                columns: table => new
                {
                    OrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    EventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineStatusOrders", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "SyncLogs",
                columns: table => new
                {
                    ClientId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResponseSnapshot = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLogs", x => x.ClientId);
                });

            migrationBuilder.CreateTable(
                name: "MachineStatusOrderLines",
                columns: table => new
                {
                    OrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LineSeq = table.Column<int>(type: "integer", nullable: false),
                    MachineCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WorkCenterCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetStatus = table.Column<int>(type: "integer", nullable: false),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineStatusOrderLines", x => new { x.OrderId, x.LineSeq });
                    table.ForeignKey(
                        name: "FK_MachineStatusOrderLines_MachineStatusOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "MachineStatusOrders",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineStatusOrders_PlantId_OrderId",
                table: "MachineStatusOrders",
                columns: new[] { "PlantId", "OrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncLogs_Endpoint_CreatedAt",
                table: "SyncLogs",
                columns: new[] { "Endpoint", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineStatusOrderLines");

            migrationBuilder.DropTable(
                name: "SyncLogs");

            migrationBuilder.DropTable(
                name: "MachineStatusOrders");
        }
    }
}
