using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class PUR_VendorEvaluation_HasDataFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 既有列一律 true，不是 false。
            //
            // 舊資料的 QualityScore 可能是真實評分，也可能是「期間無進貨」時填進去的 100——
            // 存下來之後兩者就分不出來了。標成 false 等於對每一筆舊評核宣告「這是沒有依據的」，
            // 那是我們並不知道的事；標成 true 則維持現狀（該看不出來的還是看不出來），
            // 不會憑空製造出一批誤報。下一次重新評核時後端會填上真正的值。
            migrationBuilder.AddColumn<bool>(
                name: "DeliveryHasData",
                table: "VendorEvaluations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "QualityHasData",
                table: "VendorEvaluations",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryHasData",
                table: "VendorEvaluations");

            migrationBuilder.DropColumn(
                name: "QualityHasData",
                table: "VendorEvaluations");
        }
    }
}
