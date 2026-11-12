using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.ViewModels
{
    public class ScheduleQueryRowVm
    {
        public string          DispatchId        { get; init; } = "";
        public string          WorkOrderId       { get; init; } = "";
        public string          ProductionBatchId { get; init; } = "";
        public string          ProductCode       { get; init; } = "";
        public string?         UomCode           { get; init; }
        public int             OperationSeq      { get; init; }
        public string          OperationName     { get; init; } = "";
        public string          WorkCenterId      { get; init; } = "";
        public string?         MachineCode       { get; init; }
        public DateTime?       ScheduledStart    { get; init; }
        public DateTime?       ScheduledEnd      { get; init; }
        public DispatchStatus  Status            { get; init; }
        public bool            MaterialRisk      { get; init; }
        public string?         ToolingCode       { get; init; }
        public bool            IsPredictedLate   { get; init; }
        public string?         SalesOrderId      { get; init; }
        public decimal         Quantity          { get; init; }
        public decimal         QtyDone           { get; init; }
    }
}
