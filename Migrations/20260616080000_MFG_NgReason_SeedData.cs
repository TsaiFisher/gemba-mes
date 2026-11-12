using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_NgReason_SeedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "CodeEntries",
                columns: new[] { "Id", "Category", "Code", "NameZh", "NameEn", "Remark", "IsActive", "SortOrder", "ApprovalStatus", "ApprovalRequestId" },
                values: new object[,]
                {
                    { "ng-dim-001", "ng_reason", "DIM", "尺寸超差", "Dimension Out of Spec", null, true, 10, 2, null },
                    { "ng-app-002", "ng_reason", "APP", "外觀不良", "Appearance Defect",      null, true, 20, 2, null },
                    { "ng-fun-003", "ng_reason", "FUN", "功能異常", "Functional Failure",      null, true, 30, 2, null },
                    { "ng-mat-004", "ng_reason", "MAT", "材質不良", "Material Defect",         null, true, 40, 2, null },
                    { "ng-asy-005", "ng_reason", "ASY", "裝配不良", "Assembly Error",          null, true, 50, 2, null },
                    { "ng-oth-099", "ng_reason", "OTH", "其他",     "Other",                   null, true, 90, 2, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM \"CodeEntries\" WHERE \"Id\" IN ('ng-dim-001','ng-app-002','ng-fun-003','ng-mat-004','ng-asy-005','ng-oth-099');");
        }
    }
}
