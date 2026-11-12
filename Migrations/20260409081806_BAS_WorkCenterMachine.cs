using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_WorkCenterMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkCenterMachines",
                columns: table => new
                {
                    WorkCenterCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MachineCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MachineName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MachineStatus = table.Column<int>(type: "integer", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCenterMachines", x => new { x.WorkCenterCode, x.MachineCode });
                    table.ForeignKey(
                        name: "FK_WorkCenterMachines_WorkCenters_WorkCenterCode",
                        column: x => x.WorkCenterCode,
                        principalTable: "WorkCenters",
                        principalColumn: "WorkCenterCode",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkCenterMachines");
        }
    }
}
