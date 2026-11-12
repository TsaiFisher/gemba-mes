using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 10 個 decimal 欄位從未宣告 <c>[Column(TypeName)]</c>，EF 因此產出**無界的**
    /// PostgreSQL <c>numeric</c>，與同名同義的兄弟欄位不一致。
    ///
    /// 最明顯的是匯率：全庫 11 個 <c>ExchangeRate</c> 都是 <c>decimal(18,6)</c>，
    /// 只有出貨明細與銷退明細這兩個是無界 numeric；稅率也一樣，13 個兄弟欄位是
    /// <c>decimal(8,4)</c>，只有 <c>Company.TaxRate</c> 無界。
    /// 無界不是「更精確」，是「沒人決定過」——同一個數字在不同單據上存進不同精度，
    /// 對帳時的差異會落在小數末位，看起來像四捨五入誤差而不像結構問題。
    ///
    /// ⚠️ PostgreSQL 收窄 numeric 是**四捨五入**而非報錯（與 varchar 不同）。
    /// 既有資料若有超過目標位數的值會被無聲修掉；此處收窄的都是應用層本就
    /// 以 ≤4 位寫入的欄位（數量走 RoundQtyAsync、稅率走稅別主檔），故可接受。
    ///
    /// 刻意**不動**的：檢驗量測值（InspectionItem／InspectionSpecItem 的
    /// LowerSpec／UpperSpec／TargetValue／MeasuredValue／TargetStdDev 共 9 欄）。
    /// 那些的精度由量測儀器決定，不受 UOM 小數位數約束，維持無界是正確的。
    /// </summary>
    public partial class AlignUnboundedNumericColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                table: "SalesReturnLines",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "MinLotSize",
                table: "Products",
                type: "numeric(18,4)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "LotMultiple",
                table: "Products",
                type: "numeric(18,4)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "SampleQty",
                table: "MobileIqcRecords",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "PassQty",
                table: "MobileIqcRecords",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "FailQty",
                table: "MobileIqcRecords",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "LotSize",
                table: "InspectionOrders",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                table: "GoodsShipmentLines",
                type: "numeric(18,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "DifferenceAmount",
                table: "DataAuditFindings",
                type: "numeric(18,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "TaxRate",
                table: "Companies",
                type: "numeric(8,4)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                table: "SalesReturnLines",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "MinLotSize",
                table: "Products",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "LotMultiple",
                table: "Products",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "SampleQty",
                table: "MobileIqcRecords",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "PassQty",
                table: "MobileIqcRecords",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "FailQty",
                table: "MobileIqcRecords",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "LotSize",
                table: "InspectionOrders",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "ExchangeRate",
                table: "GoodsShipmentLines",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,6)");

            migrationBuilder.AlterColumn<decimal>(
                name: "DifferenceAmount",
                table: "DataAuditFindings",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)");

            migrationBuilder.AlterColumn<decimal>(
                name: "TaxRate",
                table: "Companies",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(8,4)");
        }
    }
}
