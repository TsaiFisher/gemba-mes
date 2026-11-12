using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentApprovalConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveApproverId",
                table: "ApprovalRequests",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentApprovalConfigs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DocumentTypeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsApprovalRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalTemplateId = table.Column<string>(type: "text", nullable: true),
                    PlantId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentApprovalConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentApprovalConfigs_ApprovalTemplates_ApprovalTemplateId",
                        column: x => x.ApprovalTemplateId,
                        principalTable: "ApprovalTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentApprovalConfigs_ApprovalTemplateId",
                table: "DocumentApprovalConfigs",
                column: "ApprovalTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentApprovalConfigs_PlantId_DocumentType",
                table: "DocumentApprovalConfigs",
                columns: new[] { "PlantId", "DocumentType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentApprovalConfigs");

            migrationBuilder.DropColumn(
                name: "ActiveApproverId",
                table: "ApprovalRequests");
        }
    }
}
