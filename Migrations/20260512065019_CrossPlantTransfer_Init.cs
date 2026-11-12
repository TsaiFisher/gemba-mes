using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class CrossPlantTransfer_Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrossPlantTransfers",
                columns: table => new
                {
                    CrossPlantTransferId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TransferDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FromWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToWhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    ToPlantId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossPlantTransfers", x => x.CrossPlantTransferId);
                });

            migrationBuilder.CreateTable(
                name: "CrossPlantTransferLines",
                columns: table => new
                {
                    CrossPlantTransferId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FromLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToLocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QtyShipped = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    QtyReceived = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossPlantTransferLines", x => new { x.CrossPlantTransferId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_CrossPlantTransferLines_CrossPlantTransfers_CrossPlantTrans~",
                        column: x => x.CrossPlantTransferId,
                        principalTable: "CrossPlantTransfers",
                        principalColumn: "CrossPlantTransferId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[] { "CrossPlantTransfer", 2, "跨廠調撥單", true, true, "XP", 1, "-", 5 });

            migrationBuilder.InsertData(
                table: "InventoryTransactionTypes",
                columns: new[] { "TypeCode", "CreatedAt", "Direction", "IsActive", "IsSystemType", "RequireDoc", "RequireLot", "SourceTypeCode", "TypeCategory", "TypeName", "UpdatedAt" },
                values: new object[,]
                {
                    { "XP", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), -1, true, true, true, false, null, "Transfer", "跨廠調撥出", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { "XI", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), 1, true, true, true, false, "XP", "Transfer", "跨廠調撥入", new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrossPlantTransfers_PlantId_TransferDate",
                table: "CrossPlantTransfers",
                columns: new[] { "PlantId", "TransferDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CrossPlantTransfers_ToPlantId_TransferDate",
                table: "CrossPlantTransfers",
                columns: new[] { "ToPlantId", "TransferDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrossPlantTransferLines");

            migrationBuilder.DropTable(
                name: "CrossPlantTransfers");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "CrossPlantTransfer");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "XI");

            migrationBuilder.DeleteData(
                table: "InventoryTransactionTypes",
                keyColumn: "TypeCode",
                keyValue: "XP");
        }
    }
}
