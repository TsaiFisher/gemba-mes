using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class InvMaterialStagingOrder_MrpFstockFullMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaterialStagingOrders",
                columns: table => new
                {
                    StagingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    StagingWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StagingLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConfirmedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialStagingOrders", x => x.StagingId);
                });

            migrationBuilder.CreateTable(
                name: "MaterialStagingLines",
                columns: table => new
                {
                    StagingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RequiredQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PickedQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReservationId = table.Column<int>(type: "integer", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialStagingLines", x => new { x.StagingId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_MaterialStagingLines_InventoryReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "InventoryReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MaterialStagingLines_MaterialStagingOrders_StagingId",
                        column: x => x.StagingId,
                        principalTable: "MaterialStagingOrders",
                        principalColumn: "StagingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[] { "MaterialStagingOrder", 2, "備料單", true, true, "IM", 1, "-", 5 });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialStagingLines_ReservationId",
                table: "MaterialStagingLines",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialStagingOrders_PlantId_StagingId",
                table: "MaterialStagingOrders",
                columns: new[] { "PlantId", "StagingId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaterialStagingLines");

            migrationBuilder.DropTable(
                name: "MaterialStagingOrders");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialStagingOrder");
        }
    }
}
