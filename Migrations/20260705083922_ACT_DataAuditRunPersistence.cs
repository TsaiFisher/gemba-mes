using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class ACT_DataAuditRunPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataAuditRuns",
                columns: table => new
                {
                    RunId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TriggerType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PlantId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataAuditRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "DataAuditFindings",
                columns: table => new
                {
                    FindingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<int>(type: "integer", nullable: false),
                    AuditType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DocId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PlantId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    DifferenceAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    ErrorDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataAuditFindings", x => x.FindingId);
                    table.ForeignKey(
                        name: "FK_DataAuditFindings_DataAuditRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "DataAuditRuns",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataAuditFindings_RunId_AuditType",
                table: "DataAuditFindings",
                columns: new[] { "RunId", "AuditType" });

            migrationBuilder.CreateIndex(
                name: "IX_DataAuditRuns_RunAt",
                table: "DataAuditRuns",
                column: "RunAt");

            // 一次性重算歷史反正規化欄位（§3.9 稽核上線配套）：
            // 過去 GR/退貨單/銷退單作廢時未回沖累計欄位（本次已修，見 GoodsReceiptController.Void 等），
            // 歷史資料存在漂移；依單據明細重算為正確值，否則新稽核上線即擋關帳。
            // 語意：含草稿（回寫發生在建單時）、排除作廢（Status = 4 = DocStatus.Void）。
            migrationBuilder.Sql("""
                UPDATE "PurchaseOrderLines" pol
                SET "ReceivedQty" = COALESCE((
                    SELECT SUM(grl."ReceiptQty")
                    FROM "GoodsReceiptLines" grl
                    JOIN "GoodsReceipts" gr ON gr."GoodsReceiptId" = grl."GoodsReceiptId"
                    WHERE grl."PurchaseOrderId" = pol."PurchaseOrderId"
                      AND grl."PoLineNo" = pol."LineNo"
                      AND gr."Status" <> 4
                ), 0);

                UPDATE "GoodsReceiptLines" grl
                SET "ReturnedQty" = COALESCE((
                    SELECT SUM(grtl."ReturnQty")
                    FROM "GoodsReturnLines" grtl
                    JOIN "GoodsReturns" grt ON grt."GoodsReturnId" = grtl."GoodsReturnId"
                    WHERE grtl."GoodsReceiptId" = grl."GoodsReceiptId"
                      AND grtl."ReceiptLineNo" = grl."LineNo"
                      AND grt."Status" <> 4
                ), 0);

                UPDATE "SalesOrderLines" sol
                SET "ShippedQty" = COALESCE((
                    SELECT SUM(gsl."ShipQty")
                    FROM "GoodsShipmentLines" gsl
                    JOIN "GoodsShipments" gs ON gs."GoodsShipmentId" = gsl."GoodsShipmentId"
                    WHERE gsl."SalesOrderId" = sol."SalesOrderId"
                      AND gsl."SoLineNo" = sol."LineNo"
                      AND gs."Status" <> 4
                ), 0);

                UPDATE "GoodsShipmentLines" gsl
                SET "ReturnedQty" = COALESCE((
                    SELECT SUM(srl."ReturnQty")
                    FROM "SalesReturnLines" srl
                    JOIN "SalesReturns" sr ON sr."SalesReturnId" = srl."SalesReturnId"
                    WHERE srl."GoodsShipmentId" = gsl."GoodsShipmentId"
                      AND srl."ShipmentLineNo" = gsl."LineNo"
                      AND sr."Status" <> 4
                ), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataAuditFindings");

            migrationBuilder.DropTable(
                name: "DataAuditRuns");
        }
    }
}
