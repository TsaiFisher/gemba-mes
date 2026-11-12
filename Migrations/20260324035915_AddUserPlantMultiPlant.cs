using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPlantMultiPlant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PlantId",
                table: "AspNetUsers",
                newName: "ActivePlantId");

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
                name: "IX_AspNetUsers_ActivePlantId",
                table: "AspNetUsers",
                column: "ActivePlantId");

            // 資料遷移：將現有 ActivePlantId 寫入 UserPlants（僅當廠別存在時）
            migrationBuilder.Sql(@"
                INSERT INTO ""UserPlants"" (""UserId"", ""PlantId"")
                SELECT u.""Id"", u.""ActivePlantId""
                FROM ""AspNetUsers"" u
                INNER JOIN ""Plants"" p ON p.""Id"" = u.""ActivePlantId""
                WHERE u.""ActivePlantId"" IS NOT NULL
                  AND u.""ActivePlantId"" <> ''
                ON CONFLICT DO NOTHING;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_UserPlants_PlantId",
                table: "UserPlants",
                column: "PlantId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Plants_ActivePlantId",
                table: "AspNetUsers",
                column: "ActivePlantId",
                principalTable: "Plants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Plants_ActivePlantId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "UserPlants");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ActivePlantId",
                table: "AspNetUsers");

            migrationBuilder.RenameColumn(
                name: "ActivePlantId",
                table: "AspNetUsers",
                newName: "PlantId");
        }
    }
}
