using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartFactory.MES.Modules.QA.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitDocChunk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocChunks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FilePath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Lang = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HeadingPath = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Module = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MenuKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DocUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Lexemes = table.Column<string>(type: "text", nullable: false),
                    ImagePaths = table.Column<List<string>>(type: "text[]", nullable: false),
                    IndexedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocChunks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocChunks_ContentHash",
                table: "DocChunks",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_DocChunks_FilePath_Lang_ChunkIndex",
                table: "DocChunks",
                columns: new[] { "FilePath", "Lang", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocChunks_Lang_MenuKey",
                table: "DocChunks",
                columns: new[] { "Lang", "MenuKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocChunks");
        }
    }
}
