using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddInventorySchemaChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductCategories_Products_ProductCode",
                table: "ProductCategories");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductCategories",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "ProductCode",
                table: "ProductCategories");

            migrationBuilder.AddColumn<int>(
                name: "WarehouseType",
                table: "Warehouses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CategoryCode",
                table: "Products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CategoryCode",
                table: "ProductCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<string>(
                name: "CategoryName",
                table: "ProductCategories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CostMethod",
                table: "ProductCategories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ProductCategories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "DefaultLocationCode",
                table: "ProductCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultStockUomCode",
                table: "ProductCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultWarehouseCode",
                table: "ProductCategories",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableLotTracking",
                table: "ProductCategories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "ProductCategories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "ProductCategories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "ProductCategories",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "ProductCategories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "Direction",
                table: "InventoryTransactionTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemType",
                table: "InventoryTransactionTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireDoc",
                table: "InventoryTransactionTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireLot",
                table: "InventoryTransactionTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductCategories",
                table: "ProductCategories",
                column: "CategoryCode");

            migrationBuilder.CreateTable(
                name: "ProductCategoryTags",
                columns: table => new
                {
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CategoryCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategoryTags", x => new { x.ProductCode, x.CategoryCode });
                    table.ForeignKey(
                        name: "FK_ProductCategoryTags_Products_ProductCode",
                        column: x => x.ProductCode,
                        principalTable: "Products",
                        principalColumn: "ProductCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageLocations_LocationCode",
                table: "StorageLocations",
                column: "LocationCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_CategoryCode",
                table: "Products",
                column: "CategoryCode");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_PlantId_CategoryCode",
                table: "ProductCategories",
                columns: new[] { "PlantId", "CategoryCode" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductCategories_CategoryCode",
                table: "Products",
                column: "CategoryCode",
                principalTable: "ProductCategories",
                principalColumn: "CategoryCode",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductCategories_CategoryCode",
                table: "Products");

            migrationBuilder.DropTable(
                name: "ProductCategoryTags");

            migrationBuilder.DropIndex(
                name: "IX_StorageLocations_LocationCode",
                table: "StorageLocations");

            migrationBuilder.DropIndex(
                name: "IX_Products_CategoryCode",
                table: "Products");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductCategories",
                table: "ProductCategories");

            migrationBuilder.DropIndex(
                name: "IX_ProductCategories_PlantId_CategoryCode",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "WarehouseType",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "CategoryCode",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CategoryName",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "CostMethod",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "DefaultLocationCode",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "DefaultStockUomCode",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "DefaultWarehouseCode",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "EnableLotTracking",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "PlantId",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "InventoryTransactionTypes");

            migrationBuilder.DropColumn(
                name: "IsSystemType",
                table: "InventoryTransactionTypes");

            migrationBuilder.DropColumn(
                name: "RequireDoc",
                table: "InventoryTransactionTypes");

            migrationBuilder.DropColumn(
                name: "RequireLot",
                table: "InventoryTransactionTypes");

            migrationBuilder.AlterColumn<string>(
                name: "CategoryCode",
                table: "ProductCategories",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "ProductCode",
                table: "ProductCategories",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductCategories",
                table: "ProductCategories",
                columns: new[] { "ProductCode", "CategoryCode" });

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCategories_Products_ProductCode",
                table: "ProductCategories",
                column: "ProductCode",
                principalTable: "Products",
                principalColumn: "ProductCode",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
