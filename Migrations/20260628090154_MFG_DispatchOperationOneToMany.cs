using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_DispatchOperationOneToMany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dispatches_WorkOrderId_OperationSeq",
                table: "Dispatches");

            migrationBuilder.CreateIndex(
                name: "IX_Dispatches_WorkOrderId_OperationSeq",
                table: "Dispatches",
                columns: new[] { "WorkOrderId", "OperationSeq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dispatches_WorkOrderId_OperationSeq",
                table: "Dispatches");

            migrationBuilder.CreateIndex(
                name: "IX_Dispatches_WorkOrderId_OperationSeq",
                table: "Dispatches",
                columns: new[] { "WorkOrderId", "OperationSeq" },
                unique: true);
        }
    }
}
