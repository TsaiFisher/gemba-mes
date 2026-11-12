using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFactory.MES.Migrations
{
    /// <inheritdoc />
    public partial class MFG_DispatchHoldLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HoldAt",
                table: "Dispatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldFromStatus",
                table: "Dispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldReason",
                table: "Dispatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsMachineLocked",
                table: "Dispatches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // ── 既有資料修正（一次性）──────────────────────────────────────────────
            // 1. 工單已完工／結案／作廢，底下卻還有未結束的派工：舊規則下暫停算「完成」，
            //    工單會帶著暫停派工完工；作廢也不處理派工。這些派工留在派工清單當待辦，
            //    準備中／準備完成／暫停者還被排程當凍結佔用、一直佔著機台。
            //    有產出→完工（Closed，量維持原樣）；沒有產出→取消並清掉時段。
            //    狀態值：Dispatch Closed=5、Cancelled=8；WorkOrder Completed=3、Closed=4、Void=5。
            migrationBuilder.Sql(@"
UPDATE ""Dispatches"" d
   SET ""Status"" = CASE WHEN d.""QtyDone"" > 0 THEN 5 ELSE 8 END,
       ""ScheduledStart"" = CASE WHEN d.""QtyDone"" > 0 THEN d.""ScheduledStart"" ELSE NULL END,
       ""ScheduledEnd""   = CASE WHEN d.""QtyDone"" > 0 THEN d.""ScheduledEnd""   ELSE NULL END,
       ""UpdatedAt"" = now()
  FROM ""WorkOrders"" w
 WHERE w.""WorkOrderId"" = d.""WorkOrderId""
   AND w.""Status"" IN (3, 4, 5)
   AND d.""Status"" NOT IN (5, 8);");

            // 2. 仍在進行中的暫停派工沒有記錄原因。所在機台目前停用的，推定為機台停用造成——
            //    機台恢復可用時才會自動解除；其餘維持未記錄，由派工作業「暫停處理」手動恢復。
            //    OnHold=6、HoldReason.MachineDown=1；機台可用＝Available(0)／Standby(1)。
            migrationBuilder.Sql(@"
UPDATE ""Dispatches"" d
   SET ""HoldReason"" = 1
  FROM ""WorkCenterMachines"" m
  JOIN ""WorkCenters"" c ON c.""WorkCenterCode"" = m.""WorkCenterCode""
 WHERE d.""Status"" = 6
   AND d.""HoldReason"" IS NULL
   AND m.""MachineCode"" = d.""MachineCode""
   AND c.""PlantId"" = d.""PlantId""
   AND m.""MachineStatus"" NOT IN (0, 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoldAt",
                table: "Dispatches");

            migrationBuilder.DropColumn(
                name: "HoldFromStatus",
                table: "Dispatches");

            migrationBuilder.DropColumn(
                name: "HoldReason",
                table: "Dispatches");

            migrationBuilder.DropColumn(
                name: "IsMachineLocked",
                table: "Dispatches");
        }
    }
}
