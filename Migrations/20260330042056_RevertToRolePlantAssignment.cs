using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class RevertToRolePlantAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""UserPlants"";");
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""RolePlantAssignments"" (
                    ""RoleId"" text NOT NULL,
                    ""PlantId"" text NOT NULL,
                    ""IsDefault"" boolean NOT NULL,
                    CONSTRAINT ""PK_RolePlantAssignments"" PRIMARY KEY (""RoleId"", ""PlantId""),
                    CONSTRAINT ""FK_RolePlantAssignments_AspNetRoles_RoleId"" FOREIGN KEY (""RoleId"") REFERENCES ""AspNetRoles""(""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_RolePlantAssignments_Plants_PlantId"" FOREIGN KEY (""PlantId"") REFERENCES ""Plants""(""Id"") ON DELETE CASCADE
                );");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_RolePlantAssignments_PlantId"" ON ""RolePlantAssignments""(""PlantId"")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolePlantAssignments");

            migrationBuilder.CreateTable(
                name: "UserPlants",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PlantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPlants", x => new { x.UserId, x.PlantId });
                    table.ForeignKey(
                        name: "FK_UserPlants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserPlants_Plants_PlantId",
                        column: x => x.PlantId,
                        principalTable: "Plants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPlants_PlantId",
                table: "UserPlants",
                column: "PlantId");
        }
    }
}
