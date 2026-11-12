using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PUR_VendorPriceList_Company_Qualification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── BAS.Company：供應商資格狀態（pur-domain.md §3）──────────────
            migrationBuilder.AddColumn<int>(
                name: "QualificationStatus",
                table: "Companies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "QualificationExpiry",
                table: "Companies",
                type: "date",
                nullable: true);

            // ── PUR.VendorPriceList：廠商單價目錄（pur-domain.md §2）─────────
            migrationBuilder.CreateTable(
                name: "VendorPriceLists",
                columns: table => new
                {
                    Id             = table.Column<int>(type: "integer", nullable: false)
                                         .Annotation("Npgsql:ValueGenerationStrategy",
                                                     NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierCode   = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductCode    = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrencyCode   = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UnitPrice      = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    MinQty         = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    ValidFrom      = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo        = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive       = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Remarks        = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt      = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt      = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorPriceLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorPriceLists_Companies_SupplierCode",
                        column: x => x.SupplierCode,
                        principalTable: "Companies",
                        principalColumn: "CompanyCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorPriceLists_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorPriceLists_Products_ProductCode",
                        column: x => x.ProductCode,
                        principalTable: "Products",
                        principalColumn: "ProductCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorPriceLists_CurrencyCode",
                table: "VendorPriceLists",
                column: "CurrencyCode");

            migrationBuilder.CreateIndex(
                name: "IX_VendorPriceLists_ProductCode",
                table: "VendorPriceLists",
                column: "ProductCode");

            migrationBuilder.CreateIndex(
                name: "IX_VendorPriceLists_SupplierCode_ProductCode_CurrencyCode_ValidFrom",
                table: "VendorPriceLists",
                columns: new[] { "SupplierCode", "ProductCode", "CurrencyCode", "ValidFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "VendorPriceLists");

            migrationBuilder.DropColumn(name: "QualificationStatus", table: "Companies");
            migrationBuilder.DropColumn(name: "QualificationExpiry",  table: "Companies");
        }
    }
}
