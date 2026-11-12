using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_CostLayerConsumption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryCostLayerConsumptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LayerId = table.Column<int>(type: "integer", nullable: false),
                    PlantId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DocNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConsumedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RestoredQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCostLayerConsumptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCostLayerConsumptions_InventoryCostLayers_LayerId",
                        column: x => x.LayerId,
                        principalTable: "InventoryCostLayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostLayerConsumptions_LayerId",
                table: "InventoryCostLayerConsumptions",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCostLayerConsumptions_PlantId_DocNo_ProductCode_Lo~",
                table: "InventoryCostLayerConsumptions",
                columns: new[] { "PlantId", "DocNo", "ProductCode", "LocationCode", "LotNo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCostLayerConsumptions");
        }
    }
}
