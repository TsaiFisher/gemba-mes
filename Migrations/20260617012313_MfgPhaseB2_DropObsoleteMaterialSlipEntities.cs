using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MfgPhaseB2_DropObsoleteMaterialSlipEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaterialIssueSlipLines");

            migrationBuilder.DropTable(
                name: "MaterialRequisitionLines");

            migrationBuilder.DropTable(
                name: "MaterialIssueSlips");

            migrationBuilder.DropTable(
                name: "MaterialRequisitions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaterialIssueSlips",
                columns: table => new
                {
                    IssueSlipId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IssuedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialIssueSlips", x => x.IssueSlipId);
                });

            migrationBuilder.CreateTable(
                name: "MaterialRequisitions",
                columns: table => new
                {
                    RequisitionId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialRequisitions", x => x.RequisitionId);
                });

            migrationBuilder.CreateTable(
                name: "MaterialIssueSlipLines",
                columns: table => new
                {
                    IssueSlipId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    BomSeq = table.Column<int>(type: "integer", nullable: false),
                    IssuedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RequiredQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ReturnedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialIssueSlipLines", x => new { x.IssueSlipId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_MaterialIssueSlipLines_MaterialIssueSlips_IssueSlipId",
                        column: x => x.IssueSlipId,
                        principalTable: "MaterialIssueSlips",
                        principalColumn: "IssueSlipId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaterialRequisitionLines",
                columns: table => new
                {
                    RequisitionId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ApprovedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BomSeq = table.Column<int>(type: "integer", nullable: false),
                    IssuedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OperationSeq = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    RequestedQty = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WorkOrderId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialRequisitionLines", x => new { x.RequisitionId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_MaterialRequisitionLines_MaterialRequisitions_RequisitionId",
                        column: x => x.RequisitionId,
                        principalTable: "MaterialRequisitions",
                        principalColumn: "RequisitionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialIssueSlips_PlantId_IssueSlipId",
                table: "MaterialIssueSlips",
                columns: new[] { "PlantId", "IssueSlipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialRequisitions_PlantId_RequisitionId",
                table: "MaterialRequisitions",
                columns: new[] { "PlantId", "RequisitionId" },
                unique: true);
        }
    }
}
