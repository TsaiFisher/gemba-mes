using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class OutsourceRework_PhaseF : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutsourceReworks",
                columns: table => new
                {
                    OutsourceReworkId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OriginalDispatchId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    OutsourcePurchaseOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SourceNcrId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ReworkQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReworkReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LiabilityParty = table.Column<int>(type: "integer", nullable: false),
                    VendorLiablePct = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    VendorChargebackAmt = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReworkProcessingFee = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceReworks", x => x.OutsourceReworkId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutsourceReworks");
        }
    }
}
