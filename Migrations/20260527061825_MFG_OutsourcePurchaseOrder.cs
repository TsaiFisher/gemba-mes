using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_OutsourcePurchaseOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutsourcePurchaseOrders",
                columns: table => new
                {
                    OutsourcePurchaseOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpectedDeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourcePurchaseOrders", x => x.OutsourcePurchaseOrderId);
                });

            migrationBuilder.CreateTable(
                name: "OutsourcePurchaseOrderLines",
                columns: table => new
                {
                    OutsourcePurchaseOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    SourceWorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourcePurchaseOrderLines", x => new { x.OutsourcePurchaseOrderId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_OutsourcePurchaseOrderLines_OutsourcePurchaseOrders_Outsour~",
                        column: x => x.OutsourcePurchaseOrderId,
                        principalTable: "OutsourcePurchaseOrders",
                        principalColumn: "OutsourcePurchaseOrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[] { "OutsourcePurchaseOrder", 2, "委外採購單", true, true, "OP", 1, "-", 5 });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourcePurchaseOrders_PlantId_OutsourcePurchaseOrderId",
                table: "OutsourcePurchaseOrders",
                columns: new[] { "PlantId", "OutsourcePurchaseOrderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutsourcePurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "OutsourcePurchaseOrders");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "OutsourcePurchaseOrder");
        }
    }
}
