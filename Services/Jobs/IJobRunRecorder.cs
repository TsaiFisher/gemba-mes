using SmartFactory.MES.Models.Jobs;

namespace SmartFactory.MES.Services.Jobs
{
    /// <summary>
    /// 每日排程執行紀錄的存取。獨立成服務而不是讓基底直接碰 DbContext，
    /// 是為了讓 <see cref="DailyJobServiceBase"/> 的測試能在不起資料庫的情況下
    /// 驗證去重行為——那是這整張表存在的理由，測不到等於沒做。
    /// </summary>
    public interface IJobRunRecorder
    {
        /// <summary>指定排程在指定的當地日期是否已經<b>成功</b>執行過。</summary>
        Task<bool> HasSucceededOnAsync(string jobName, DateOnly jobDate, CancellationToken ct = default);

        /// <summary>寫入一列執行紀錄。</summary>
        Task RecordAsync(ScheduledJobRun run, CancellationToken ct = default);
    }
}
