using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Quality_SamplingPlan_FQC_RQC : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutivePasses",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LotCounter",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NormalVl",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PassesToNormal",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PassesToReduce",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PassesToSkipLot",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReducedVl",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresFQC",
                table: "InspectionSpecs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresRQC",
                table: "InspectionSpecs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SamplingState",
                table: "InspectionSpecs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "SkipLotEnabled",
                table: "InspectionSpecs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SkipLotFrequency",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TightenedVl",
                table: "InspectionSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutivePasses",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "LotCounter",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "NormalVl",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "PassesToNormal",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "PassesToReduce",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "PassesToSkipLot",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "ReducedVl",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "RequiresFQC",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "RequiresRQC",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "SamplingState",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "SkipLotEnabled",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "SkipLotFrequency",
                table: "InspectionSpecs");

            migrationBuilder.DropColumn(
                name: "TightenedVl",
                table: "InspectionSpecs");
        }
    }
}
