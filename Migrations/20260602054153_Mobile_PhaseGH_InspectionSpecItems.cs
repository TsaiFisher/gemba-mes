using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Mobile_PhaseGH_InspectionSpecItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InspectionOrders",
                columns: table => new
                {
                    InspectionOrderId = table.Column<string>(type: "text", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    InspectionType = table.Column<string>(type: "text", nullable: false),
                    SourceDocId = table.Column<string>(type: "text", nullable: false),
                    SourceLineNo = table.Column<int>(type: "integer", nullable: true),
                    ProductCode = table.Column<string>(type: "text", nullable: false),
                    LotSize = table.Column<decimal>(type: "numeric", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    VerificationLevel = table.Column<int>(type: "integer", nullable: false),
                    ActualSampleQty = table.Column<int>(type: "integer", nullable: false),
                    FailQty = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Remarks = table.Column<string>(type: "text", nullable: true),
                    InspectorId = table.Column<string>(type: "text", nullable: true),
                    InspectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionOrders", x => x.InspectionOrderId);
                });

            migrationBuilder.CreateTable(
                name: "InspectionSpecs",
                columns: table => new
                {
                    SpecId = table.Column<string>(type: "text", nullable: false),
                    SpecCode = table.Column<string>(type: "text", nullable: false),
                    SpecName = table.Column<string>(type: "text", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    ProductCode = table.Column<string>(type: "text", nullable: true),
                    SupplierCode = table.Column<string>(type: "text", nullable: true),
                    VerificationLevel = table.Column<int>(type: "integer", nullable: false),
                    SamplingType = table.Column<string>(type: "text", nullable: false),
                    TriggerMode = table.Column<string>(type: "text", nullable: false),
                    PeriodHours = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSpecs", x => x.SpecId);
                });

            migrationBuilder.CreateTable(
                name: "InspectionItems",
                columns: table => new
                {
                    ItemId = table.Column<string>(type: "text", nullable: false),
                    InspectionOrderId = table.Column<string>(type: "text", nullable: false),
                    SeqNo = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false),
                    UomCode = table.Column<string>(type: "text", nullable: true),
                    LowerSpec = table.Column<decimal>(type: "numeric", nullable: true),
                    TargetValue = table.Column<decimal>(type: "numeric", nullable: true),
                    UpperSpec = table.Column<decimal>(type: "numeric", nullable: true),
                    MeasuredValue = table.Column<decimal>(type: "numeric", nullable: true),
                    IsConforming = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionItems", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_InspectionItems_InspectionOrders_InspectionOrderId",
                        column: x => x.InspectionOrderId,
                        principalTable: "InspectionOrders",
                        principalColumn: "InspectionOrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionSpecItems",
                columns: table => new
                {
                    SpecItemId = table.Column<string>(type: "text", nullable: false),
                    SpecId = table.Column<string>(type: "text", nullable: false),
                    SeqNo = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false),
                    UomCode = table.Column<string>(type: "text", nullable: true),
                    LowerSpec = table.Column<decimal>(type: "numeric", nullable: true),
                    TargetValue = table.Column<decimal>(type: "numeric", nullable: true),
                    UpperSpec = table.Column<decimal>(type: "numeric", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSpecItems", x => x.SpecItemId);
                    table.ForeignKey(
                        name: "FK_InspectionSpecItems_InspectionSpecs_SpecId",
                        column: x => x.SpecId,
                        principalTable: "InspectionSpecs",
                        principalColumn: "SpecId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionItems_InspectionOrderId",
                table: "InspectionItems",
                column: "InspectionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionOrders_PlantId_InspectionType_SourceDocId_SourceL~",
                table: "InspectionOrders",
                columns: new[] { "PlantId", "InspectionType", "SourceDocId", "SourceLineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSpecItems_SpecId_SeqNo",
                table: "InspectionSpecItems",
                columns: new[] { "SpecId", "SeqNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSpecs_PlantId_ProductCode_SupplierCode",
                table: "InspectionSpecs",
                columns: new[] { "PlantId", "ProductCode", "SupplierCode" });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSpecs_PlantId_SpecCode",
                table: "InspectionSpecs",
                columns: new[] { "PlantId", "SpecCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InspectionItems");

            migrationBuilder.DropTable(
                name: "InspectionSpecItems");

            migrationBuilder.DropTable(
                name: "InspectionOrders");

            migrationBuilder.DropTable(
                name: "InspectionSpecs");
        }
    }
}
