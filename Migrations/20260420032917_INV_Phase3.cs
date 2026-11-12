using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_Phase3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutsourceMaterialDocs",
                columns: table => new
                {
                    OutsourceMaterialDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SupplierCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WorkOrderCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceMaterialDocs", x => x.OutsourceMaterialDocId);
                });

            migrationBuilder.CreateTable(
                name: "ReworkMaterialDocs",
                columns: table => new
                {
                    ReworkMaterialDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WorkCenterCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WorkOrderCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReworkMaterialDocs", x => x.ReworkMaterialDocId);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustments",
                columns: table => new
                {
                    StockAdjustmentId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AdjustDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DepartmentCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    AdjustReasonCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustments", x => x.StockAdjustmentId);
                });

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                columns: table => new
                {
                    StockTransferId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TransferDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FromWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.StockTransferId);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceMaterialDocLines",
                columns: table => new
                {
                    OutsourceMaterialDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceMaterialDocLines", x => new { x.OutsourceMaterialDocId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_OutsourceMaterialDocLines_OutsourceMaterialDocs_OutsourceMa~",
                        column: x => x.OutsourceMaterialDocId,
                        principalTable: "OutsourceMaterialDocs",
                        principalColumn: "OutsourceMaterialDocId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReworkMaterialDocLines",
                columns: table => new
                {
                    ReworkMaterialDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReworkMaterialDocLines", x => new { x.ReworkMaterialDocId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_ReworkMaterialDocLines_ReworkMaterialDocs_ReworkMaterialDoc~",
                        column: x => x.ReworkMaterialDocId,
                        principalTable: "ReworkMaterialDocs",
                        principalColumn: "ReworkMaterialDocId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustmentLines",
                columns: table => new
                {
                    StockAdjustmentId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustmentLines", x => new { x.StockAdjustmentId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLines_StockAdjustments_StockAdjustmentId",
                        column: x => x.StockAdjustmentId,
                        principalTable: "StockAdjustments",
                        principalColumn: "StockAdjustmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferLines",
                columns: table => new
                {
                    StockTransferId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    FromWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FromLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferLines", x => new { x.StockTransferId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_StockTransferLines_StockTransfers_StockTransferId",
                        column: x => x.StockTransferId,
                        principalTable: "StockTransfers",
                        principalColumn: "StockTransferId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceMaterialDocLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OutsourceMaterialDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceMaterialDocLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutsourceMaterialDocLocationDetails_OutsourceMaterialDocLin~",
                        columns: x => new { x.OutsourceMaterialDocId, x.LineNo },
                        principalTable: "OutsourceMaterialDocLines",
                        principalColumns: new[] { "OutsourceMaterialDocId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReworkMaterialDocLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReworkMaterialDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReworkMaterialDocLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReworkMaterialDocLocationDetails_ReworkMaterialDocLines_Rew~",
                        columns: x => new { x.ReworkMaterialDocId, x.LineNo },
                        principalTable: "ReworkMaterialDocLines",
                        principalColumns: new[] { "ReworkMaterialDocId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustmentLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockAdjustmentId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustmentLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLocationDetails_StockAdjustmentLines_StockAd~",
                        columns: x => new { x.StockAdjustmentId, x.LineNo },
                        principalTable: "StockAdjustmentLines",
                        principalColumns: new[] { "StockAdjustmentId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceMaterialDocLocationDetails_OutsourceMaterialDocId_~",
                table: "OutsourceMaterialDocLocationDetails",
                columns: new[] { "OutsourceMaterialDocId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceMaterialDocs_PlantId_TypeCode_IssueDate",
                table: "OutsourceMaterialDocs",
                columns: new[] { "PlantId", "TypeCode", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ReworkMaterialDocLocationDetails_ReworkMaterialDocId_LineNo",
                table: "ReworkMaterialDocLocationDetails",
                columns: new[] { "ReworkMaterialDocId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ReworkMaterialDocs_PlantId_TypeCode_IssueDate",
                table: "ReworkMaterialDocs",
                columns: new[] { "PlantId", "TypeCode", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustmentLocationDetails_StockAdjustmentId_LineNo",
                table: "StockAdjustmentLocationDetails",
                columns: new[] { "StockAdjustmentId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_PlantId_TypeCode_AdjustDate",
                table: "StockAdjustments",
                columns: new[] { "PlantId", "TypeCode", "AdjustDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_PlantId_TransferDate",
                table: "StockTransfers",
                columns: new[] { "PlantId", "TransferDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutsourceMaterialDocLocationDetails");

            migrationBuilder.DropTable(
                name: "ReworkMaterialDocLocationDetails");

            migrationBuilder.DropTable(
                name: "StockAdjustmentLocationDetails");

            migrationBuilder.DropTable(
                name: "StockTransferLines");

            migrationBuilder.DropTable(
                name: "OutsourceMaterialDocLines");

            migrationBuilder.DropTable(
                name: "ReworkMaterialDocLines");

            migrationBuilder.DropTable(
                name: "StockAdjustmentLines");

            migrationBuilder.DropTable(
                name: "StockTransfers");

            migrationBuilder.DropTable(
                name: "OutsourceMaterialDocs");

            migrationBuilder.DropTable(
                name: "ReworkMaterialDocs");

            migrationBuilder.DropTable(
                name: "StockAdjustments");
        }
    }
}
