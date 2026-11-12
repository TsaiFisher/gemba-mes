using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class InventoryCountAdjustmentDocId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdjustmentDocId",
                table: "InventoryCountSheets",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // 既有已結帳清冊回填：Close 建的盤盈虧調整單一律 TypeCode='IC' 且
            // Remarks = '盤點結帳 {SheetId}'（零差異也會建單，故每張已結帳清冊都對得到一張）。
            // 取 MIN 是為了「已經被重複結帳過」的清冊——本次要修的正是這個缺陷，
            // 舊資料若已有兩張調整單，回填只認最早那張，第二張留在庫裡供人工檢視，
            // 不在 migration 裡擅自刪除已過帳的單據。
            // 對不到的（Remarks 被改過）留 NULL：不參與唯一索引，仍由 SheetStatus 守門擋著。
            migrationBuilder.Sql("""
                UPDATE "InventoryCountSheets" s
                SET "AdjustmentDocId" = (
                    SELECT MIN(a."StockAdjustmentId")
                    FROM "StockAdjustments" a
                    WHERE a."TypeCode" = 'IC'
                      AND a."Remarks"  = '盤點結帳 ' || s."SheetId"
                      AND a."PlantId" IS NOT DISTINCT FROM s."PlantId"
                )
                WHERE s."SheetStatus" = 'Closed'
                  AND s."AdjustmentDocId" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountSheets_PlantId_AdjustmentDocId",
                table: "InventoryCountSheets",
                columns: new[] { "PlantId", "AdjustmentDocId" },
                unique: true,
                filter: "\"AdjustmentDocId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryCountSheets_PlantId_AdjustmentDocId",
                table: "InventoryCountSheets");

            migrationBuilder.DropColumn(
                name: "AdjustmentDocId",
                table: "InventoryCountSheets");
        }
    }
}
