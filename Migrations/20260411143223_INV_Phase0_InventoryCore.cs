using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_Phase0_InventoryCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GoodsReceiptLines_InventoryMovements_InventoryMovementId",
                table: "GoodsReceiptLines");

            migrationBuilder.DropForeignKey(
                name: "FK_GoodsReturnLines_InventoryMovements_InventoryMovementId",
                table: "GoodsReturnLines");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReturnLines_InventoryMovementId",
                table: "GoodsReturnLines");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReceiptLines_InventoryMovementId",
                table: "GoodsReceiptLines");

            migrationBuilder.DropColumn(
                name: "InventoryMovementId",
                table: "GoodsReturnLines");

            migrationBuilder.DropColumn(
                name: "InventoryMovementId",
                table: "GoodsReceiptLines");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "GoodsReturns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TypeCategory",
                table: "GoodsReturns",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeCode",
                table: "GoodsReturns",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "QtyPutAway",
                table: "GoodsReturnLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "GoodsReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TypeCategory",
                table: "GoodsReceipts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeCode",
                table: "GoodsReceipts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "QtyPutAway",
                table: "GoodsReceiptLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "GoodsReceiptLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GoodsReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
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
                    table.PrimaryKey("PK_GoodsReceiptLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLocationDetails_GoodsReceiptLines_GoodsReceiptI~",
                        columns: x => new { x.GoodsReceiptId, x.LineNo },
                        principalTable: "GoodsReceiptLines",
                        principalColumns: new[] { "GoodsReceiptId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReturnLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GoodsReturnId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
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
                    table.PrimaryKey("PK_GoodsReturnLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoodsReturnLocationDetails_GoodsReturnLines_GoodsReturnId_L~",
                        columns: x => new { x.GoodsReturnId, x.LineNo },
                        principalTable: "GoodsReturnLines",
                        principalColumns: new[] { "GoodsReturnId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Inventories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    QtyOnHand = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    QtyReserved = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCostLayers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceDocNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    QtyRemaining = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCostLayers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    MovementId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DocNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DocLineNo = table.Column<int>(type: "integer", nullable: false),
                    SourceDocNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SourceDocLineNo = table.Column<int>(type: "integer", nullable: true),
                    MovementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    TotalCost = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Remark = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.MovementId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptLocationDetails_GoodsReceiptId_LineNo",
                table: "GoodsReceiptLocationDetails",
                columns: new[] { "GoodsReceiptId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReturnLocationDetails_GoodsReturnId_LineNo",
                table: "GoodsReturnLocationDetails",
                columns: new[] { "GoodsReturnId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_Inventories_PlantId_ProductCode_LocationCode_LotNo",
                table: "Inventories",
                columns: new[] { "PlantId", "ProductCode", "LocationCode", "LotNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostLayers_PlantId_ProductCode_LocationCode_LotNo_~",
                table: "InventoryCostLayers",
                columns: new[] { "PlantId", "ProductCode", "LocationCode", "LotNo", "ReceiptDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_DocNo_DocLineNo",
                table: "StockMovements",
                columns: new[] { "DocNo", "DocLineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_PlantId_MovementDate",
                table: "StockMovements",
                columns: new[] { "PlantId", "MovementDate" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_PlantId_ProductCode",
                table: "StockMovements",
                columns: new[] { "PlantId", "ProductCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoodsReceiptLocationDetails");

            migrationBuilder.DropTable(
                name: "GoodsReturnLocationDetails");

            migrationBuilder.DropTable(
                name: "Inventories");

            migrationBuilder.DropTable(
                name: "InventoryCostLayers");

            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "TypeCategory",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "TypeCode",
                table: "GoodsReturns");

            migrationBuilder.DropColumn(
                name: "QtyPutAway",
                table: "GoodsReturnLines");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "TypeCategory",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "TypeCode",
                table: "GoodsReceipts");

            migrationBuilder.DropColumn(
                name: "QtyPutAway",
                table: "GoodsReceiptLines");

            migrationBuilder.AddColumn<int>(
                name: "InventoryMovementId",
                table: "GoodsReturnLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InventoryMovementId",
                table: "GoodsReceiptLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MovementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SourceLineNo = table.Column<int>(type: "integer", nullable: true),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReturnLines_InventoryMovementId",
                table: "GoodsReturnLines",
                column: "InventoryMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptLines_InventoryMovementId",
                table: "GoodsReceiptLines",
                column: "InventoryMovementId");

            migrationBuilder.AddForeignKey(
                name: "FK_GoodsReceiptLines_InventoryMovements_InventoryMovementId",
                table: "GoodsReceiptLines",
                column: "InventoryMovementId",
                principalTable: "InventoryMovements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_GoodsReturnLines_InventoryMovements_InventoryMovementId",
                table: "GoodsReturnLines",
                column: "InventoryMovementId",
                principalTable: "InventoryMovements",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
