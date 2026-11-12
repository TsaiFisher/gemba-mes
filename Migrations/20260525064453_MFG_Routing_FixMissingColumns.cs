using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 修補 RoutingOperations / RoutingBoms 缺漏欄位。
    ///
    /// 背景：與 20260517100000_MFG_WorkCalendar_FixMissingColumns 同源的 snapshot drift。
    /// RoutingOperation 模型有 SopNumber / UseContainerIn / UseContainerOut，RoutingBom 有
    /// ScrapRate，但建表的 20260515014440_MFG_PhaseA 未含這些欄位；原本應補上的
    /// 20260515031306_MFG_WorkCalendar_Routing_Ext 在 snapshot drift 清理時 Up() 被整段
    /// 清空，導致實際 DB 永遠缺這些欄位 →
    /// 執行期 PostgresException 42703: column r.SopNumber does not exist（新增/編輯途程時）。
    ///
    /// snapshot 早已含這些欄位（領先實際 DB），故 `dotnet ef migrations add` 會產生空 Up()；
    /// 因此本 migration 以冪等 SQL 手寫補欄，可安全重跑。先 ADD ... DEFAULT 回填既有資料列，
    /// 再 DROP DEFAULT 對齊 EF 模型（模型未定義 DB 預設值），使乾淨 DB 與既有 DB 結構一致。
    /// </summary>
    public partial class MFG_Routing_FixMissingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""RoutingOperations"" ADD COLUMN IF NOT EXISTS ""SopNumber"" character varying(50);
ALTER TABLE ""RoutingOperations"" ADD COLUMN IF NOT EXISTS ""UseContainerIn"" boolean NOT NULL DEFAULT false;
ALTER TABLE ""RoutingOperations"" ADD COLUMN IF NOT EXISTS ""UseContainerOut"" boolean NOT NULL DEFAULT false;
ALTER TABLE ""RoutingOperations"" ALTER COLUMN ""UseContainerIn"" DROP DEFAULT;
ALTER TABLE ""RoutingOperations"" ALTER COLUMN ""UseContainerOut"" DROP DEFAULT;

ALTER TABLE ""RoutingBoms"" ADD COLUMN IF NOT EXISTS ""ScrapRate"" numeric(8,4) NOT NULL DEFAULT 0;
ALTER TABLE ""RoutingBoms"" ALTER COLUMN ""ScrapRate"" DROP DEFAULT;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""RoutingOperations"" DROP COLUMN IF EXISTS ""SopNumber"";
ALTER TABLE ""RoutingOperations"" DROP COLUMN IF EXISTS ""UseContainerIn"";
ALTER TABLE ""RoutingOperations"" DROP COLUMN IF EXISTS ""UseContainerOut"";

ALTER TABLE ""RoutingBoms"" DROP COLUMN IF EXISTS ""ScrapRate"";
");
        }
    }
}
