using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Approval_DelegationAndSkip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipSameApprover",
                table: "ApprovalTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ActiveDelegatorId",
                table: "ApprovalRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OnBehalfOfUserId",
                table: "ApprovalLogs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalDelegations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DelegatorId = table.Column<string>(type: "text", nullable: false),
                    DelegateId = table.Column<string>(type: "text", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalDelegations_AspNetUsers_DelegateId",
                        column: x => x.DelegateId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApprovalDelegations_AspNetUsers_DelegatorId",
                        column: x => x.DelegatorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_DelegateId",
                table: "ApprovalDelegations",
                column: "DelegateId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_DelegatorId",
                table: "ApprovalDelegations",
                column: "DelegatorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalDelegations");

            migrationBuilder.DropColumn(
                name: "SkipSameApprover",
                table: "ApprovalTemplates");

            migrationBuilder.DropColumn(
                name: "ActiveDelegatorId",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "OnBehalfOfUserId",
                table: "ApprovalLogs");
        }
    }
}
