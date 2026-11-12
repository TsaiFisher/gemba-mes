using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_GlobalMasterData_RemovePlantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Uoms_PlantId_UomCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Taxes_PlantId_TaxCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Products_PlantId_ProductCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_ProductCategories_PlantId_CategoryCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_InventoryTransactionTypes_PlantId_TypeCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Currencies_PlantId_CurrencyCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Containers_PlantId_ContainerCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Companies_PlantId_CompanyCode""");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_CodeEntries_PlantId_Category_Code""");

            migrationBuilder.Sql(@"ALTER TABLE ""Uoms"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""Taxes"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""Products"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""ProductCategories"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""InventoryTransactionTypes"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""Currencies"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""Containers"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""Companies"" DROP COLUMN IF EXISTS ""PlantId""");
            migrationBuilder.Sql(@"ALTER TABLE ""CodeEntries"" DROP COLUMN IF EXISTS ""PlantId""");

            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Uoms_UomCode"" ON ""Uoms""(""UomCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Taxes_TaxCode"" ON ""Taxes""(""TaxCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Products_ProductCode"" ON ""Products""(""ProductCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ProductCategories_CategoryCode"" ON ""ProductCategories""(""CategoryCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_InventoryTransactionTypes_TypeCode"" ON ""InventoryTransactionTypes""(""TypeCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Currencies_CurrencyCode"" ON ""Currencies""(""CurrencyCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Containers_ContainerCode"" ON ""Containers""(""ContainerCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Companies_CompanyCode"" ON ""Companies""(""CompanyCode"")");
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CodeEntries_Category_Code"" ON ""CodeEntries""(""Category"", ""Code"")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Uoms_UomCode",
                table: "Uoms");

            migrationBuilder.DropIndex(
                name: "IX_Taxes_TaxCode",
                table: "Taxes");

            migrationBuilder.DropIndex(
                name: "IX_Products_ProductCode",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_ProductCategories_CategoryCode",
                table: "ProductCategories");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactionTypes_TypeCode",
                table: "InventoryTransactionTypes");

            migrationBuilder.DropIndex(
                name: "IX_Currencies_CurrencyCode",
                table: "Currencies");

            migrationBuilder.DropIndex(
                name: "IX_Containers_ContainerCode",
                table: "Containers");

            migrationBuilder.DropIndex(
                name: "IX_Companies_CompanyCode",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_CodeEntries_Category_Code",
                table: "CodeEntries");

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "Uoms",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "Taxes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "Products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "ProductCategories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "InventoryTransactionTypes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "Currencies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "Containers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "CodeEntries",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Uoms_PlantId_UomCode",
                table: "Uoms",
                columns: new[] { "PlantId", "UomCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Taxes_PlantId_TaxCode",
                table: "Taxes",
                columns: new[] { "PlantId", "TaxCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_PlantId_ProductCode",
                table: "Products",
                columns: new[] { "PlantId", "ProductCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_PlantId_CategoryCode",
                table: "ProductCategories",
                columns: new[] { "PlantId", "CategoryCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactionTypes_PlantId_TypeCode",
                table: "InventoryTransactionTypes",
                columns: new[] { "PlantId", "TypeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_PlantId_CurrencyCode",
                table: "Currencies",
                columns: new[] { "PlantId", "CurrencyCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Containers_PlantId_ContainerCode",
                table: "Containers",
                columns: new[] { "PlantId", "ContainerCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_PlantId_CompanyCode",
                table: "Companies",
                columns: new[] { "PlantId", "CompanyCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CodeEntries_PlantId_Category_Code",
                table: "CodeEntries",
                columns: new[] { "PlantId", "Category", "Code" },
                unique: true);
        }
    }
}
