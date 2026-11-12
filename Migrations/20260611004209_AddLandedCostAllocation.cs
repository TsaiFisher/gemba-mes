using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddLandedCostAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LandedCostAllocations",
                columns: table => new
                {
                    LandedCostAllocationId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DocDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Basis = table.Column<int>(type: "integer", nullable: false),
                    CostType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TotalLandedCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LandedCostAllocations", x => x.LandedCostAllocationId);
                });

            migrationBuilder.CreateTable(
                name: "LandedCostAllocationLines",
                columns: table => new
                {
                    LandedCostAllocationId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    GoodsReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    GoodsReceiptLineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReceiptQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PurchaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    OriginalUnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AllocatedCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CapitalizedCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExpensedCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    NewUnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LandedCostAllocationLines", x => new { x.LandedCostAllocationId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_LandedCostAllocationLines_LandedCostAllocations_LandedCostA~",
                        column: x => x.LandedCostAllocationId,
                        principalTable: "LandedCostAllocations",
                        principalColumn: "LandedCostAllocationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[] { "LandedCostAllocation", 2, "採購分攤費用單", true, true, "LCA", 1, "-", 5 });

            migrationBuilder.CreateIndex(
                name: "IX_LandedCostAllocations_PlantId_LandedCostAllocationId",
                table: "LandedCostAllocations",
                columns: new[] { "PlantId", "LandedCostAllocationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LandedCostAllocationLines");

            migrationBuilder.DropTable(
                name: "LandedCostAllocations");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "LandedCostAllocation");
        }
    }
}
