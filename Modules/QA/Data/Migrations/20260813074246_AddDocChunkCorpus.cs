using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Modules.QA.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocChunkCorpus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Approved",
                table: "DocChunks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Corpus",
                table: "DocChunks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<bool>(
                name: "IsStale",
                table: "DocChunks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SourceHash",
                table: "DocChunks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourcePath",
                table: "DocChunks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_DocChunks_Corpus_Lang",
                table: "DocChunks",
                columns: new[] { "Corpus", "Lang" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocChunks_Corpus_Lang",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "Approved",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "Corpus",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "IsStale",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "SourceHash",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "SourcePath",
                table: "DocChunks");
        }
    }
}
