using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    CompanyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ShortName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsCustomer = table.Column<bool>(type: "boolean", nullable: false),
                    IsSupplier = table.Column<bool>(type: "boolean", nullable: false),
                    PaymentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxRate = table.Column<decimal>(type: "numeric", nullable: false),
                    PaymentTermDays = table.Column<int>(type: "integer", nullable: true),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    InvoiceNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    InvoiceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SalesPersonId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    AbcGrade = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.CompanyCode);
                });

            migrationBuilder.CreateTable(
                name: "Containers",
                columns: table => new
                {
                    ContainerCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContainerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Spec = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MaxCapacity = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Containers", x => x.ContainerCode);
                });

            migrationBuilder.CreateTable(
                name: "Currencies",
                columns: table => new
                {
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CurrencyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ShortName = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    DecimalPlaces = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currencies", x => x.CurrencyCode);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransactionTypes",
                columns: table => new
                {
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SourceTypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransactionTypes", x => x.TypeCode);
                    table.ForeignKey(
                        name: "FK_InventoryTransactionTypes_InventoryTransactionTypes_SourceT~",
                        column: x => x.SourceTypeCode,
                        principalTable: "InventoryTransactionTypes",
                        principalColumn: "TypeCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Spec = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RoutingId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SalesUom = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PurchaseUom = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockUom = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SafetyStock = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StandardCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    MrpMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: false),
                    UrgentLeadTimeDays = table.Column<int>(type: "integer", nullable: false),
                    OutsourceQtyPerDay = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ShelfLifeDays = table.Column<int>(type: "integer", nullable: true),
                    NetWeight = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    GrossWeight = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    BoxQty = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    IsLotControlled = table.Column<bool>(type: "boolean", nullable: false),
                    InspectionLevelN = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    InspectionLevelR = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    InspectionLevelC = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.ProductCode);
                });

            migrationBuilder.CreateTable(
                name: "ShiftGroups",
                columns: table => new
                {
                    GroupCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GroupName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftGroups", x => x.GroupCode);
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    ShiftCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ShiftName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    WorkMinutes = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.ShiftCode);
                });

            migrationBuilder.CreateTable(
                name: "SpecialDays",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    DayType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CompensatoryDayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecialDays", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "Taxes",
                columns: table => new
                {
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaxName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Taxes", x => x.TaxCode);
                });

            migrationBuilder.CreateTable(
                name: "Toolings",
                columns: table => new
                {
                    ToolingCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ToolingName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Spec = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CavityCount = table.Column<int>(type: "integer", nullable: true),
                    ToolingType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Toolings", x => x.ToolingCode);
                });

            migrationBuilder.CreateTable(
                name: "Uoms",
                columns: table => new
                {
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UomName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecimalPlaces = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uoms", x => x.UomCode);
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                columns: table => new
                {
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WhName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsMrpWarehouse = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.WhCode);
                });

            migrationBuilder.CreateTable(
                name: "WorkCenters",
                columns: table => new
                {
                    WorkCenterCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WorkCenterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DepartmentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ShiftGroupCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsProductionLine = table.Column<bool>(type: "boolean", nullable: false),
                    AdditionalRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    StandardLaborCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCenters", x => x.WorkCenterCode);
                });

            migrationBuilder.CreateTable(
                name: "CompanyContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SeqNo = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ContactName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Fax = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Mobile = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyContacts_Companies_CompanyCode",
                        column: x => x.CompanyCode,
                        principalTable: "Companies",
                        principalColumn: "CompanyCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CurrencyRates",
                columns: table => new
                {
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BuyRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    SellRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyRates", x => new { x.CurrencyCode, x.RateDate });
                    table.ForeignKey(
                        name: "FK_CurrencyRates_Currencies_CurrencyCode",
                        column: x => x.CurrencyCode,
                        principalTable: "Currencies",
                        principalColumn: "CurrencyCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CategoryCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => new { x.ProductCode, x.CategoryCode });
                    table.ForeignKey(
                        name: "FK_ProductCategories_Products_ProductCode",
                        column: x => x.ProductCode,
                        principalTable: "Products",
                        principalColumn: "ProductCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShiftGroupDays",
                columns: table => new
                {
                    GroupCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    Shift1Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Shift2Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Shift3Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Shift4Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WorkStart = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    WorkEnd = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftGroupDays", x => new { x.GroupCode, x.DayOfWeek });
                    table.ForeignKey(
                        name: "FK_ShiftGroupDays_ShiftGroups_GroupCode",
                        column: x => x.GroupCode,
                        principalTable: "ShiftGroups",
                        principalColumn: "GroupCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UomConversions",
                columns: table => new
                {
                    FromUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ToUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Numerator = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Denominator = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UomConversions", x => new { x.FromUomCode, x.ToUomCode });
                    table.ForeignKey(
                        name: "FK_UomConversions_Uoms_FromUomCode",
                        column: x => x.FromUomCode,
                        principalTable: "Uoms",
                        principalColumn: "UomCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StorageLocations",
                columns: table => new
                {
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LocationName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LocationType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageLocations", x => new { x.WhCode, x.LocationCode });
                    table.ForeignKey(
                        name: "FK_StorageLocations_Warehouses_WhCode",
                        column: x => x.WhCode,
                        principalTable: "Warehouses",
                        principalColumn: "WhCode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_PlantId_CompanyCode",
                table: "Companies",
                columns: new[] { "PlantId", "CompanyCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyContacts_CompanyCode_SeqNo",
                table: "CompanyContacts",
                columns: new[] { "CompanyCode", "SeqNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Containers_PlantId_ContainerCode",
                table: "Containers",
                columns: new[] { "PlantId", "ContainerCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Currencies_PlantId_CurrencyCode",
                table: "Currencies",
                columns: new[] { "PlantId", "CurrencyCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactionTypes_PlantId_TypeCode",
                table: "InventoryTransactionTypes",
                columns: new[] { "PlantId", "TypeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactionTypes_SourceTypeCode",
                table: "InventoryTransactionTypes",
                column: "SourceTypeCode");

            migrationBuilder.CreateIndex(
                name: "IX_Products_PlantId_ProductCode",
                table: "Products",
                columns: new[] { "PlantId", "ProductCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShiftGroups_PlantId_GroupCode",
                table: "ShiftGroups",
                columns: new[] { "PlantId", "GroupCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shifts_PlantId_ShiftCode",
                table: "Shifts",
                columns: new[] { "PlantId", "ShiftCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpecialDays_PlantId_Date",
                table: "SpecialDays",
                columns: new[] { "PlantId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Taxes_PlantId_TaxCode",
                table: "Taxes",
                columns: new[] { "PlantId", "TaxCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Toolings_PlantId_ToolingCode",
                table: "Toolings",
                columns: new[] { "PlantId", "ToolingCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Uoms_PlantId_UomCode",
                table: "Uoms",
                columns: new[] { "PlantId", "UomCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_PlantId_WhCode",
                table: "Warehouses",
                columns: new[] { "PlantId", "WhCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCenters_PlantId_WorkCenterCode",
                table: "WorkCenters",
                columns: new[] { "PlantId", "WorkCenterCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyContacts");

            migrationBuilder.DropTable(
                name: "Containers");

            migrationBuilder.DropTable(
                name: "CurrencyRates");

            migrationBuilder.DropTable(
                name: "InventoryTransactionTypes");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropTable(
                name: "ShiftGroupDays");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "SpecialDays");

            migrationBuilder.DropTable(
                name: "StorageLocations");

            migrationBuilder.DropTable(
                name: "Taxes");

            migrationBuilder.DropTable(
                name: "Toolings");

            migrationBuilder.DropTable(
                name: "UomConversions");

            migrationBuilder.DropTable(
                name: "WorkCenters");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "Currencies");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "ShiftGroups");

            migrationBuilder.DropTable(
                name: "Warehouses");

            migrationBuilder.DropTable(
                name: "Uoms");
        }
    }
}
