using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class INV_SeedDocNumberConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO ""DocNumberConfigs"" (""DocType"",""DocTypeName"",""Prefix"",""DateFormat"",""SeqLength"",""Separator"",""ResetCycle"",""IsSystemType"",""IsActive"") VALUES
('LoanDoc','借入借出單','LD',2,5,'-',1,TRUE,TRUE),
('SampleDoc','樣品客供單','SM',2,5,'-',1,TRUE,TRUE),
('ScrapDoc','報廢單','IJ',2,5,'-',1,TRUE,TRUE),
('StockAdjustment','調整單','IA',2,5,'-',1,TRUE,TRUE),
('OutsourceMaterial','委外發退料單','OM',2,5,'-',1,TRUE,TRUE),
('ReworkMaterial','重工發退料單','RM',2,5,'-',1,TRUE,TRUE),
('StockTransfer','同廠調撥單','WT',2,5,'-',1,TRUE,TRUE)
ON CONFLICT (""DocType"") DO NOTHING;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "LoanDoc");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "OutsourceMaterial");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "ReworkMaterial");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "SampleDoc");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "ScrapDoc");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "StockAdjustment");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "StockTransfer");
        }
    }
}
