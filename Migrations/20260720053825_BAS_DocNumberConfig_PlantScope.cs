using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// 單號設定加「適用廠別」（PlantId，""=預設規則）：
    /// - DocNumberConfigs PK：DocType → (DocType, PlantId)，既有列一律成為預設規則（PlantId=""）
    /// - DocNumberSequences.PlantId 語意改為「序號 scope」＝解析到的設定之適用廠別：
    ///   既有 per-plant 序號列以各 (DocType, Period) 的跨廠最大值併入預設 scope（""），
    ///   共用預設規則的廠別自此共用一條序號（連續編、跨廠不重號），舊 per-plant 列移除。
    /// 注意：EF 原本為此變更 scaffold 出整批種子 DeleteData/InsertData（會把使用者改過的
    /// 設定重置回種子值），已手工移除——AddColumn 的 defaultValue 即完成資料遷移。
    /// </summary>
    public partial class BAS_DocNumberConfig_PlantScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs");

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "DocNumberConfigs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs",
                columns: new[] { "DocType", "PlantId" });

            // 既有序號併入預設 scope（""）：各 (DocType, Period) 取跨廠最大序號，
            // 避免共用序號從 1 重新起跳而重發已用過的單號；舊 per-plant 列移除。
            migrationBuilder.Sql("""
                INSERT INTO "DocNumberSequences" ("DocType", "PlantId", "Period", "LastSeq")
                SELECT "DocType", '', "Period", MAX("LastSeq")
                FROM "DocNumberSequences"
                WHERE "PlantId" <> ''
                GROUP BY "DocType", "Period"
                ON CONFLICT ("DocType", "PlantId", "Period")
                DO UPDATE SET "LastSeq" = GREATEST("DocNumberSequences"."LastSeq", EXCLUDED."LastSeq");

                DELETE FROM "DocNumberSequences" WHERE "PlantId" <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 僅還原鍵結構；序號 scope 併入為單向資料遷移（per-plant 原始分佈不可逆），
            // 還原後全部序號歸於 "" scope，舊版 NextAsync 以呼叫端 plantId 為鍵會另起新列。
            migrationBuilder.Sql("""
                DELETE FROM "DocNumberConfigs" WHERE "PlantId" <> '';
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs");

            migrationBuilder.DropColumn(
                name: "PlantId",
                table: "DocNumberConfigs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DocNumberConfigs",
                table: "DocNumberConfigs",
                column: "DocType");
        }
    }
}
