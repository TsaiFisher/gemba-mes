using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages
{
    /// <summary>
    /// 所有使用 crud-panel.js 側欄（offcanvas）的 CRUD 畫面共用操作。
    /// 封裝「開面板 → 填表 → 儲存 → 等 Toast → 關面板」的標準流程。
    /// </summary>
    public class CrudPanelPage : BasePage
    {
        // DOM 契約（預設＝共用 crud-panel.js/sidePanel；BAS 自洽 crudPanel 以另一組選擇器建構）
        private readonly string PanelSel;
        private readonly string PanelBodySel;
        private readonly string PanelLabelSel;
        private const string ToastSel = "#successToast";
        private readonly string SubmitBtn;
        private readonly string CloseBtnSel;

        public CrudPanelPage(IPage page, string baseUrl)
            : this(page, baseUrl,
                   panelSel: "#sidePanel",
                   bodySel: "#sidePanelBody",
                   labelSel: "#sidePanelLabel",
                   closeBtnSel: "#sidePanelBody button.btn-secondary")
        { }

        public CrudPanelPage(IPage page, string baseUrl,
            string panelSel, string bodySel, string labelSel, string closeBtnSel)
            : base(page, baseUrl)
        {
            PanelSel      = panelSel;
            PanelBodySel  = bodySel;
            PanelLabelSel = labelSel;
            CloseBtnSel   = closeBtnSel;
            SubmitBtn     = $"{bodySel} button[type='submit']";
        }

        // ── 導航 ────────────────────────────────────────────────────────────
        public async Task GotoIndexAsync(string path)
            => await GotoAsync(path);

        // ── 面板操作 ─────────────────────────────────────────────────────────

        /// <summary>點擊「新增」按鈕並等候面板載入（等待指定的 selector 出現在 sidePanelBody）。</summary>
        public async Task OpenNewPanelAsync(string newButtonSelector, string waitForSelector)
        {
            await _page.ClickAsync(newButtonSelector);
            await _page.WaitForSelectorAsync($"{PanelBodySel} {waitForSelector}");
        }

        /// <summary>點擊列表中的「編輯」按鈕（使用 data-url 含目標路徑的按鈕）。</summary>
        public async Task OpenEditPanelAsync(string editButtonSelector, string waitForSelector)
        {
            await _page.ClickAsync(editButtonSelector);
            await _page.WaitForSelectorAsync($"{PanelBodySel} {waitForSelector}");
        }

        /// <summary>點擊 side panel 內的儲存按鈕，等候 Toast / 導航（reload = 儲存成功） 或驗證錯誤。</summary>
        public async Task<SaveResult> ClickSaveAsync(int timeoutMs = 5000)
        {
            // ① 攔截 JsonFail 的 alert()
            string? serverAlertMsg = null;
            void OnDialog(object? _, IDialog dialog)
            {
                serverAlertMsg = dialog.Message;
                _ = dialog.DismissAsync();
            }
            _page.Dialog += OnDialog;

            // 記下這次按下儲存「真的送出去」的 POST 與它的回應。
            // 以前只看「有沒有導航／toast 出現」：任何一次無關的重新整理或殘留的 toast
            // 都會被當成「存檔成功」，必填測試就會報「清空 X 仍存檔成功」，
            // 而 DB 根本沒有寫入（2026-11-06／11-07 nightly：NCR ProductCode、DefectCode Code）。
            var formAction = await _page.EvaluateAsync<string?>(
                "sel => { const b = document.querySelector(sel); const f = b && b.form; return f ? f.action : null; }",
                SubmitBtn);
            var formPath = formAction != null ? new Uri(formAction).AbsolutePath : null;
            IResponse? saveResponse = null;
            void OnResponse(object? _, IResponse r)
            {
                if (r.Request.Method == "POST" && formPath != null
                    && string.Equals(new Uri(r.Url).AbsolutePath, formPath, StringComparison.OrdinalIgnoreCase))
                    saveResponse = r;
            }
            _page.Response += OnResponse;
            LastSaveDiagnostic = null;

            try
            {
                // ② 在 click 前先排隊等待「頁面導航」（成功 → refreshList → location.reload）
                //    與「toast/validation JS 輪詢」兩個任務，取先完成的那個。
                var navTask = _page.WaitForNavigationAsync(new PageWaitForNavigationOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout   = timeoutMs
                });

                var pollTask = _page.WaitForFunctionAsync(
                    @"(bodySel) => {
                        const toast = document.getElementById('successToast');
                        if (toast && toast.classList.contains('show')) return 'toast';
                        const err = document.querySelector(bodySel + ' [data-valmsg-for]:not(:empty)');
                        if (err) return 'validation';
                        return null;
                    }",
                    PanelBodySel,
                    new PageWaitForFunctionOptions { Timeout = timeoutMs }
                );

                await _page.ClickAsync(SubmitBtn);

                // ③ 取先完成的任務
                var first = await Task.WhenAny(navTask, pollTask);

                if (first == navTask)
                {
                    // 頁面 reload = refreshList 被觸發 = 儲存成功
                    await navTask; // re-await to propagate any exception
                    return await ConfirmSuccessAsync();
                }
                else
                {
                    var result = (await pollTask).ToString();
                    return result?.Contains("toast") == true ? await ConfirmSuccessAsync() : SaveResult.ValidationError;
                }
            }
            catch (TimeoutException te)
            {
                await Task.Delay(150); // 讓 async OnDialog callback 有機會完成
                if (serverAlertMsg != null)
                    // JsonFail alert = form 被 server 拒絕（空白驗證或業務規則）
                    return SaveResult.ValidationError;

                throw new InvalidOperationException(
                    $"ClickSaveAsync 超時（{timeoutMs}ms）— 無回應（no toast / no navigation / no validation error / no dialog）", te);
            }
            catch (Exception ex) when (ex.Message.Contains("Navigation") ||
                                        ex.Message.Contains("navigation"))
            {
                // 若導航本身失敗（例如頁面出錯），視為儲存失敗
                throw new InvalidOperationException($"儲存後導航失敗：{ex.Message}", ex);
            }
            finally
            {
                _page.Dialog -= OnDialog;
                _page.Response -= OnResponse;
            }

            // 出現成功訊號時，回頭核對伺服端對這次 POST 的回應：
            // 只有 crud-panel 的成功契約（<script>…refreshList…／{"success":true}）才算成功。
            async Task<SaveResult> ConfirmSuccessAsync()
            {
                if (formPath == null) return SaveResult.Success;   // 找不到 form，維持舊判斷
                if (saveResponse == null)
                {
                    LastSaveDiagnostic = $"出現成功訊號，但沒有送出 POST {formPath}——訊號來自其他動作";
                    return SaveResult.ValidationError;
                }
                string body;
                try { body = await saveResponse.TextAsync(); }
                catch (PlaywrightException) { return SaveResult.Success; } // 導航後 body 已不可讀；POST 有送出且頁面已重載
                var head = body.TrimStart();
                // 只把「明確的失敗」判成失敗：JSON success:false、非 2xx、或回傳的是表單 partial（驗證失敗重繪）。
                // 其餘格式維持成功，避免各畫面自訂的成功回應被誤判。
                var compact = head.Replace(" ", "");
                var failed = !saveResponse.Ok
                             || (compact.StartsWith("{") && compact.Contains("\"success\":false"))
                             || (head.StartsWith("<") && !head.StartsWith("<script", StringComparison.OrdinalIgnoreCase));
                var ok = !failed;
                LastSaveDiagnostic = $"POST {formPath} → {saveResponse.Status}：{(head.Length > 300 ? head[..300] : head)}";
                return ok ? SaveResult.Success : SaveResult.ValidationError;
            }
        }

        /// <summary>最近一次 <see cref="ClickSaveAsync"/> 的伺服端回應摘要（斷言訊息用）。</summary>
        public string? LastSaveDiagnostic { get; private set; }

        /// <summary>點擊關閉/取消按鈕，等候面板消失。</summary>
        public async Task ClosePanelAsync()
        {
            await _page.ClickAsync(CloseBtnSel);
            await _page.WaitForSelectorAsync($"{PanelSel}:not(.show)", new()
            {
                State = WaitForSelectorState.Attached,
                Timeout = 3000,
            }).ConfigureAwait(false);
        }

        // ── 斷言輔助 ─────────────────────────────────────────────────────────

        /// <summary>取得目前面板標題（sidePanelLabel）。</summary>
        public async Task<string> GetPanelTitleAsync()
            => await _page.InnerTextAsync(PanelLabelSel);

        /// <summary>確認面板 body 內只有一個 offcanvas-header（雙標題回歸守門）。</summary>
        public async Task<int> CountInnerOffcanvasHeadersAsync()
            => await _page.Locator($"{PanelBodySel} .offcanvas-header").CountAsync();

        /// <summary>
        /// 清空面板內所有欄位級驗證訊息。
        ///
        /// <see cref="ClickSaveAsync"/> 是以「面板內出現非空的 [data-valmsg-for]」判定驗證失敗，
        /// 因此同一個面板連續送出多次時（無效輸入等價類逐欄測），上一次留下的訊息會讓下一次
        /// 在請求送出前就立刻回 ValidationError——測試看起來全綠，實際上什麼都沒驗到。
        /// 每次送出前呼叫這支把畫面歸零。
        /// </summary>
        public async Task ClearValidationMessagesAsync()
            => await _page.EvaluateAsync(
                "sel => document.querySelectorAll(sel + ' [data-valmsg-for]')" +
                ".forEach(e => { e.textContent = ''; })", PanelBodySel);

        /// <summary>確認 Toast 目前是否顯示。</summary>
        public async Task<bool> IsToastVisibleAsync()
            => await _page.IsVisibleAsync($"{ToastSel}.show");

        /// <summary>
        /// 確認「儲存」按鈕的 onsubmit 有 return false（防原生雙重提交）。
        /// 透過 DOM attribute 驗證，不需要實際送出。
        /// </summary>
        public async Task<bool> FormHasReturnFalseAsync()
        {
            var onsubmit = await _page.GetAttributeAsync(
                $"{PanelBodySel} form", "onsubmit");
            // submitPanel 本身已 return false；onsubmit 不應啟動原生 action
            return onsubmit == null || !onsubmit.Contains("return true");
        }
    }

    public enum SaveResult { Success, ValidationError }
}
