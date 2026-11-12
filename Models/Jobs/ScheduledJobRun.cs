using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Models.Jobs
{
    /// <summary>
    /// 每日排程的執行紀錄。<see cref="Services.Jobs.DailyJobServiceBase"/> 的衍生類別
    /// 只要覆寫 <c>JobScopeFactory</c> 就會自動落地一列，並據此去重。
    ///
    /// <para><b>為什麼需要這張表</b>：啟動補跑（PR #113）讓排程在非 24 小時開機的機器上
    /// 也跑得到，代價是<b>每次應用程式啟動都會再跑一次</b>。對冪等的清理工作無所謂，
    /// 但對兩類工作是真的問題：很貴的（ETA 全量校準逐廠重算所有工單）、以及<b>會對外
    /// 送東西的</b>（生產準備提醒會推播給全廠負責角色）。IIS 集區回收、改組態、部署
    /// 都會觸發啟動，開發機更是一天好幾次。有了「今天跑過了沒」的紀錄，補跑才能免費。</para>
    ///
    /// <para><b>刻意不實作 IPlantEntity</b>：這裡記的是「某支排程在某天跑過了沒」，
    /// 是系統層級的作業紀錄，不是業務單據。排程本身也是全系統性的——生產準備提醒雖然
    /// 逐廠發送，但那是<b>一次</b>執行內的迴圈，不是每廠一次執行。加上廠別會讓去重
    /// 變成「每廠各跑一次」，而全域查詢過濾還會讓背景服務（沒有 CurrentPlantId）讀不到
    /// 自己寫的紀錄——那正是最難查的那種無聲失效。</para>
    ///
    /// <para><b>容量</b>：一天最多十幾列（每支排程一列），一年約五千列，不需要保留期清理。</para>
    ///
    /// <para>⚠️ 這張表<b>不是</b>分散式鎖。單一進程的背景服務不會自己跟自己搶，但若日後
    /// 同一個資料庫接上兩個應用程式執行個體，兩邊會各自判定「今天還沒跑」而同時執行。
    /// 屆時要的是資料庫層級的租約，不是在這張表上加唯一索引（加了會讓失敗後的重試寫不進來）。</para>
    /// </summary>
    public class ScheduledJobRun
    {
        /// <summary>主鍵。命名對齊既有的 DataAuditRun / ReportRun。</summary>
        [Key]
        public int RunId { get; set; }

        /// <summary>排程名稱，取自 <c>DailyJobServiceBase.JobName</c>。</summary>
        [MaxLength(100)]
        public string JobName { get; set; } = string.Empty;

        /// <summary>
        /// 這次執行算在哪一天——<b>排程時區（Jobs:TimeZone）的當地日期</b>，不是 UTC 日期。
        /// 去重比對的就是這一欄。直接用 UTC 日期，在 +08 會把當地凌晨 02:00 的執行算成前一天，
        /// 於是每天早上都判定「昨天跑的、今天還沒跑」而重複執行。
        /// </summary>
        public DateOnly JobDate { get; set; }

        /// <summary>觸發方式：「啟動補跑」或「每日排程」。</summary>
        [MaxLength(20)]
        public string Trigger { get; set; } = string.Empty;

        /// <summary>開始時間（UTC）。</summary>
        public DateTime StartedAtUtc { get; set; }

        /// <summary>結束時間（UTC）。</summary>
        public DateTime CompletedAtUtc { get; set; }

        /// <summary>
        /// 是否成功。<b>去重只看成功的紀錄</b>：失敗的那次不該擋住當天稍後的重試
        /// （例如 02:00 因資料庫短暫斷線失敗，10:00 重啟應用程式時應該要補得回來）。
        /// </summary>
        public bool Succeeded { get; set; }

        /// <summary>失敗時的例外訊息；成功為 null。只存訊息不存堆疊，完整內容在日誌裡。</summary>
        [MaxLength(1000)]
        public string? Error { get; set; }
    }
}
