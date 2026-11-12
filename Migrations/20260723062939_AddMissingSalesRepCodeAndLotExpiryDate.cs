using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 補兩個「模型與 snapshot 都有、卻沒有任何 migration 建立」的欄位。
    ///
    /// 症狀：乾淨資料庫（全新部署／CI）跑起來後，任何查到 SalesOrders 或 Inventories
    /// 的畫面都會 <c>42703 column does not exist</c>。例如 MRP 執行的新增面板
    /// （需求來源讀訂單）直接 500。現有的 dev/正式庫因為欄位是早期就在，看不出問題。
    ///
    /// 為何 <c>dotnet ef migrations add</c> 產不出來：snapshot 已經含這兩個欄位
    /// （模型＝snapshot），EF 認定無差異 → 產出空 migration。所以 Up() 由手寫，
    /// 且**不得**同時更新 snapshot（snapshot 本來就是對的）。
    ///
    /// 用 <c>IF NOT EXISTS</c> 而非 <c>AddColumn</c>：既有資料庫已經有這兩欄，
    /// AddColumn 會 42701 直接炸掉升級。
    /// </summary>
    public partial class AddMissingSalesRepCodeAndLotExpiryDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"SalesOrders\" ADD COLUMN IF NOT EXISTS \"SalesRepCode\" character varying(50);");
            migrationBuilder.Sql(
                "ALTER TABLE \"Inventories\" ADD COLUMN IF NOT EXISTS \"LotExpiryDate\" date;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"SalesOrders\" DROP COLUMN IF EXISTS \"SalesRepCode\";");
            migrationBuilder.Sql("ALTER TABLE \"Inventories\" DROP COLUMN IF EXISTS \"LotExpiryDate\";");
        }
    }
}
