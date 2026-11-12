namespace SmartFactory.MES.Modules.MFG.Services
{
    public interface IWorkCalendarService
    {
        Task<decimal> GetWorkHoursAsync(string? machineCode, string? workCenterId, string? plantId, DateOnly date);

        /// <summary>
        /// 傳回指定廠別/工作中心/機台於指定日期的可用時段清單（StartMinute/EndMinute）。
        /// 結果已依優先級（機台 > 工作中心 > 廠別）篩選，供 WorkingTimeWalker 使用。
        /// </summary>
        Task<IReadOnlyList<(int Start, int End)>> GetSegmentsAsync(
            string? machineCode, string? workCenterId, string? plantId, DateOnly date);
    }
}
