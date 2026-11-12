using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules._Shared
{
    /// <summary>
    /// 顧問模組控制器共用基底。把每個 *Controller 重複手抄的樣板（IsAjax、
    /// 權限 ViewBag、JSON 契約、驗證失敗回傳、列印 ViewBag）集中於此，
    /// 讓畫面「天生完整」，從源頭消除 Vibe Coding 遺漏。
    ///
    /// 採用 <see cref="HttpContext"/>.RequestServices 延遲解析服務，
    /// 子類別「不需」改建構式注入，導入成本＝把 ": Controller" 換成 ": ModuleControllerBase"。
    /// 僅供 ProjectZ 顧問模組（Modules/*）使用，不套用於 Core 控制器。
    /// </summary>
    public abstract class ModuleControllerBase : Controller
    {
        /// <summary>是否為 AJAX 請求（取代散落各控制器的同名私有屬性）。</summary>
        protected virtual bool IsAjax =>
            Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        private T Svc<T>() where T : notnull =>
            HttpContext.RequestServices.GetRequiredService<T>();

        protected IPermissionService Permissions => Svc<IPermissionService>();
        protected IUserContext UserContext => Svc<IUserContext>();
        protected ApplicationDbContext Db => Svc<ApplicationDbContext>();

        /// <summary>
        /// 使用者／廠別時區的時間換算。顯示用時間戳一律走這裡，不可讀伺服器時鐘——
        /// 開發與 CI 刻意跑 TZ=UTC（見 DEVELOPMENT.md），DateTime.Now 會直接差 8 小時。
        /// </summary>
        protected IDateTimeService DateTimeSvc => Svc<IDateTimeService>();

        /// <summary>以實際控制器型別為分類的 logger（背景失敗如自動過帳必須記錄，禁止空 catch）。</summary>
        protected Microsoft.Extensions.Logging.ILogger Logger =>
            Svc<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger(GetType());

        /// <summary>
        /// 一次塞入 Index 畫面所需的 CanCreate/Update/Delete/Print 權限旗標，
        /// 取代每個 Index action 手寫 4 行 ViewBag（漏一個就缺權限控制）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 本方法<b>不做任何守門</b>，只準備 ViewBag。讀取閘門必須由 action 自行寫：
        /// <code>if (!await Permissions.CanRead(MenuKey)) return Forbid();</code>
        /// 專案沒有全域權限 filter，少了那一行等於「選單隱藏但 URL 直達可看」。
        /// 2026-08-03 全面稽核時，30 支畫面的 Index 因誤以為本方法會擋人而缺少閘門。
        /// </remarks>
        protected virtual async Task PrepareIndexPermissionsAsync(string menuKey)
        {
            ViewBag.CanCreate = await Permissions.CanCreate(menuKey);
            ViewBag.CanUpdate = await Permissions.CanUpdate(menuKey);
            ViewBag.CanDelete = await Permissions.CanDelete(menuKey);
            ViewBag.CanPrint  = await Permissions.CanPrint(menuKey);
        }

        /// <summary>
        /// 單據明細（<c>Details</c>）的讀取閘門：<b>模組可讀</b> 或 <b>本人是這一張單的簽核關係人</b>。
        /// </summary>
        /// <remarks>
        /// 為什麼不是單純的 <c>CanRead(menuKey)</c>：
        /// <c>Views/Approval/_DocDetailsUrls.cshtml</c> 把「待我簽核」的單號點擊直接導向各模組的
        /// <c>Details</c>（25 條映射）。簽核者未必持有該模組的 MenuKey——內建 8 個角色的矩陣多半
        /// 兜得住，但**自訂簽核角色**一旦沒有該模組權限，補上 CanRead 就會讓點單號變成 403，
        /// 而前端只顯示「載入失敗」，看不出是權限問題（靜默壞掉）。
        ///
        /// 反過來也不能因此不擋：旁路是**單筆授權**，只放行「這一張單」的關係人，
        /// 不會讓他看到同模組的其他單據。關係人＝申請人／現任簽核人／
        /// 當前步驟以角色或個人指派到我／曾在本單簽過（讓簽完的人仍能回看）。
        ///
        /// <paramref name="documentType"/> 可省略：單號本身帶型別前綴（SO／PO／WO…），
        /// 但有 <c>DocType</c> 常數的控制器一律傳，避免跨型別同號的理論碰撞。
        /// </remarks>
        protected async Task<bool> CanReadDocumentAsync(
            string menuKey, string documentId, string? documentType = null)
        {
            if (await Permissions.CanRead(menuKey)) return true;
            if (string.IsNullOrEmpty(documentId)) return false;

            var userId = UserContext.UserId;
            if (string.IsNullOrEmpty(userId)) return false;
            var roleId = UserContext.ActiveRoleId;

            var requests = Db.ApprovalRequests.AsNoTracking()
                .Where(r => r.DocumentId == documentId);
            if (!string.IsNullOrEmpty(documentType))
                requests = requests.Where(r => r.DocumentType == documentType);

            // 申請人／現任簽核人／未指派但當前步驟指到我（語意對齊 ApprovalService.MyPendingQuery）
            var isParty = await requests
                .Join(Db.ApprovalSteps.AsNoTracking(), r => r.CurrentStepId, s => s.Id,
                      (r, s) => new { r, s })
                .AnyAsync(x => x.r.RequesterId == userId
                            || x.r.ActiveApproverId == userId
                            || (string.IsNullOrEmpty(x.r.ActiveApproverId)
                                && ((x.s.ApproverType == ApproverType.Role && x.s.TargetRoleId == roleId)
                                 || (x.s.ApproverType == ApproverType.User && x.s.TargetUserId == userId))));
            if (isParty) return true;

            // 曾簽過本單（已完成簽核者回頭查看）
            return await requests
                .Join(Db.ApprovalLogs.AsNoTracking(), r => r.Id, l => l.RequestId, (r, l) => l)
                .AnyAsync(l => l.ApproverId == userId || l.OnBehalfOfUserId == userId);
        }

        /// <summary>
        /// 統一的成功 JSON 契約：{ success = true, message, data }。
        /// 刻意不取名 Ok，避免遮蔽 <see cref="ControllerBase.Ok()"/> 改變既有多載解析。
        /// </summary>
        protected virtual JsonResult JsonOk(object? data = null, string? message = null) =>
            Json(new { success = true, message, data });

        /// <summary>統一的失敗 JSON 契約：{ success = false, message }。</summary>
        protected virtual JsonResult JsonFail(string message) =>
            Json(new { success = false, message });

        /// <summary>
        /// ModelState 無效時的統一回傳：AJAX 回傳 partial（沿用既有面板流程），
        /// 否則回傳完整 View。取代各控制器各寫一套 if(IsAjax) 分支。
        /// </summary>
        protected virtual IActionResult ValidationPartial(string formView, object model) =>
            IsAjax ? PartialView(formView, model) : View(formView, model);

        /// <summary>
        /// 統一產生列印 ViewBag：QR Code SVG、列印時間，並依使用者 ID 解析顯示名稱。
        /// 取代各 Print action 手抄 QrSvg/UserNames 區塊（重用 Core PrintHelper）。
        /// </summary>
        /// <summary>
        /// 同步簽核狀態的統一實作，取代 19 個 Controller 各自的同名私有方法。
        /// 查 ApprovalRequests → 映射 DocumentApprovalStatus → 若核准則呼叫 onApprovedAsync
        /// → 退稿/撤回時清除 ApprovalRequestId → SaveChanges。
        ///
        /// 自動過帳的原子性由本方法統一負責：onApprovedAsync 多為自動過帳，過帳路徑內含
        /// 日記帳自身 SaveChanges（多次存檔）；故「狀態推進＋回呼過帳＋存檔」以顯式交易包住，
        /// 任一步失敗即整體回滾（避免孤兒 GL/異動），單據維持同步前狀態待重試，並記錄＋提示。
        /// 因此各 onApprovedAsync 回呼只需單純執行過帳，<b>不應自行 try/catch 吞例外</b>——
        /// 吞掉例外會使此處的回滾失效（過帳半套已落、狀態卻照樣 commit）。
        /// </summary>
        protected async Task SyncApprovalStatusAsync(
            IApprovalDocument doc,
            Func<Task>? onApprovedAsync = null)
        {
            if (string.IsNullOrEmpty(doc.ApprovalRequestId)) return;
            var req = await Db.ApprovalRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == doc.ApprovalRequestId);
            if (req == null) return;

            var newStatus = req.Status switch
            {
                ApprovalStatus.Approved            => DocumentApprovalStatus.Approved,
                ApprovalStatus.Rejected            => DocumentApprovalStatus.Rejected,
                ApprovalStatus.ReturnedForRevision => DocumentApprovalStatus.Draft,
                ApprovalStatus.Withdrawn           => DocumentApprovalStatus.Draft,
                _ => (DocumentApprovalStatus?)null
            };
            if (newStatus == null) return;

            // 核准且帶自動過帳回呼：以交易包住整段，失敗整體回滾、維持原狀態待重試。
            if (newStatus == DocumentApprovalStatus.Approved && onApprovedAsync != null)
            {
                var reqId = doc.ApprovalRequestId;
                await using var tx = await Db.Database.BeginTransactionAsync();
                try
                {
                    doc.ApprovalStatus = newStatus.Value;
                    await onApprovedAsync();
                    await Db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync();
                    Db.ChangeTracker.Clear();   // 丟棄回滾後仍掛在追蹤器上的半套變更
                    Logger.LogError(ex,
                        "簽核核准後自動過帳失敗，已回滾，單據維持原狀態待重試（ApprovalRequestId={ReqId}）", reqId);
                    var loc = Svc<IStringLocalizer<ProjectZSharedResource>>();
                    TempData["ErrorMessage"] = string.Format(loc["AutoPostFailed"].Value, reqId, ex.Message);
                }
                return;
            }

            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft)
                doc.ApprovalRequestId = null;
            await Db.SaveChangesAsync();
        }

        /// <summary>
        /// 送簽的統一實作，吸收各單據 Controller 幾乎逐字相同的 Submit 動作：
        /// 權限 → 載入 → 僅 Draft/Rejected 可送 → 查簽核設定（無流程則直接核准）→
        /// 建立簽核申請、回寫 ApprovalRequestId/PendingApproval → 存檔。
        /// 各 Controller 的 Submit 改為一行委派：<c>=> SubmitForApprovalAsync&lt;Entity&gt;(id, MenuKey, DocType)</c>。
        /// MenuKey/DocType 多為 private const，無法以覆寫屬性傳遞，故以參數帶入。
        /// </summary>
        protected async Task<IActionResult> SubmitForApprovalAsync<T>(string id, string menuKey, string docType)
            where T : class, IApprovalDocument
        {
            if (!await Permissions.CanUpdate(menuKey)) return Forbid();
            var doc = await Db.Set<T>().FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.ApprovalStatus != DocumentApprovalStatus.Draft
                && doc.ApprovalStatus != DocumentApprovalStatus.Rejected)
                return BadRequest(new { success = false, message = SharedL["StagingErrDraftOrReject"].Value });

            var approval = Svc<IApprovalService>();
            var plantId  = UserContext.PlantId ?? "";
            var userId   = UserContext.UserId ?? "";
            var config   = await approval.GetApprovalConfig(docType, plantId);
            if (config?.ApprovalTemplateId == null)
            {
                doc.ApprovalStatus = DocumentApprovalStatus.Approved;
                await Db.SaveChangesAsync();
                return Ok(new { success = true, message = SharedL["StagingApprovedNoWorkflow"].Value });
            }

            // 交易包住「建立申請單＋回寫單據」：第二段存檔失敗時整體回滾，
            // 避免留下孤兒 Pending 申請單（單據仍 Draft 可再送 → 產生兩筆有效申請）。
            // InMemory 測試環境無真交易（TestBase 已忽略 TransactionIgnoredWarning），正式 PG 有效。
            await using var tx = await Db.Database.BeginTransactionAsync();
            var request = await approval.SubmitForApproval(id, docType, config.ApprovalTemplateId, userId);
            doc.ApprovalRequestId = request.Id;
            // 簽核 v1.4：模板開啟「同一簽核人自動跳簽」且全部步驟被跳過時，送審即終局核准
            // （contributor 已在 SubmitForApproval 交易內回寫 Approved），不可再覆寫回待審核；
            // 帶自動過帳的單據型別由既有 sync-on-read（SyncApprovalStatusAsync + onApprovedAsync）補過帳。
            var autoApproved = request.Status == ApprovalStatus.Approved;
            doc.ApprovalStatus = autoApproved
                ? DocumentApprovalStatus.Approved
                : DocumentApprovalStatus.PendingApproval;
            await Db.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok(new { success = true, message = autoApproved
                ? SharedL["StagingAutoApproved"].Value
                : SharedL["StagingSubmitted"].Value });
        }

        /// <summary>
        /// 撤簽的統一實作，吸收各單據 Controller 幾乎逐字相同的 Withdraw 動作：
        /// 權限 → 載入 → 僅 PendingApproval 可撤 → 撤回簽核申請 → 回 Draft、清 ApprovalRequestId → 存檔。
        /// 各 Controller 的 Withdraw 改為一行委派：<c>=> WithdrawApprovalAsync&lt;Entity&gt;(id, MenuKey)</c>。
        /// </summary>
        protected async Task<IActionResult> WithdrawApprovalAsync<T>(string id, string menuKey)
            where T : class, IApprovalDocument
        {
            if (!await Permissions.CanUpdate(menuKey)) return Forbid();
            var doc = await Db.Set<T>().FindAsync(id);
            if (doc == null) return NotFound();
            if (doc.ApprovalStatus != DocumentApprovalStatus.PendingApproval)
                return BadRequest(new { success = false, message = SharedL["StagingErrMustBePending"].Value });

            var approval = Svc<IApprovalService>();
            if (!await approval.WithdrawRequest(doc.ApprovalRequestId!, UserContext.UserId ?? ""))
                return BadRequest(new { success = false, message = SharedL["StagingWithdrawFailed"].Value });

            doc.ApprovalStatus    = DocumentApprovalStatus.Draft;
            doc.ApprovalRequestId = null;
            await Db.SaveChangesAsync();
            return Ok(new { success = true, message = SharedL["StagingWithdrawn"].Value });
        }

        /// <summary>共用資源在地化器（送/撤簽訊息等）。</summary>
        private IStringLocalizer<ProjectZSharedResource> SharedL =>
            Svc<IStringLocalizer<ProjectZSharedResource>>();

        /// <summary>
        /// 統一產生列印 ViewBag：QR Code SVG、列印時間，並依使用者 ID 解析顯示名稱。
        /// 取代各 Print action 手抄 QrSvg/UserNames 區塊（重用 Core PrintHelper）。
        /// </summary>
        protected virtual async Task PreparePrintViewBagAsync(
            string qrContent, IEnumerable<string?>? userIds = null)
        {
            ViewBag.QrSvg = PrintHelper.GenerateQrSvg(qrContent);
            ViewBag.PrintTime = await PlantNowAsync();

            var ids = (userIds ?? Enumerable.Empty<string?>())
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .Distinct()
                .ToList();

            ViewBag.UserNames = ids.Count == 0
                ? new Dictionary<string, string>()
                : await Db.Users.AsNoTracking()
                    .Where(u => ids.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
        }

        // ── 共用列印版型（Views/Shared/_PrintDocument.cshtml，2026-10-27）─────────────

        /// <summary>列印欄名（ProjectZSharedResource 的 Print_* 鍵）。</summary>
        protected IStringLocalizer<ProjectZSharedResource> PrintL => Svc<IStringLocalizer<ProjectZSharedResource>>();

        /// <summary>某畫面 Index 的資源檔（Resources/Modules/{module}/Views/{screen}/Index.*.resx），取該畫面既有的狀態／類型文字。</summary>
        protected IStringLocalizer ScreenText(string module, string screen) =>
            Svc<IStringLocalizerFactory>().Create($"Modules.{module}.Views.{screen}.Index",
                typeof(ProjectZSharedResource).Assembly.GetName().Name!);

        /// <summary>代碼配名稱（品號→品名、往來對象、單位、幣別、倉庫…）。</summary>
        protected Printing.PrintNameService PrintNames => Svc<Printing.PrintNameService>();

        /// <summary>UTC 時間戳 → 使用者時區的「yyyy-MM-dd HH:mm」；null 回 null。</summary>
        protected string? PrintTime(DateTime? utc) =>
            utc.HasValue ? DateTimeSvc.ToLocalTime(utc.Value).ToString("yyyy-MM-dd HH:mm") : null;

        protected Task<Printing.PrintFormats> PrintFormatsAsync() =>
            Printing.PrintFormats.LoadAsync(Svc<SmartFactory.MES.Modules.BAS.Services.IDisplayPrecision>());

        /// <summary>使用者 ID → 帳號名稱（PreparePrintViewBagAsync 已查好的 ViewBag.UserNames）；查無回原值。</summary>
        protected string? PrintUser(string? userId) =>
            userId != null && ViewBag.UserNames is Dictionary<string, string> map && map.TryGetValue(userId, out var n) ? n : userId;

        /// <summary>
        /// 簽核區「製作／審核／核准」的名字（ViewBag.PrintSign，_PrintBase 讀取）。規則見
        /// <see cref="Printing.PrintSignatureService"/>；<paramref name="createdBy"/> 為 null 時取送審人。
        /// </summary>
        protected async Task PreparePrintSignAsync(string documentId, string? createdBy)
        {
            var L = PrintL;
            ViewBag.PrintSign = await Svc<Printing.PrintSignatureService>()
                .ForAsync(documentId, createdBy, L["Print_Sign_OnBehalf"].Value, L["Print_Sign_Separator"].Value);
        }

        /// <summary>
        /// 列印時間一律用**目前廠別**的時區（Plant.DefaultTimeZone），不用個人時區：
        /// 同一張單不論誰印，紙上的時間都要對得上該廠的時鐘。廠別沒設或代碼解不開時退回 DateTimeSvc.Now。
        /// </summary>
        protected async Task<DateTime> PlantNowAsync()
        {
            var plantId = UserContext.PlantId;
            var tzId = string.IsNullOrEmpty(plantId) ? null
                : await Db.Plants.AsNoTracking().Where(p => p.Id == plantId).Select(p => p.DefaultTimeZone).FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(tzId))
            {
                try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tzId)); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return DateTimeSvc.Now;
        }

        protected IActionResult PrintDocumentView(Printing.PrintDocument doc) =>
            View("~/Views/Shared/_PrintDocument.cshtml", doc);
    }
}
