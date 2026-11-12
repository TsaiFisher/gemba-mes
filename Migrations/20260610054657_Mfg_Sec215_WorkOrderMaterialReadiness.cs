using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class Mfg_Sec215_WorkOrderMaterialReadiness : Migration
    {
        // §21.5：WorkOrder 物料備齊狀態欄位。
        // 註：EF 因 snapshot 漂移另產生多筆既存 xmin 影子欄的 Add/Drop（rowVersion=xid），
        //     那些欄位資料庫已存在，屬虛假差異，已手動修剪，僅保留本欄位。
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaterialReadinessStatus",
                table: "WorkOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaterialReadinessStatus",
                table: "WorkOrders");
        }
    }
}
