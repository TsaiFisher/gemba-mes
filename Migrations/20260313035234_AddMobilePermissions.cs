using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddMobilePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MobileModules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ModuleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IconClass = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileModules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MobileModuleActions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ModuleId = table.Column<string>(type: "text", nullable: false),
                    ActionKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileModuleActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MobileModuleActions_MobileModules_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "MobileModules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MobileRolePermissions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ModuleActionId = table.Column<string>(type: "text", nullable: false),
                    IsAllowed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MobileRolePermissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MobileRolePermissions_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MobileRolePermissions_MobileModuleActions_ModuleActionId",
                        column: x => x.ModuleActionId,
                        principalTable: "MobileModuleActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MobileModuleActions_ModuleId",
                table: "MobileModuleActions",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_MobileModules_ModuleKey",
                table: "MobileModules",
                column: "ModuleKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MobileRolePermissions_ModuleActionId",
                table: "MobileRolePermissions",
                column: "ModuleActionId");

            migrationBuilder.CreateIndex(
                name: "IX_MobileRolePermissions_RoleId_ModuleActionId",
                table: "MobileRolePermissions",
                columns: new[] { "RoleId", "ModuleActionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MobileRolePermissions");

            migrationBuilder.DropTable(
                name: "MobileModuleActions");

            migrationBuilder.DropTable(
                name: "MobileModules");
        }
    }
}
