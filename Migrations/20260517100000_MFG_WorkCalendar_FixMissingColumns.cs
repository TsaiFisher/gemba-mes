using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 修補 WorkCalendars 缺漏欄位。
    ///
    /// 背景：WorkCalendar 模型有 ShiftGroupCode / WorkStart / WorkEnd，但
    /// 建表的 20260515014440_MFG_PhaseA 未含這三欄；原本應補上的
    /// 20260515031306_MFG_WorkCalendar_Routing_Ext 在 snapshot drift 清理時
    /// Up() 被整段清空，導致實際 DB 永遠缺這三欄 →
    /// 執行期 PostgresException 42703: column w.ShiftGroupCode does not exist。
    ///
    /// snapshot 早已含這三欄（領先實際 DB），故 `dotnet ef migrations add`
    /// 會產生空 Up()；因此本 migration 以冪等 SQL 手寫補欄，可安全重跑。
    /// </summary>
    public partial class MFG_WorkCalendar_FixMissingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""WorkCalendars"" ADD COLUMN IF NOT EXISTS ""ShiftGroupCode"" character varying(20);
ALTER TABLE ""WorkCalendars"" ADD COLUMN IF NOT EXISTS ""WorkStart"" time without time zone;
ALTER TABLE ""WorkCalendars"" ADD COLUMN IF NOT EXISTS ""WorkEnd"" time without time zone;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""WorkCalendars"" DROP COLUMN IF EXISTS ""ShiftGroupCode"";
ALTER TABLE ""WorkCalendars"" DROP COLUMN IF EXISTS ""WorkStart"";
ALTER TABLE ""WorkCalendars"" DROP COLUMN IF EXISTS ""WorkEnd"";
");
        }
    }
}
