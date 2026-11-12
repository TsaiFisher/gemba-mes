using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class FixDbSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApprovalSteps_ApprovalTemplates_TemplateId",
                table: "ApprovalSteps");

            migrationBuilder.DropIndex(
                name: "IX_MenuPermissions_RoleId",
                table: "MenuPermissions");

            // IF NOT EXISTS 確保冪等（可能與 AddDataAuthToApprovalModels 重疊）
            migrationBuilder.Sql(@"ALTER TABLE ""WorkOrders"" ADD COLUMN IF NOT EXISTS ""UpdatedAt"" timestamp with time zone NOT NULL DEFAULT TIMESTAMPTZ '-infinity';");

            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalTemplates"" ADD COLUMN IF NOT EXISTS ""CreatedByRoleId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalTemplates"" ADD COLUMN IF NOT EXISTS ""OrganizationUnitPath"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalTemplates"" ADD COLUMN IF NOT EXISTS ""PlantId"" text;");

            migrationBuilder.AlterColumn<string>(
                name: "TemplateId",
                table: "ApprovalSteps",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalRequests"" ADD COLUMN IF NOT EXISTS ""CreatedByRoleId"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalRequests"" ADD COLUMN IF NOT EXISTS ""OrganizationUnitPath"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalRequests"" ADD COLUMN IF NOT EXISTS ""PlantId"" text;");

            migrationBuilder.Sql(@"ALTER TABLE ""ApprovalLogs"" ADD COLUMN IF NOT EXISTS ""PlantId"" text;");

            migrationBuilder.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MenuPermissions_RoleId_MenuKey"" ON ""MenuPermissions"" (""RoleId"", ""MenuKey"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalRequests_CurrentStepId"" ON ""ApprovalRequests"" (""CurrentStepId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalRequests_RequesterId"" ON ""ApprovalRequests"" (""RequesterId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalLogs_ApproverId"" ON ""ApprovalLogs"" (""ApproverId"");");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_ApprovalLogs_RequestId"" ON ""ApprovalLogs"" (""RequestId"");");

            // AddForeignKey 亦需要冪等
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalLogs_ApprovalRequests_RequestId') THEN
                        ALTER TABLE ""ApprovalLogs"" ADD CONSTRAINT ""FK_ApprovalLogs_ApprovalRequests_RequestId""
                            FOREIGN KEY (""RequestId"") REFERENCES ""ApprovalRequests""(""Id"") ON DELETE CASCADE;
                    END IF;
                END $$;");

            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalLogs_AspNetUsers_ApproverId') THEN
                        ALTER TABLE ""ApprovalLogs"" ADD CONSTRAINT ""FK_ApprovalLogs_AspNetUsers_ApproverId""
                            FOREIGN KEY (""ApproverId"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE RESTRICT;
                    END IF;
                END $$;");

            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalRequests_ApprovalSteps_CurrentStepId') THEN
                        ALTER TABLE ""ApprovalRequests"" ADD CONSTRAINT ""FK_ApprovalRequests_ApprovalSteps_CurrentStepId""
                            FOREIGN KEY (""CurrentStepId"") REFERENCES ""ApprovalSteps""(""Id"") ON DELETE RESTRICT;
                    END IF;
                END $$;");

            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalRequests_AspNetUsers_RequesterId') THEN
                        ALTER TABLE ""ApprovalRequests"" ADD CONSTRAINT ""FK_ApprovalRequests_AspNetUsers_RequesterId""
                            FOREIGN KEY (""RequesterId"") REFERENCES ""AspNetUsers""(""Id"") ON DELETE SET NULL;
                    END IF;
                END $$;");

            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ApprovalSteps_ApprovalTemplates_TemplateId') THEN
                        ALTER TABLE ""ApprovalSteps"" ADD CONSTRAINT ""FK_ApprovalSteps_ApprovalTemplates_TemplateId""
                            FOREIGN KEY (""TemplateId"") REFERENCES ""ApprovalTemplates""(""Id"");
                    END IF;
                END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApprovalLogs_ApprovalRequests_RequestId",
                table: "ApprovalLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_ApprovalLogs_AspNetUsers_ApproverId",
                table: "ApprovalLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_ApprovalRequests_ApprovalSteps_CurrentStepId",
                table: "ApprovalRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ApprovalRequests_AspNetUsers_RequesterId",
                table: "ApprovalRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ApprovalSteps_ApprovalTemplates_TemplateId",
                table: "ApprovalSteps");

            migrationBuilder.DropIndex(
                name: "IX_MenuPermissions_RoleId_MenuKey",
                table: "MenuPermissions");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRequests_CurrentStepId",
                table: "ApprovalRequests");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalRequests_RequesterId",
                table: "ApprovalRequests");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalLogs_ApproverId",
                table: "ApprovalLogs");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalLogs_RequestId",
                table: "ApprovalLogs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "ApprovalTemplates");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "ApprovalTemplates");

            migrationBuilder.DropColumn(
                name: "PlantId",
                table: "ApprovalTemplates");

            migrationBuilder.DropColumn(
                name: "CreatedByRoleId",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitPath",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "PlantId",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "PlantId",
                table: "ApprovalLogs");

            migrationBuilder.AlterColumn<string>(
                name: "TemplateId",
                table: "ApprovalSteps",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuPermissions_RoleId",
                table: "MenuPermissions",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApprovalSteps_ApprovalTemplates_TemplateId",
                table: "ApprovalSteps",
                column: "TemplateId",
                principalTable: "ApprovalTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
