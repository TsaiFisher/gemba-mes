using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Modules.QA.Data.Migrations
{
    /// <summary>
    /// 詞彙通道把標題與內文分成兩欄（待決 #48）。
    ///
    /// <para><b>這支 migration 只加欄位，不搬資料。</b>舊列的 <c>Lexemes</c> 仍是「標題＋內文」平鋪、
    /// <c>HeadingLexemes</c> 是空字串，於是加權查詢對它們**等同於加權前的行為**——
    /// 不會錯，只是標題權重不生效。要把它們補成新格式必須重新切詞，而切詞在 C# 裡
    /// （<c>CjkTokenizer</c>），SQL 搬不動；硬在這裡拿 <c>split_part</c> 之類的手段拆，
    /// 拆出來的東西與索引期的切法不一致，那是比不搬更糟的失敗。</para>
    ///
    /// <para>真正的補救在兩邊各自的重建路徑上，且**都是自動的**：
    /// 手冊靠 <c>DocIndexBuilder.IndexFormatVersion</c> 摻進 <c>ContentHash</c>（雜湊全變 → 全檔重建），
    /// 擷取物本來就是每次啟動全量重寫。</para>
    /// </summary>
    public partial class AddDocChunkHeadingLexemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeadingLexemes",
                table: "DocChunks",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeadingLexemes",
                table: "DocChunks");
        }
    }
}
