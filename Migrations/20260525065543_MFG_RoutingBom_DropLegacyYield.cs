using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 移除 RoutingBoms 殘留的舊欄位 Yield。
    ///
    /// 背景：與 20260525064453_MFG_Routing_FixMissingColumns 同源的 snapshot drift 反面。
    /// 模型早已將 Yield 改為 ScrapRate（snapshot/model 均無 Yield），但建表的
    /// 20260515014440_MFG_PhaseA 建了 Yield numeric(10,6) NOT NULL；原本應 DropColumn 的
    /// 20260515031306_MFG_WorkCalendar_Routing_Ext 在 snapshot drift 清理時 Up() 被整段清空，
    /// 導致 DB 殘留孤兒欄位 Yield NOT NULL。EF INSERT 不提供 Yield →
    /// 執行期 PostgresException 23502: null value in column "Yield" ... violates not-null（編輯/新增途程存 BOM 時）。
    ///
    /// snapshot 早已無 Yield（領先實際 DB），故 `dotnet ef migrations add` 會產生空 Up()；
    /// 因此本 migration 以冪等 SQL 手寫移除，可安全重跑。
    /// </summary>
    public partial class MFG_RoutingBom_DropLegacyYield : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""RoutingBoms"" DROP COLUMN IF EXISTS ""Yield"";
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 還原：以 100% 良率（1.0）回填，避免既有資料列違反 NOT NULL。
            migrationBuilder.Sql(@"
ALTER TABLE ""RoutingBoms"" ADD COLUMN IF NOT EXISTS ""Yield"" numeric(10,6) NOT NULL DEFAULT 1;
ALTER TABLE ""RoutingBoms"" ALTER COLUMN ""Yield"" DROP DEFAULT;
");
        }
    }
}
