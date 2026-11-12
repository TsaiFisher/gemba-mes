using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MfgPhaseG_OutsourceDemand_MaterialSlip_OutsourceQuotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "OutsourcePurchaseOrderLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DemandId",
                table: "OutsourcePurchaseOrderLines",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SplitQty",
                table: "OutsourcePurchaseOrderLines",
                type: "numeric(18,6)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "OutsourcePurchaseOrderLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "GoodsReceipts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocType",
                table: "GoodsReceipts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QtyGood",
                table: "GoodsReceiptLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "QtyRejected",
                table: "GoodsReceiptLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "MaterialSlips",
                columns: table => new
                {
                    SlipId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SlipNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SlipDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceDocType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceDocId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetSupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IssuedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialSlips", x => x.SlipId);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceDemands",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MrpRunId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DemandType = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DemandQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RequiredDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RoutingOperationCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    TransferredQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceDemands", x => x.DemandId);
                    table.ForeignKey(
                        name: "FK_OutsourceDemands_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "WorkOrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceQuotations",
                columns: table => new
                {
                    QuotationId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RoutingId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    OperationSeq = table.Column<int>(type: "integer", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MinQty = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    MaxQty = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    LeadDays = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceQuotations", x => x.QuotationId);
                });

            migrationBuilder.CreateTable(
                name: "MaterialSlipLines",
                columns: table => new
                {
                    SlipId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    SourceLineId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ApprovedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    ConfirmedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FromWarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FromLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToWarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SerialNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReturnedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialSlipLines", x => new { x.SlipId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_MaterialSlipLines_MaterialSlips_SlipId",
                        column: x => x.SlipId,
                        principalTable: "MaterialSlips",
                        principalColumn: "SlipId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[,]
                {
                    { "MaterialSlip_MI", 2, "廠內發料單", true, true, "MI", 1, "-", 5 },
                    { "MaterialSlip_MR", 2, "廠內領料單", true, true, "MR", 1, "-", 5 },
                    { "MaterialSlip_MX", 2, "廠內退料單", true, true, "MX", 1, "-", 5 },
                    { "MaterialSlip_OI", 2, "委外發料單", true, true, "OI", 1, "-", 5 },
                    { "MaterialSlip_OR", 2, "委外退料單", true, true, "OR", 1, "-", 5 },
                    { "MaterialSlip_RI", 2, "重工發料單", true, true, "RI", 1, "-", 5 },
                    { "MaterialSlip_RX", 2, "重工退料單", true, true, "RX", 1, "-", 5 },
                    { "OutsourceDemand", 2, "委外建議單", true, true, "OD", 1, "-", 5 },
                    { "OutsourceQuotation", 2, "委外報價主檔", true, true, "OQ", 1, "-", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourcePurchaseOrderLines_DemandId",
                table: "OutsourcePurchaseOrderLines",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSlips_PlantId_SlipId",
                table: "MaterialSlips",
                columns: new[] { "PlantId", "SlipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSlips_PlantId_SourceDocType_SourceDocId",
                table: "MaterialSlips",
                columns: new[] { "PlantId", "SourceDocType", "SourceDocId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialSlips_SlipNo",
                table: "MaterialSlips",
                column: "SlipNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceDemands_PlantId_DemandId",
                table: "OutsourceDemands",
                columns: new[] { "PlantId", "DemandId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceDemands_PlantId_WorkOrderId",
                table: "OutsourceDemands",
                columns: new[] { "PlantId", "WorkOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceDemands_WorkOrderId",
                table: "OutsourceDemands",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceQuotations_IsActive_EffectiveFrom_EffectiveTo",
                table: "OutsourceQuotations",
                columns: new[] { "IsActive", "EffectiveFrom", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceQuotations_SupplierCode_ProductCode_RoutingId_Oper~",
                table: "OutsourceQuotations",
                columns: new[] { "SupplierCode", "ProductCode", "RoutingId", "OperationSeq", "EffectiveFrom" });

            migrationBuilder.AddForeignKey(
                name: "FK_OutsourcePurchaseOrderLines_OutsourceDemands_DemandId",
                table: "OutsourcePurchaseOrderLines",
                column: "DemandId",
                principalTable: "OutsourceDemands",
                principalColumn: "DemandId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OutsourcePurchaseOrderLines_OutsourceDemands_DemandId",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "MaterialSlipLines");

            migrationBuilder.DropTable(
                name: "OutsourceDemands");

            migrationBuilder.DropTable(
                name: "OutsourceQuotations");

            migrationBuilder.DropTable(
                name: "MaterialSlips");

            migrationBuilder.DropIndex(
                name: "IX_OutsourcePurchaseOrderLines_DemandId",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_MI");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_MR");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_MX");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_OI");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_OR");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_RI");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "MaterialSlip_RX");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "OutsourceDemand");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "OutsourceQuotation");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "DemandId",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "SplitQty",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "OutsourcePurchaseOrderLines");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "SourceDocType",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "QtyGood",
                table: "GoodsReceiptLines");

            migrationBuilder.DropColumn(
                name: "QtyRejected",
                table: "GoodsReceiptLines");
        }
    }
}
