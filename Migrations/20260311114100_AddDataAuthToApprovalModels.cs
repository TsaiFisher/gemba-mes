using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class AddDataAuthToApprovalModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 使用 IF NOT EXISTS 確保冪等性（避免因 schema drift 導致重複執行失敗）

            // ── ApprovalTemplates ────────────────────────────────────────────────
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalTemplates"" ADD COLUMN IF NOT EXISTS ""PlantId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalTemplates"" ADD COLUMN IF NOT EXISTS ""CreatedByRoleId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalTemplates"" ADD COLUMN IF NOT EXISTS ""OrganizationUnitPath"" text;");

            // ── ApprovalRequests ─────────────────────────────────────────────────
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalRequests"" ADD COLUMN IF NOT EXISTS ""PlantId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalRequests"" ADD COLUMN IF NOT EXISTS ""CreatedByRoleId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalRequests"" ADD COLUMN IF NOT EXISTS ""OrganizationUnitPath"" text;");

            // ── ApprovalLogs ─────────────────────────────────────────────────────
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalLogs"" ADD COLUMN IF NOT EXISTS ""PlantId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalLogs"" ADD COLUMN IF NOT EXISTS ""CreatedByRoleId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalLogs"" ADD COLUMN IF NOT EXISTS ""OrganizationUnitPath"" text;");

            // ── 索引：資料權限查詢效能優化 ────────────────────────────────────────
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_WorkOrders_CreatedByRoleId"" ON ""WorkOrders"" (""CreatedByRoleId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_WorkOrders_OrganizationUnitPath"" ON ""WorkOrders"" (""OrganizationUnitPath"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalTemplates_CreatedByRoleId"" ON ""ApprovalTemplates"" (""CreatedByRoleId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalTemplates_OrganizationUnitPath"" ON ""ApprovalTemplates"" (""OrganizationUnitPath"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalRequests_CreatedByRoleId"" ON ""ApprovalRequests"" (""CreatedByRoleId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalRequests_OrganizationUnitPath"" ON ""ApprovalRequests"" (""OrganizationUnitPath"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalLogs_CreatedByRoleId"" ON ""ApprovalLogs"" (""CreatedByRoleId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalLogs_OrganizationUnitPath"" ON ""ApprovalLogs"" (""OrganizationUnitPath"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 移除索引
            migrationBuilder.DropIndex(name: "IX_ApprovalLogs_OrganizationUnitPath",    table: "ApprovalLogs");
            migrationBuilder.DropIndex(name: "IX_ApprovalLogs_CreatedByRoleId",          table: "ApprovalLogs");
            migrationBuilder.DropIndex(name: "IX_ApprovalRequests_OrganizationUnitPath", table: "ApprovalRequests");
            migrationBuilder.DropIndex(name: "IX_ApprovalRequests_CreatedByRoleId",      table: "ApprovalRequests");
            migrationBuilder.DropIndex(name: "IX_ApprovalTemplates_OrganizationUnitPath",table: "ApprovalTemplates");
            migrationBuilder.DropIndex(name: "IX_ApprovalTemplates_CreatedByRoleId",     table: "ApprovalTemplates");
            migrationBuilder.DropIndex(name: "IX_WorkOrders_OrganizationUnitPath",       table: "WorkOrders");
            migrationBuilder.DropIndex(name: "IX_WorkOrders_CreatedByRoleId",            table: "WorkOrders");

            // 移除欄位
            migrationBuilder.DropColumn(name: "PlantId",              table: "ApprovalLogs");
            migrationBuilder.DropColumn(name: "CreatedByRoleId",      table: "ApprovalLogs");
            migrationBuilder.DropColumn(name: "OrganizationUnitPath", table: "ApprovalLogs");

            migrationBuilder.DropColumn(name: "PlantId",              table: "ApprovalRequests");
            migrationBuilder.DropColumn(name: "CreatedByRoleId",      table: "ApprovalRequests");
            migrationBuilder.DropColumn(name: "OrganizationUnitPath", table: "ApprovalRequests");

            migrationBuilder.DropColumn(name: "PlantId",              table: "ApprovalTemplates");
            migrationBuilder.DropColumn(name: "CreatedByRoleId",      table: "ApprovalTemplates");
            migrationBuilder.DropColumn(name: "OrganizationUnitPath", table: "ApprovalTemplates");
        }
    }
}
