using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Modules.QA.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQaTurn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QaTurns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundNo = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToolKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ParamsJson = table.Column<string>(type: "text", nullable: true),
                    CandidatesJson = table.Column<string>(type: "text", nullable: true),
                    RowsReturned = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QaTurns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QaTurns_CreatedAt",
                table: "QaTurns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QaTurns_ThreadId",
                table: "QaTurns",
                column: "ThreadId");

            migrationBuilder.CreateIndex(
                name: "IX_QaTurns_UserId_Id",
                table: "QaTurns",
                columns: new[] { "UserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QaTurns");
        }
    }
}
