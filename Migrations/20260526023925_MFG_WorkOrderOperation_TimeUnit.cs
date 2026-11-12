using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_WorkOrderOperation_TimeUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 既有列預設為 2 = OperationTimeUnit.Minute（避免 0 為無效列舉值）
            migrationBuilder.AddColumn<int>(
                name: "TimeUnit",
                table: "WorkOrderOperations",
                type: "integer",
                nullable: false,
                defaultValue: 2);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeUnit",
                table: "WorkOrderOperations");
        }
    }
}
