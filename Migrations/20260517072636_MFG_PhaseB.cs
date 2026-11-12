using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_PhaseB : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 註：本 migration 僅新增 MFG Phase B 的 8 張資料表。
            // EF 工具因 ModelSnapshot 過期（merge=ours 漂移）誤生了一批
            // 針對 Phase A 既有欄位（ScrapRate/SopNumber/WorkStart 等，已由
            // 20260515031306_MFG_WorkCalendar_Routing_Ext 套用至 DB）的虛假
            // AddColumn/DropColumn，已手動移除；snapshot 已由 EF 重寫為正確全量。
            migrationBuilder.CreateTable(
                name: "MrpRuns",
                columns: table => new
                {
                    MrpRunId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrpRuns", x => x.MrpRunId);
                });

            migrationBuilder.CreateTable(
                name: "ProductionBatches",
                columns: table => new
                {
                    BatchId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MrpRunId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MrpRunLineSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    PlannedEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsPredictedLate = table.Column<bool>(type: "boolean", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionBatches", x => x.BatchId);
                });

            migrationBuilder.CreateTable(
                name: "MrpRunLines",
                columns: table => new
                {
                    MrpRunId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineSeq = table.Column<int>(type: "integer", nullable: false),
                    SalesOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SalesOrderLineSeq = table.Column<int>(type: "integer", nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequiredQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    StockQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    PurchaseQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    OutsourceQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    MakeQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ProductionBatchId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrpRunLines", x => new { x.MrpRunId, x.LineSeq });
                    table.ForeignKey(
                        name: "FK_MrpRunLines_MrpRuns_MrpRunId",
                        column: x => x.MrpRunId,
                        principalTable: "MrpRuns",
                        principalColumn: "MrpRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                columns: table => new
                {
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProductionBatchId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RoutingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ParentWorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReworkFromWorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PlannedStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlannedEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.WorkOrderId);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ProductionBatches_ProductionBatchId",
                        column: x => x.ProductionBatchId,
                        principalTable: "ProductionBatches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderOperations",
                columns: table => new
                {
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    OperationName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WorkCenterId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SetupTime = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UnitTime = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    BatchMode = table.Column<int>(type: "integer", nullable: false),
                    BatchQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    IsOutsource = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresInspection = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderOperations", x => new { x.WorkOrderId, x.OperationSeq });
                    table.ForeignKey(
                        name: "FK_WorkOrderOperations_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "WorkOrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dispatches",
                columns: table => new
                {
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    QtyDone = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PrevDispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SplitGroupId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsOutsource = table.Column<bool>(type: "boolean", nullable: false),
                    MachineCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ScheduledStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScheduledEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dispatches", x => x.DispatchId);
                    table.ForeignKey(
                        name: "FK_Dispatches_WorkOrderOperations_WorkOrderId_OperationSeq",
                        columns: x => new { x.WorkOrderId, x.OperationSeq },
                        principalTable: "WorkOrderOperations",
                        principalColumns: new[] { "WorkOrderId", "OperationSeq" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderMaterials",
                columns: table => new
                {
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    BomSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequiredQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChildWorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SourceMode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderMaterials", x => new { x.WorkOrderId, x.OperationSeq, x.BomSeq });
                    table.ForeignKey(
                        name: "FK_WorkOrderMaterials_WorkOrderOperations_WorkOrderId_Operatio~",
                        columns: x => new { x.WorkOrderId, x.OperationSeq },
                        principalTable: "WorkOrderOperations",
                        principalColumns: new[] { "WorkOrderId", "OperationSeq" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DispatchMaterials",
                columns: table => new
                {
                    DispatchId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BomSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequiredQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    IssuedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchMaterials", x => new { x.DispatchId, x.BomSeq });
                    table.ForeignKey(
                        name: "FK_DispatchMaterials_Dispatches_DispatchId",
                        column: x => x.DispatchId,
                        principalTable: "Dispatches",
                        principalColumn: "DispatchId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dispatches_PlantId_DispatchId",
                table: "Dispatches",
                columns: new[] { "PlantId", "DispatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dispatches_WorkOrderId_OperationSeq",
                table: "Dispatches",
                columns: new[] { "WorkOrderId", "OperationSeq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MrpRuns_PlantId_MrpRunId",
                table: "MrpRuns",
                columns: new[] { "PlantId", "MrpRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionBatches_PlantId_BatchId",
                table: "ProductionBatches",
                columns: new[] { "PlantId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_PlantId_WorkOrderId",
                table: "WorkOrders",
                columns: new[] { "PlantId", "WorkOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_ProductionBatchId",
                table: "WorkOrders",
                column: "ProductionBatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchMaterials");

            migrationBuilder.DropTable(
                name: "MrpRunLines");

            migrationBuilder.DropTable(
                name: "WorkOrderMaterials");

            migrationBuilder.DropTable(
                name: "Dispatches");

            migrationBuilder.DropTable(
                name: "MrpRuns");

            migrationBuilder.DropTable(
                name: "WorkOrderOperations");

            migrationBuilder.DropTable(
                name: "WorkOrders");

            migrationBuilder.DropTable(
                name: "ProductionBatches");
        }
    }
}
