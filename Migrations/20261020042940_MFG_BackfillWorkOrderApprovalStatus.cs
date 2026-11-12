using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <summary>
    /// 純資料回填（無 DDL）：已投產／生產中但核准狀態仍為草稿的工單 → 已核准。
    ///
    /// 這種組合只會出自 §18.2 工單簽核上線前投產的舊工單——現行投產一律寫入已核准或送審中，
    /// 撤回／駁回會退回已規劃。回填後「已投產＋草稿」不再有歧義（2026-10-20 A2）：
    /// 開工閘門對草稿仍放行（WorkOrderApprovalGate），本回填讓資料本身也說得清楚。
    /// Down 不還原：回填前的草稿與「本來就該是已核准」無法區分。
    /// 0=Draft、2=Approved（DocumentApprovalStatus）；1=Released、2=InProgress（WorkOrderStatus）。
    /// </summary>
    public partial class MFG_BackfillWorkOrderApprovalStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"WorkOrders\" SET \"ApprovalStatus\" = 2 WHERE \"ApprovalStatus\" = 0 AND \"Status\" IN (1, 2);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
