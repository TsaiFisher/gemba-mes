using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class BAS_ProductCategoryTagFKToProductCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 移除舊的 CodeEntry-based tags（不存在於 ProductCategories 的記錄）
            migrationBuilder.Sql(@"
DELETE FROM ""ProductCategoryTags""
WHERE ""CategoryCode"" NOT IN (SELECT ""CategoryCode"" FROM ""ProductCategories"");
");

            migrationBuilder.AlterColumn<string>(
                name: "CategoryCode",
                table: "ProductCategoryTags",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategoryTags_CategoryCode",
                table: "ProductCategoryTags",
                column: "CategoryCode");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCategoryTags_ProductCategories_CategoryCode",
                table: "ProductCategoryTags",
                column: "CategoryCode",
                principalTable: "ProductCategories",
                principalColumn: "CategoryCode",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductCategoryTags_ProductCategories_CategoryCode",
                table: "ProductCategoryTags");

            migrationBuilder.DropIndex(
                name: "IX_ProductCategoryTags_CategoryCode",
                table: "ProductCategoryTags");

            migrationBuilder.AlterColumn<string>(
                name: "CategoryCode",
                table: "ProductCategoryTags",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }
    }
}
