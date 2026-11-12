using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_DocNumberConfig_ScopeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "APInvoice", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "APPayment", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "ARInvoice", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "ARReceipt", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "CrossPlantTransfer", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "GoodsShipment", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "InternalOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "InventoryCountSheet", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "LandedCostAllocation", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "LoanDoc", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_MI", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_MR", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_MX", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_OI", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_OR", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_RI", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialSlip_RX", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MaterialStagingOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "MrpRun", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "NonConformanceReport", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "OutsourceDemand", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "OutsourceMaterial", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "OutsourcePurchaseOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "OutsourceQuotation", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "OutsourceReceipt", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "OutsourceRework", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "ProductionBatch", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "ReworkMaterial", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "SalesOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "SalesReturn", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "SampleDoc", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "ScrapDoc", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "StockAdjustment", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "PlantId" },
                keyValues: new object[] { "StockTransfer", "" });

            migrationBuilder.AddColumn<string>(
                name: "ScopeKey",
                table: "DocNumberConfigs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            // 既有專屬規則（PlantId 非空）回填 ScopeKey = 現有 PlantId（可為多廠 CSV），使其成為
            // 唯一的不變序號鍵；預設規則維持 ScopeKey=""。如此：
            //   (1) 新 PK (DocType, ScopeKey) 不會讓「預設列」與「專屬列」撞在 ("")；
            //   (2) 既有 DocNumberSequences（scope 原本就存 PlantId 值）無需搬移，序號完全延續。
            // 必須在 AddPrimaryKey 之前執行，否則專屬列的 ScopeKey 仍為預設 "" 會撞主鍵。
            migrationBuilder.Sql(
                @"UPDATE ""DocNumberConfigs"" SET ""ScopeKey"" = ""PlantId"" WHERE ""PlantId"" <> '';");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "ScopeKey" });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "ScopeKey", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "PlantId", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[,]
                {
                    { "APInvoice", "", 2, "應付發票", true, true, "", "API", 1, "-", 5 },
                    { "APPayment", "", 2, "付款單", true, true, "", "APP", 1, "-", 5 },
                    { "ARInvoice", "", 2, "應收發票", true, true, "", "ARI", 1, "-", 5 },
                    { "ARReceipt", "", 2, "收款單", true, true, "", "ARR", 1, "-", 5 },
                    { "CrossPlantTransfer", "", 2, "跨廠調撥單", true, true, "", "XP", 1, "-", 5 },
                    { "GoodsShipment", "", 2, "出貨單", true, true, "", "GS", 1, "-", 5 },
                    { "InternalOrder", "", 2, "內部訂單", true, true, "", "NO", 1, "-", 5 },
                    { "InventoryCountSheet", "", 2, "盤點清冊", true, true, "", "IC", 1, "-", 5 },
                    { "LandedCostAllocation", "", 2, "採購分攤費用單", true, true, "", "LCA", 1, "-", 5 },
                    { "LoanDoc", "", 2, "借入借出單", true, true, "", "LD", 1, "-", 5 },
                    { "MaterialSlip_MI", "", 2, "廠內發料單", true, true, "", "MI", 1, "-", 5 },
                    { "MaterialSlip_MR", "", 2, "廠內領料單", true, true, "", "MR", 1, "-", 5 },
                    { "MaterialSlip_MX", "", 2, "廠內退料單", true, true, "", "MX", 1, "-", 5 },
                    { "MaterialSlip_OI", "", 2, "委外發料單", true, true, "", "OI", 1, "-", 5 },
                    { "MaterialSlip_OR", "", 2, "委外退料單", true, true, "", "OR", 1, "-", 5 },
                    { "MaterialSlip_RI", "", 2, "重工發料單", true, true, "", "RI", 1, "-", 5 },
                    { "MaterialSlip_RX", "", 2, "重工退料單", true, true, "", "RX", 1, "-", 5 },
                    { "MaterialStagingOrder", "", 2, "備料單", true, true, "", "IM", 1, "-", 5 },
                    { "MrpRun", "", 2, "MRP執行單", true, true, "", "MRP", 1, "-", 5 },
                    { "NonConformanceReport", "", 2, "不合格品單", true, true, "", "NCR", 1, "-", 5 },
                    { "OutsourceDemand", "", 2, "委外建議單", true, true, "", "OD", 1, "-", 5 },
                    { "OutsourceMaterial", "", 2, "委外發退料單", true, true, "", "OM", 1, "-", 5 },
                    { "OutsourcePurchaseOrder", "", 2, "委外採購單", true, true, "", "OP", 1, "-", 5 },
                    { "OutsourceQuotation", "", 2, "委外報價主檔", true, true, "", "OQ", 1, "-", 5 },
                    { "OutsourceReceipt", "", 2, "委外收貨單", true, true, "", "OR", 1, "-", 5 },
                    { "OutsourceRework", "", 2, "委外重工單", true, true, "", "RW", 1, "-", 5 },
                    { "ProductionBatch", "", 2, "生產批", true, true, "", "PB", 1, "-", 5 },
                    { "ReworkMaterial", "", 2, "重工發退料單", true, true, "", "RM", 1, "-", 5 },
                    { "SalesOrder", "", 2, "銷售訂單", true, true, "", "SO", 1, "-", 5 },
                    { "SalesReturn", "", 2, "銷退單", true, true, "", "GR", 1, "-", 5 },
                    { "SampleDoc", "", 2, "樣品客供單", true, true, "", "SM", 1, "-", 5 },
                    { "ScrapDoc", "", 2, "報廢單", true, true, "", "IJ", 1, "-", 5 },
                    { "StockAdjustment", "", 2, "調整單", true, true, "", "IA", 1, "-", 5 },
                    { "StockTransfer", "", 2, "同廠調撥單", true, true, "", "WT", 1, "-", 5 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "APInvoice", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "APPayment", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "ARInvoice", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "ARReceipt", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "CrossPlantTransfer", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "GoodsShipment", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "InternalOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "InventoryCountSheet", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "LandedCostAllocation", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "LoanDoc", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_MI", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_MR", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_MX", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_OI", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_OR", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_RI", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialSlip_RX", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MaterialStagingOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "MrpRun", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "NonConformanceReport", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "OutsourceDemand", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "OutsourceMaterial", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "OutsourcePurchaseOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "OutsourceQuotation", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "OutsourceReceipt", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "OutsourceRework", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "ProductionBatch", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "ReworkMaterial", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "SalesOrder", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "SalesReturn", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "SampleDoc", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "ScrapDoc", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "StockAdjustment", "" });

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumns: new[] { "DocType", "ScopeKey" },
                keyColumnTypes: new[] { "character varying(50)", "character varying(500)" },
                keyValues: new object[] { "StockTransfer", "" });

            migrationBuilder.DropColumn(
                name: "ScopeKey",
                table: "DocNumberConfigs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "PlantId" });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "PlantId", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[,]
                {
                    { "APInvoice", "", 2, "應付發票", true, true, "API", 1, "-", 5 },
                    { "APPayment", "", 2, "付款單", true, true, "APP", 1, "-", 5 },
                    { "ARInvoice", "", 2, "應收發票", true, true, "ARI", 1, "-", 5 },
                    { "ARReceipt", "", 2, "收款單", true, true, "ARR", 1, "-", 5 },
                    { "CrossPlantTransfer", "", 2, "跨廠調撥單", true, true, "XP", 1, "-", 5 },
                    { "GoodsShipment", "", 2, "出貨單", true, true, "GS", 1, "-", 5 },
                    { "InternalOrder", "", 2, "內部訂單", true, true, "NO", 1, "-", 5 },
                    { "InventoryCountSheet", "", 2, "盤點清冊", true, true, "IC", 1, "-", 5 },
                    { "LandedCostAllocation", "", 2, "採購分攤費用單", true, true, "LCA", 1, "-", 5 },
                    { "LoanDoc", "", 2, "借入借出單", true, true, "LD", 1, "-", 5 },
                    { "MaterialSlip_MI", "", 2, "廠內發料單", true, true, "MI", 1, "-", 5 },
                    { "MaterialSlip_MR", "", 2, "廠內領料單", true, true, "MR", 1, "-", 5 },
                    { "MaterialSlip_MX", "", 2, "廠內退料單", true, true, "MX", 1, "-", 5 },
                    { "MaterialSlip_OI", "", 2, "委外發料單", true, true, "OI", 1, "-", 5 },
                    { "MaterialSlip_OR", "", 2, "委外退料單", true, true, "OR", 1, "-", 5 },
                    { "MaterialSlip_RI", "", 2, "重工發料單", true, true, "RI", 1, "-", 5 },
                    { "MaterialSlip_RX", "", 2, "重工退料單", true, true, "RX", 1, "-", 5 },
                    { "MaterialStagingOrder", "", 2, "備料單", true, true, "IM", 1, "-", 5 },
                    { "MrpRun", "", 2, "MRP執行單", true, true, "MRP", 1, "-", 5 },
                    { "NonConformanceReport", "", 2, "不合格品單", true, true, "NCR", 1, "-", 5 },
                    { "OutsourceDemand", "", 2, "委外建議單", true, true, "OD", 1, "-", 5 },
                    { "OutsourceMaterial", "", 2, "委外發退料單", true, true, "OM", 1, "-", 5 },
                    { "OutsourcePurchaseOrder", "", 2, "委外採購單", true, true, "OP", 1, "-", 5 },
                    { "OutsourceQuotation", "", 2, "委外報價主檔", true, true, "OQ", 1, "-", 5 },
                    { "OutsourceReceipt", "", 2, "委外收貨單", true, true, "OR", 1, "-", 5 },
                    { "OutsourceRework", "", 2, "委外重工單", true, true, "RW", 1, "-", 5 },
                    { "ProductionBatch", "", 2, "生產批", true, true, "PB", 1, "-", 5 },
                    { "ReworkMaterial", "", 2, "重工發退料單", true, true, "RM", 1, "-", 5 },
                    { "SalesOrder", "", 2, "銷售訂單", true, true, "SO", 1, "-", 5 },
                    { "SalesReturn", "", 2, "銷退單", true, true, "GR", 1, "-", 5 },
                    { "SampleDoc", "", 2, "樣品客供單", true, true, "SM", 1, "-", 5 },
                    { "ScrapDoc", "", 2, "報廢單", true, true, "IJ", 1, "-", 5 },
                    { "StockAdjustment", "", 2, "調整單", true, true, "IA", 1, "-", 5 },
                    { "StockTransfer", "", 2, "同廠調撥單", true, true, "WT", 1, "-", 5 }
                });
        }
    }
}
