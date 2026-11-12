using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Npgsql;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Filters
{
    /// <summary>
    /// 全站安全網：把 PostgreSQL 唯一鍵衝突（23505，含 PK / Unique Index）從原始
    /// DbUpdateException 轉譯成使用者看得懂的訊息，取代整頁堆疊。
    /// 各畫面仍應自行先做重複檢查（欄位級錯誤 UX 較佳）；本 Filter 只接住
    /// 漏網之處與「先檢查、後插入」間隙的並發競態。
    /// - AJAX（側欄面板）：回 { success:false, message }，submitPanel/postAction 既有流程會 alert
    /// - 非 AJAX：回 409，由 UseStatusCodePagesWithReExecute 導至友善錯誤頁
    /// - 例外：撞的是手機去重表 SyncLog 的主鍵 → 不是錯誤，回第一個請求的結果（見 <see cref="TryReplayDuplicateAsync"/>）
    /// </summary>
    public class DbUniqueViolationExceptionFilter : IAsyncExceptionFilter
    {
        /// <summary>手機／倉庫 API 去重表的主鍵（clientId）。</summary>
        internal const string SyncLogPrimaryKey = "PK_SyncLogs";

        private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly ILogger<DbUniqueViolationExceptionFilter> _logger;

        public DbUniqueViolationExceptionFilter(
            IStringLocalizer<ProjectZSharedResource> localizer,
            ILogger<DbUniqueViolationExceptionFilter> logger)
        {
            _localizer = localizer;
            _logger = logger;
        }

        public async Task OnExceptionAsync(ExceptionContext context)
        {
            // 單據類型查無編號規則：友善提示引導至設定畫面，取代 500（絕不自動代編臨時號）
            if (context.Exception is DocNumberConfigNotFoundException docEx)
            {
                _logger.LogWarning(
                    "單據類型 {DocType} 查無編號規則，已轉譯為友善訊息 path={Path}",
                    docEx.DocType, context.HttpContext.Request.Path);

                var docMsg = string.Format(_localizer["DocNumberConfigMissing"].Value, docEx.DocType);
                context.Result = context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
                    ? new JsonResult(new { success = false, message = docMsg })
                    : new StatusCodeResult(StatusCodes.Status409Conflict);
                context.ExceptionHandled = true;
                return;
            }

            if (context.Exception is not DbUpdateException dbEx) return;
            if (FindPostgresException(dbEx) is not { SqlState: PostgresErrorCodes.UniqueViolation } pgEx) return;

            if (pgEx.ConstraintName == SyncLogPrimaryKey && await TryReplayDuplicateAsync(context, dbEx)) return;

            _logger.LogWarning(
                "唯一鍵衝突已轉譯為友善訊息：constraint={Constraint} path={Path}",
                pgEx.ConstraintName, context.HttpContext.Request.Path);

            var message = _localizer["DbUniqueViolation"].Value;
            var isAjax = context.HttpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest";

            context.Result = isAjax
                ? new JsonResult(new { success = false, message })
                : new StatusCodeResult(StatusCodes.Status409Conflict);
            context.ExceptionHandled = true;
        }

        /// <summary>
        /// 同一個 clientId 的兩個請求同時在處理：兩邊都在對方存檔前查過 SyncLog（沒有），都往下做，
        /// 後存的這一個撞主鍵。它的業務變更與 SyncLog 在同一次存檔，已經一起不存在；這筆動作由
        /// 先存的那個請求做完了。所以這不是失敗——回那個請求的結果，與事後重送拿到的一樣。
        ///
        /// <para>原本落到下面的通用處理：手機請求沒有 X-Requested-With，回的是 409。離線佇列把
        /// 非並發衝突的 409 標成「失敗、需人工處理」，直接送出的畫面顯示錯誤——人看到失敗就重報一次
        /// （新的 clientId），那才真的重複了。現場會走到這裡：回應在路上斷掉、手機轉排隊後立刻重送，
        /// 而第一個請求還在伺服器上跑；或同一支手機兩個分頁的佇列同時補送。</para>
        ///
        /// <para>另開一個 scope 讀：這個請求的交易可能已因錯誤被中止，原本的連線上任何查詢都會失敗。
        /// 撞主鍵代表另一邊已提交（PostgreSQL 會等對方交易結束才判定），所以一定讀得到。</para>
        /// </summary>
        private async Task<bool> TryReplayDuplicateAsync(ExceptionContext context, DbUpdateException dbEx)
        {
            var clientId = dbEx.Entries.Select(e => e.Entity).OfType<SyncLog>().FirstOrDefault()?.ClientId;
            if (string.IsNullOrEmpty(clientId)) return false;

            using var scope = context.HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var log = await db.SyncLogs.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId);
            if (log == null) return false;

            // 快照的屬性大小寫依各端點的序列化設定而不同（MFG 未指定、其他用 Web 預設），一律不分大小寫讀；
            // 還沒有快照（先存的那個請求快照是第二次存檔才補上）就回「已處理」，形狀與各端點的去重重放一致。
            MobileOkResult? replay = null;
            if (log.ResponseSnapshot != null)
            {
                try { replay = JsonSerializer.Deserialize<MobileOkResult>(log.ResponseSnapshot, SnapshotJson); }
                catch (JsonException) { /* 快照壞了就回通用的已處理 */ }
            }
            replay ??= new MobileOkResult(Message: PzText.Get("Mob_AlreadyProcessed"));

            _logger.LogInformation(
                "同一 clientId 的請求同時處理，後到者回先到者的結果：clientId={ClientId} path={Path}",
                clientId, context.HttpContext.Request.Path);
            context.Result = new OkObjectResult(replay);
            context.ExceptionHandled = true;
            return true;
        }

        private static PostgresException? FindPostgresException(Exception ex)
        {
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                if (inner is PostgresException pg) return pg;
            return null;
        }
    }
}
