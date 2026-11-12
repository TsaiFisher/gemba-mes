using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 會計期間改為依廠別分開（manual-test-plan §12.4 #2／#3）。
    /// 主鍵由 (PeriodId) 變成 (PeriodId, PlantId)。
    ///
    /// <b>資料搬遷是這支 migration 的重點</b>：EF 自動產生的版本只會把新欄位填成空字串，
    /// 於是既有期間全部落在一個不存在的廠別底下——每個廠都查不到自己的期間，
    /// 而 <c>AssertCanPostAsync</c> 對「查無期間」是**寬鬆放行**的，
    /// 結果會是「期間鎖整個失效而且沒有任何錯誤訊息」。
    /// 因此這裡把每一筆既有期間展開成「每個廠一份」，狀態原樣複製
    /// （已關帳的期間在每個廠都維持已關帳，不會因為分廠而被偷偷打開）。
    /// </summary>
    public partial class ACT_AccountingPeriodPerPlant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingPeriods",
                table: "AccountingPeriods");

            migrationBuilder.AddColumn<string>(
                name: "PlantId",
                table: "AccountingPeriods",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingPeriods",
                table: "AccountingPeriods",
                columns: new[] { "PeriodId", "PlantId" });

            // 既有的全域期間（PlantId 被填成 ''）展開成每廠一份。
            // 先插入再刪除，因為新主鍵允許 (期, '') 與 (期, 'ADM') 同時存在。
            migrationBuilder.Sql(@"
                INSERT INTO ""AccountingPeriods""
                    (""PeriodId"", ""PlantId"", ""Year"", ""Month"", ""StartDate"", ""EndDate"",
                     ""Status"", ""ClosedAt"", ""ClosedBy"", ""CreatedAt"", ""UpdatedAt"", ""Remarks"")
                SELECT p.""PeriodId"", pl.""Id"", p.""Year"", p.""Month"", p.""StartDate"", p.""EndDate"",
                       p.""Status"", p.""ClosedAt"", p.""ClosedBy"", p.""CreatedAt"", p.""UpdatedAt"", p.""Remarks""
                  FROM ""AccountingPeriods"" p
                 CROSS JOIN ""Plants"" pl
                 WHERE p.""PlantId"" = '';
            ");

            // 只有在真的展開過（廠別主檔非空）時才清掉原本那批，
            // 否則等於把所有期間刪光——那會讓期間鎖靜默失效。
            migrationBuilder.Sql(@"
                DELETE FROM ""AccountingPeriods""
                 WHERE ""PlantId"" = ''
                   AND EXISTS (SELECT 1 FROM ""Plants"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回頭路必然有損：多廠的期間要塌回單列，只能留一份。
            // 取「狀態最嚴格」的那一份（Closed > Closing > Reopened > Open 以 Status 值大者為準），
            // 免得把某個廠已經關掉的期間降級成可過帳。
            migrationBuilder.Sql(@"
                DELETE FROM ""AccountingPeriods"" a
                 WHERE EXISTS (
                     SELECT 1 FROM ""AccountingPeriods"" b
                      WHERE b.""PeriodId"" = a.""PeriodId""
                        AND (b.""Status"" > a.""Status""
                          OR (b.""Status"" = a.""Status"" AND b.""PlantId"" < a.""PlantId""))
                 );
            ");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AccountingPeriods",
                table: "AccountingPeriods");

            migrationBuilder.DropColumn(
                name: "PlantId",
                table: "AccountingPeriods");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AccountingPeriods",
                table: "AccountingPeriods",
                column: "PeriodId");
        }
    }
}
