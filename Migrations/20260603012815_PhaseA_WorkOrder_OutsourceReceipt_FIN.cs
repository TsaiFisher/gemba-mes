using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PhaseA_WorkOrder_OutsourceReceipt_FIN : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "WorkOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "APInvoices",
                columns: table => new
                {
                    APInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceDocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_APInvoices", x => x.APInvoiceId);
                });

            migrationBuilder.CreateTable(
                name: "APPayments",
                columns: table => new
                {
                    APPaymentId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeGainLoss = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    BankAccountId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_APPayments", x => x.APPaymentId);
                });

            migrationBuilder.CreateTable(
                name: "ARInvoices",
                columns: table => new
                {
                    ARInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CustomerCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReceivedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourceDocType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SourceDocId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ARInvoices", x => x.ARInvoiceId);
                });

            migrationBuilder.CreateTable(
                name: "ARReceipts",
                columns: table => new
                {
                    ARReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CustomerCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ExchangeGainLoss = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    BankAccountId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ARReceipts", x => x.ARReceiptId);
                });

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BankName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Balance = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceReceipts",
                columns: table => new
                {
                    OutsourceReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OutsourcePurchaseOrderId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TypeCategory = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalStatus = table.Column<int>(type: "integer", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedByRoleId = table.Column<string>(type: "text", nullable: true),
                    OrganizationUnitPath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceReceipts", x => x.OutsourceReceiptId);
                });

            migrationBuilder.CreateTable(
                name: "APInvoiceLines",
                columns: table => new
                {
                    APInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SourceLineNo = table.Column<int>(type: "integer", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_APInvoiceLines", x => new { x.APInvoiceId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_APInvoiceLines_APInvoices_APInvoiceId",
                        column: x => x.APInvoiceId,
                        principalTable: "APInvoices",
                        principalColumn: "APInvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "APPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    APPaymentId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    APInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AllocatedAmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_APPaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_APPaymentAllocations_APInvoices_APInvoiceId",
                        column: x => x.APInvoiceId,
                        principalTable: "APInvoices",
                        principalColumn: "APInvoiceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_APPaymentAllocations_APPayments_APPaymentId",
                        column: x => x.APPaymentId,
                        principalTable: "APPayments",
                        principalColumn: "APPaymentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ARInvoiceLines",
                columns: table => new
                {
                    ARInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Qty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    LineAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SourceLineNo = table.Column<int>(type: "integer", nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ARInvoiceLines", x => new { x.ARInvoiceId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_ARInvoiceLines_ARInvoices_ARInvoiceId",
                        column: x => x.ARInvoiceId,
                        principalTable: "ARInvoices",
                        principalColumn: "ARInvoiceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ARReceiptAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ARReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ARInvoiceId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    AllocatedAmountLocal = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ARReceiptAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ARReceiptAllocations_ARInvoices_ARInvoiceId",
                        column: x => x.ARInvoiceId,
                        principalTable: "ARInvoices",
                        principalColumn: "ARInvoiceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ARReceiptAllocations_ARReceipts_ARReceiptId",
                        column: x => x.ARReceiptId,
                        principalTable: "ARReceipts",
                        principalColumn: "ARReceiptId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceReceiptLines",
                columns: table => new
                {
                    OutsourceReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    OpoLineNo = table.Column<int>(type: "integer", nullable: true),
                    ProductCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReceiptQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    UomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    StockQty = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    StockUomCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxRate = table.Column<decimal>(type: "numeric(8,4)", nullable: false),
                    LineAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    WhCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LocationCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LotNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    QtyPutAway = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsourceReceiptLines", x => new { x.OutsourceReceiptId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_OutsourceReceiptLines_OutsourceReceipts_OutsourceReceiptId",
                        column: x => x.OutsourceReceiptId,
                        principalTable: "OutsourceReceipts",
                        principalColumn: "OutsourceReceiptId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutsourceReceiptLocationDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OutsourceReceiptId = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
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
                    table.PrimaryKey("PK_OutsourceReceiptLocationDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutsourceReceiptLocationDetails_OutsourceReceiptLines_Outso~",
                        columns: x => new { x.OutsourceReceiptId, x.LineNo },
                        principalTable: "OutsourceReceiptLines",
                        principalColumns: new[] { "OutsourceReceiptId", "LineNo" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "DateFormat", "DocTypeName", "IsActive", "IsSystemType", "Prefix", "ResetCycle", "Separator", "SeqLength" },
                values: new object[,]
                {
                    { "APInvoice", 2, "應付發票", true, true, "API", 1, "-", 5 },
                    { "APPayment", 2, "付款單", true, true, "APP", 1, "-", 5 },
                    { "ARInvoice", 2, "應收發票", true, true, "ARI", 1, "-", 5 },
                    { "ARReceipt", 2, "收款單", true, true, "ARR", 1, "-", 5 },
                    { "OutsourceReceipt", 2, "委外收貨單", true, true, "OR", 1, "-", 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_APInvoices_PlantId_APInvoiceId",
                table: "APInvoices",
                columns: new[] { "PlantId", "APInvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_APInvoices_SourceDocType_SourceDocId",
                table: "APInvoices",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_APPaymentAllocations_APInvoiceId",
                table: "APPaymentAllocations",
                column: "APInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_APPaymentAllocations_APPaymentId",
                table: "APPaymentAllocations",
                column: "APPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_APPayments_PlantId_APPaymentId",
                table: "APPayments",
                columns: new[] { "PlantId", "APPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ARInvoices_PlantId_ARInvoiceId",
                table: "ARInvoices",
                columns: new[] { "PlantId", "ARInvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ARInvoices_SourceDocType_SourceDocId",
                table: "ARInvoices",
                columns: new[] { "SourceDocType", "SourceDocId" },
                unique: true,
                filter: "\"SourceDocType\" IS NOT NULL AND \"SourceDocId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ARReceiptAllocations_ARInvoiceId",
                table: "ARReceiptAllocations",
                column: "ARInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ARReceiptAllocations_ARReceiptId",
                table: "ARReceiptAllocations",
                column: "ARReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_ARReceipts_PlantId_ARReceiptId",
                table: "ARReceipts",
                columns: new[] { "PlantId", "ARReceiptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_PlantId_AccountId",
                table: "BankAccounts",
                columns: new[] { "PlantId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceReceiptLocationDetails_OutsourceReceiptId_LineNo",
                table: "OutsourceReceiptLocationDetails",
                columns: new[] { "OutsourceReceiptId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_OutsourceReceipts_PlantId_OutsourceReceiptId",
                table: "OutsourceReceipts",
                columns: new[] { "PlantId", "OutsourceReceiptId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "APInvoiceLines");

            migrationBuilder.DropTable(
                name: "APPaymentAllocations");

            migrationBuilder.DropTable(
                name: "ARInvoiceLines");

            migrationBuilder.DropTable(
                name: "ARReceiptAllocations");

            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropTable(
                name: "OutsourceReceiptLocationDetails");

            migrationBuilder.DropTable(
                name: "APInvoices");

            migrationBuilder.DropTable(
                name: "APPayments");

            migrationBuilder.DropTable(
                name: "ARInvoices");

            migrationBuilder.DropTable(
                name: "ARReceipts");

            migrationBuilder.DropTable(
                name: "OutsourceReceiptLines");

            migrationBuilder.DropTable(
                name: "OutsourceReceipts");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "APInvoice");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "APPayment");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "ARInvoice");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "ARReceipt");

            migrationBuilder.DeleteData(
                table: "DocNumberConfigs",
                keyColumn: "DocType",
                keyValue: "OutsourceReceipt");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "WorkOrders");
        }
    }
}
