namespace SmartFactory.MES.Modules.MFG.Models.Analytics
{
    /// <summary>「產線即時看板」（一鍵看整條線在忙什麼）畫面資料。純即時快照，不快取。</summary>
    public class LineStatusDashboardVm
    {
        public List<LineStatusCardVm> Lines { get; set; } = new();
    }

    public class LineStatusCardVm
    {
        public string WorkCenterCode { get; set; } = string.Empty;
        public string WorkCenterName { get; set; } = string.Empty;

        /// <summary>Producing / SettingUp / Idle</summary>
        public string LineState { get; set; } = "Idle";

        public List<LineActiveDispatchVm> ActiveDispatches { get; set; } = new();
    }

    public class LineActiveDispatchVm
    {
        /// <summary>
        /// 線別代號（Dispatch.MachineCode）。生產線工作中心登錄多條線別時，派工落在哪條線；
        /// 未登錄線別（整條線單一資源）時為空字串。
        /// </summary>
        public string LineCode { get; set; } = string.Empty;

        public string DispatchId { get; set; } = string.Empty;
        public string WorkOrderId { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal QtyDone { get; set; }

        /// <summary>正在生產（ProductionSession Open）的人員。</summary>
        public List<LineOperatorVm> ActiveOperators { get; set; } = new();

        /// <summary>正在備料（ProductionSetup Open）的人員。</summary>
        public List<LineOperatorVm> ActiveSetupOperators { get; set; } = new();
    }

    public class LineOperatorVm
    {
        public string OperatorName { get; set; } = string.Empty;
        public DateTime StartAt { get; set; }
    }
}
