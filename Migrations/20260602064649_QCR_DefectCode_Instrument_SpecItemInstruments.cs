using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class QCR_DefectCode_Instrument_SpecItemInstruments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstrumentCode1",
                table: "InspectionSpecItems",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstrumentCode2",
                table: "InspectionSpecItems",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstrumentCode3",
                table: "InspectionSpecItems",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DefectCodes",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FgIQC = table.Column<bool>(type: "boolean", nullable: false),
                    FgIPQC = table.Column<bool>(type: "boolean", nullable: false),
                    FgFQC = table.Column<bool>(type: "boolean", nullable: false),
                    FgRQC = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DefectCodes", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    InstrumentCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InstrumentName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CalibrationFrequencyDays = table.Column<int>(type: "integer", nullable: false),
                    CalibrationMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.InstrumentCode);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentSerials",
                columns: table => new
                {
                    SerialId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InstrumentCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SerialNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DepartmentCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CustodianId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ObsoleteDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Spec = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Tolerance = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Accuracy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CalibrationFrequencyDays = table.Column<int>(type: "integer", nullable: true),
                    CalibrationMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastCalibrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextCalibrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Remarks = table.Column<string>(type: "text", nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentSerials", x => x.SerialId);
                    table.ForeignKey(
                        name: "FK_InstrumentSerials_Instruments_InstrumentCode",
                        column: x => x.InstrumentCode,
                        principalTable: "Instruments",
                        principalColumn: "InstrumentCode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DefectCodes_Name",
                table: "DefectCodes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentSerials_InstrumentCode",
                table: "InstrumentSerials",
                column: "InstrumentCode");

            migrationBuilder.CreateIndex(
                name: "IX_InstrumentSerials_PlantId_InstrumentCode_SerialNo",
                table: "InstrumentSerials",
                columns: new[] { "PlantId", "InstrumentCode", "SerialNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DefectCodes");

            migrationBuilder.DropTable(
                name: "InstrumentSerials");

            migrationBuilder.DropTable(
                name: "Instruments");

            migrationBuilder.DropColumn(
                name: "InstrumentCode1",
                table: "InspectionSpecItems");

            migrationBuilder.DropColumn(
                name: "InstrumentCode2",
                table: "InspectionSpecItems");

            migrationBuilder.DropColumn(
                name: "InstrumentCode3",
                table: "InspectionSpecItems");
        }
    }
}
