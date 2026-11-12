using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class ORD_WarehouseOps_GoodsShipment_SalesReturn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "QtyPutAway",
                table: "SalesReturnLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "QtyPutAway",
                table: "GoodsShipmentLines",
                type: "numeric(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "GoodsShipmentLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GoodsShipmentId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
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
                    table.PrimaryKey("PK_GoodsShipmentLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoodsShipmentLocationDetails_GoodsShipmentLines_GoodsShipme~",
                        columns: x => new { x.GoodsShipmentId, x.LineNo },
                        principalTable: "GoodsShipmentLines",
                        principalColumns: new[] { "GoodsShipmentId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturnLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalesReturnId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
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
                    table.PrimaryKey("PK_SalesReturnLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesReturnLocationDetails_SalesReturnLines_SalesReturnId_L~",
                        columns: x => new { x.SalesReturnId, x.LineNo },
                        principalTable: "SalesReturnLines",
                        principalColumns: new[] { "SalesReturnId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoodsShipmentLocationDetails_GoodsShipmentId_LineNo",
                table: "GoodsShipmentLocationDetails",
                columns: new[] { "GoodsShipmentId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLocationDetails_SalesReturnId_LineNo",
                table: "SalesReturnLocationDetails",
                columns: new[] { "SalesReturnId", "LineNo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoodsShipmentLocationDetails");

            migrationBuilder.DropTable(
                name: "SalesReturnLocationDetails");

            migrationBuilder.DropColumn(
                name: "QtyPutAway",
                table: "SalesReturnLines");

            migrationBuilder.DropColumn(
                name: "QtyPutAway",
                table: "GoodsShipmentLines");
        }
    }
}
