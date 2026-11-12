using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// QCR 三個備註欄由 PG <c>text</c> 對齊成全庫慣例的 <c>varchar(500)</c>。
    ///
    /// 原本沒有長度上限，於是這三個畫面的超長輸入不會被前端 maxlength 或
    /// 模型驗證擋下，而其他畫面會——同一種操作在不同畫面行為不一致。
    ///
    /// ⚠️ 若既有資料有超過 500 字的備註，此 migration 會失敗（PG 不截斷，直接報
    /// 22001）。這是刻意的：`Program.cs` 的 `db.Database.Migrate()` 會讓 App 起不來，
    /// 逼出人工處置。不在此加 `left(Remarks, 500)` 預先截斷——那是不可逆的資料損失，
    /// 而且會安靜地發生。真的遇到時先查出那幾筆再決定。
    /// </summary>
    public partial class AlignRemarksMaxLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Remarks",
                table: "InstrumentSerials",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Remarks",
                table: "Instruments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Remarks",
                table: "InspectionSpecs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Remarks",
                table: "InstrumentSerials",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Remarks",
                table: "Instruments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Remarks",
                table: "InspectionSpecs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
