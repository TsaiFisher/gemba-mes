using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Modules.QA.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocChunkEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float[]>(
                name: "Embedding",
                table: "DocChunks",
                type: "real[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDim",
                table: "DocChunks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "DocChunks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingDim",
                table: "DocChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "DocChunks");
        }
    }
}
