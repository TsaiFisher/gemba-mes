using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// 單據畫面「已存在單據」的面板動作 smoke test 模板（階段 3）。
    ///
    /// 補的是既有兩層測試的共同盲區：<see cref="CrudSmokeTestBase"/> 與各
    /// FormInteractionTests 都只驗**新增面板**，從不驗「把一張已經存在的單據重新打開」。
    /// 實際踩過的多個缺陷都落在這個縫裡——新增時好好的，單據存檔之後再打開，
    /// 面板的動作鈕就失效了。
    ///
    /// 核心斷言是 handler 解析：CSP 強化後所有動作都改走 <c>data-action</c> 委派，
    /// dispatcher 是用 <c>window[name]</c> 找函式，找不到只會 <c>console.warn</c> 然後
    /// **靜默不動作**（見 wwwroot/js/data-action.js）。使用者的體感是「按了沒反應」，
    /// 沒有任何錯誤、沒有任何紀錄，是最難被發現的一種壞法。這裡直接把面板內每個
    /// data-action 的目標函式撈出來檢查，缺一個就紅。
    ///
    /// 派生類只要提供「怎麼建一張單」，就得到 4 個標準斷言。
    /// </summary>
    [Collection("UI Tests")]
    public abstract class DocActionSmokeTestBase
    {
        protected readonly UiTestFixture _fixture;

        protected DocActionSmokeTestBase(UiTestFixture fixture) => _fixture = fixture;

        // ── 派生類必須提供 ────────────────────────────────────────────────
        /// <summary>單據 Index 路徑，例如 "/ScrapDoc"。</summary>
        protected abstract string IndexPath { get; }

        /// <summary>「新增」按鈕 selector。</summary>
        protected abstract string NewButtonSelector { get; }

        /// <summary>面板載入完成的判斷元素，例如 "#scrapForm"。</summary>
        protected abstract string PanelReadySelector { get; }

        /// <summary>填好足以存檔的欄位（含至少一筆明細）。</summary>
        protected abstract Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix);

        // ── 可選覆寫 ──────────────────────────────────────────────────────
        protected virtual string PanelBodySel => "#sidePanelBody";

        /// <summary>存檔按鈕。預設為面板內第一個 submit。</summary>
        protected virtual string SaveButtonSelector => $"{PanelBodySel} button[type='submit']";

        /// <summary>
        /// 列表中「打開這張單」按鈕的 data-url 片段候選，依序嘗試。
        ///
        /// 為什麼是「候選清單」而不是單一 Edit：單據列表的「編輯」按鈕多半只在草稿狀態
        /// 才渲染，存檔後單據常常已經不是草稿（例如報廢單沒有簽核流程設定時，一存檔就
        /// 直接是已完成），此時列表上只剩「檢視」。而「非草稿單據打不開／動作鈕失效」
        /// 正是本測試要防的缺陷，所以不能因為找不到 Edit 就放棄。
        /// </summary>
        protected virtual IReadOnlyList<string> OpenUrlFragments =>
            new[] { $"{IndexPath}/Edit", $"{IndexPath}/Details" };

        /// <summary>
        /// 重新打開已存在單據後，預期至少要出現的動作按鈕 selector。
        /// 預設只要求有任何 data-action 按鈕；單據若有特定動作可覆寫收緊。
        /// </summary>
        protected virtual string ExpectedActionSelector => $"{PanelBodySel} [data-action]";

        /// <summary>
        /// 這張單存檔後，在「它自己的面板」上是否仍有後續動作。
        ///
        /// 絕大多數單據為 true（送審／作廢／確認…都在本面板進行）。少數單據存檔即終結於
        /// 自身面板——例如內部領用單在未設簽核流程時存檔即自動核准並轉 Confirmed，之後的
        /// 領料統一在「庫存管理 → 倉庫作業」進行，本面板重開為唯讀且刻意不再有動作鈕。
        /// 這類單覆寫成 false：第 ④ 條改為驗「面板以唯讀狀態正常重開」（fieldset 被 disabled），
        /// 而非強求動作鈕——但第 ①②③ 條（可開啟／無 JS 錯誤／handler 都掛得到）照樣守。
        /// </summary>
        protected virtual bool ReopenedDocHasActions => true;

        /// <summary>終結型單據重開後，用來確認「唯讀面板有正常渲染」的 selector。</summary>
        protected virtual string ReadonlyPanelSelector => $"{PanelBodySel} fieldset[disabled]";

        /// <summary>
        /// 重開既有單據後，判斷面板載入完成的元素；預設同 <see cref="PanelReadySelector"/>。
        ///
        /// 少數畫面「新增」與「重開」根本是兩個不同的 View——例如 FIN 的付款／收款，
        /// 新增走 <c>_CreateForm</c>（有 <c>&lt;form id="panelForm"&gt;</c>），重開走唯讀的
        /// <c>_Form</c>（只有 <c>&lt;fieldset disabled&gt;</c>、根本沒有 form 元素）。
        /// 這類畫面覆寫本屬性指定重開後該等的元素。而「新增與重開是不同 View」正是
        /// 本測試最該守的縫：新增那邊好好的，不代表重開那邊沒壞。
        /// </summary>
        protected virtual string ReopenedPanelReadySelector => PanelReadySelector;

        /// <summary>
        /// 存檔後「回列表找這張單」時附加的查詢字串（不含前導 <c>&amp;</c>），預設不附加。
        ///
        /// 單據類畫面預設新單在前，取列表第一列就是剛建的那張。但主檔類畫面（代碼、
        /// 銀行帳戶…）主鍵由使用者輸入、列表多依代碼排序又有分頁，剛建的那筆常常不在
        /// 第一頁。此時若沿用「開第一列」，就算新增其實靜默失敗也會因為打開了別人的
        /// 資料而假綠——BankAccount 的重複同名欄位缺陷正是靠「鎖定本次新增那筆」才抓到。
        /// 這類畫面覆寫本屬性以搜尋條件縮小列表，並搭配 <see cref="OpenUrlFragments"/>
        /// 指定該筆的 Edit URL。本屬性在 <see cref="FillRequiredFieldsAsync"/> 之後才求值，
        /// 因此可以安全引用當次產生的代碼。
        /// </summary>
        protected virtual string ReopenIndexQuery => "";

        /// <summary>
        /// 建單前的資料前置（預設不做事）。需要基準庫存的單據（報廢／調撥等）覆寫此鉤子
        /// 把 E2E 庫存補回——否則若某個流程測試先跑吃掉了料，本測試會撞「可用量不足」。
        /// 測試執行順序會隨新增測試類別而改變，故不能倚賴前一個測試留下的庫存狀態。
        /// </summary>
        protected virtual Task PrepareDataAsync() => Task.CompletedTask;

        // ── 可選：真的把動作按下去（⑤⑥）──────────────────────────────────
        // ①～④ 只驗「按鈕在、handler 掛得到」，從不按下去——確認／作廢／取消確認／刪除
        // 按了之後結果對不對，在這一層原本完全沒有人驗。派生類宣告下列屬性即可啟用；
        // 不宣告的單據兩條測試直接結束，不影響既有行為。

        /// <summary>
        /// 一個動作步驟：面板上 data-doc-action（或 data-url 含 /{Action}/）的按鈕，按下後狀態應變成 <paramref name="ExpectedStatus"/>。
        /// 按鈕不是這兩種寫法的單據（例如進貨的 <c>data-action="confirmDoc"</c>）以 <paramref name="ButtonSelector"/> 指定。
        /// </summary>
        protected sealed record DocActionStep(string Action, string ExpectedStatus, string? ButtonSelector = null);

        /// <summary>依序執行的動作。每一步之前都會重新打開這張單（上一步成功後頁面會重新整理）。</summary>
        protected virtual IReadOnlyList<DocActionStep> ActionSteps => Array.Empty<DocActionStep>();

        /// <summary>
        /// 讀單據狀態的 SQL，<c>{0}</c> 代入單號（已跳脫單引號），回傳一欄文字；
        /// 單據不存在時必須回傳 NULL（刪除測試靠它判斷「真的刪掉了」）。
        /// 例：<c>SELECT "Status"::text FROM "X" WHERE "XId" = '{0}'</c>
        /// </summary>
        protected virtual string? DocStatusSql => null;

        /// <summary>草稿單據能否從列表的 deleteItem 按鈕刪除。</summary>
        protected virtual bool DraftDeletableFromList => false;

        /// <summary>全部動作步驟成功後的額外斷言（例如分錄、成本回寫），預設不做事。</summary>
        protected virtual Task AssertAfterActionsAsync(string docId) => Task.CompletedTask;

        /// <summary>
        /// 每一步按下按鈕之前的前置動作（面板已重新打開）。用於業務規則要求先處理關聯單據的情境，
        /// 例：出貨單取消確認前必須先作廢確認時自動開的應收發票（2026-10-22 起的規則）。
        /// </summary>
        protected virtual Task BeforeStepAsync(IPage page, DocActionStep step, string docId) => Task.CompletedTask;

        /// <summary>
        /// 以頁面上的防偽 token 對某個端點送 POST（表單編碼），回傳後端 JSON 的 success／message。
        /// 給 <see cref="BeforeStepAsync"/> 走真正的業務端點用——不要直接改資料庫，會略過回沖等副作用。
        /// </summary>
        protected static async Task<(bool Ok, string Message)> PostFormAsync(IPage page, string url, IDictionary<string, string> form)
        {
            var r = await page.EvaluateAsync<System.Text.Json.JsonElement>(@"async ([url, form]) => {
                const token = document.querySelector('input[name=""__RequestVerificationToken""]')?.value ?? '';
                const body = new URLSearchParams({ ...form, __RequestVerificationToken: token });
                const res = await fetch(url, { method: 'POST', body,
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'X-Requested-With': 'XMLHttpRequest' } });
                let j = {}; try { j = await res.json(); } catch {}
                return { ok: res.ok && j.success === true, message: j.message ?? ('HTTP ' + res.status) };
            }", new object[] { url, form });
            return (r.GetProperty("ok").GetBoolean(), r.GetProperty("message").GetString() ?? "");
        }

        /// <summary>最近一次 <see cref="CreateThenReopenAsync"/> 建出並重開的單號（由列表按鈕的 data-url 解析）。</summary>
        private string? _lastDocId;

        /// <summary>
        /// 存檔之後、回列表之前呼叫（預設不做事）。
        ///
        /// 給「列表排序無法保證新單在第一列」的畫面用：派生類可在
        /// <see cref="FillRequiredFieldsAsync"/> 先掛好 <c>page.WaitForResponseAsync</c>，
        /// 在這裡等到存檔回應、取出新單號，再讓 <see cref="OpenUrlFragments"/> 鎖定那一張。
        /// 否則「開第一列」可能開到前一輪留下的舊單——存檔其實失敗了，測試照樣綠。
        /// </summary>
        protected virtual Task AfterSaveAsync(IPage page) => Task.CompletedTask;

        // ════════════════════════════════════════════════════════════════════
        // 共用流程：建一張單，回到列表，重新打開它
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 建立一張單並回傳重新打開它之後的 page 與該次蒐集到的 JS 錯誤。
        ///
        /// 存檔後取列表**第一列**重開，而不是去比對單號：單號由 DocNumberService 產生，
        /// 測試事先不知道；各單據列表預設又都是新單在前。這樣不必為每個畫面客製定位邏輯。
        /// </summary>
        protected async Task<(IPage page, List<string> jsErrors)> CreateThenReopenAsync()
        {
            await PrepareDataAsync();

            var page = await _fixture.NewPageAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // ① 新增
            await page.ClickAsync(NewButtonSelector);
            await page.WaitForSelectorAsync($"{PanelBodySel} {PanelReadySelector}",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await FillRequiredFieldsAsync(page, DateTime.UtcNow.ToString("MMddHHmm"));

            await page.ClickAsync(SaveButtonSelector);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            // 存檔被伺服端驗證擋下時，面板會重新渲染 _Form 並帶著欄位錯誤。
            // 一定要在離開這一頁之前把訊息撈走：否則後面只會看到「列表沒有可開啟的單據」，
            // 完全看不出是哪個欄位不合法——這與 FlowTestBase 吞掉 alert 訊息是同一類問題。
            var validationHint = await DescribeValidationErrorsAsync(page);
            await AfterSaveAsync(page);

            // ② 回列表，重開第一張單（主檔類畫面可用 ReopenIndexQuery 縮小到本次新增那筆）
            var reopenQuery = string.IsNullOrEmpty(ReopenIndexQuery) ? "" : $"&{ReopenIndexQuery}";
            await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true{reopenQuery}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            ILocator? openBtn = null;
            foreach (var fragment in OpenUrlFragments)
            {
                var candidate = page.Locator($"[data-url*='{fragment}']").First;
                if (await candidate.CountAsync() > 0) { openBtn = candidate; break; }
            }

            Assert.True(openBtn != null,
                $"{IndexPath}：存檔後列表沒有任何可開啟的單據（試過 data-url 含 " +
                $"{string.Join(" / ", OpenUrlFragments)} 的按鈕）。多半是上一步存檔沒有成立。" +
                validationHint);

            _lastDocId = ParseDocId(await openBtn!.GetAttributeAsync("data-url"));

            jsErrors.Clear();   // 只計「重新打開既有單據」這段的錯誤
            await openBtn!.ClickAsync();
            await page.WaitForSelectorAsync($"{PanelBodySel} {ReopenedPanelReadySelector}",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(600);

            return (page, jsErrors);
        }

        /// <summary>
        /// 撈出面板上的欄位級驗證訊息與驗證摘要，組成可直接行動的說明。
        /// 面板已關閉（＝存檔成功）時回傳空字串。
        /// </summary>
        private async Task<string> DescribeValidationErrorsAsync(IPage page)
        {
            try
            {
                var messages = await page.EvaluateAsync<string[]>(@"(bodySel) => {
                    const panel = document.querySelector(bodySel);
                    if (!panel) return [];
                    const out = [];
                    panel.querySelectorAll('[data-valmsg-for]').forEach(el => {
                        const t = (el.textContent || '').trim();
                        if (t) out.push(el.getAttribute('data-valmsg-for') + '：' + t);
                    });
                    panel.querySelectorAll('.validation-summary-errors li, .text-danger').forEach(el => {
                        const t = (el.textContent || '').trim();
                        if (t && !out.some(x => x.endsWith(t))) out.push(t);
                    });
                    return out;
                }", PanelBodySel);

                return messages.Length == 0
                    ? ""
                    : $"\n面板上的驗證訊息：\n  - {string.Join("\n  - ", messages)}";
            }
            catch
            {
                return "";   // 面板已消失等情況；診斷用，不該讓它自己變成失敗原因
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ① 既有單據的面板可以打開
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task ExistingDoc_EditPanel_Opens()
        {
            var (page, _) = await CreateThenReopenAsync();

            var body = await page.InnerHTMLAsync(PanelBodySel);
            Assert.False(string.IsNullOrWhiteSpace(body),
                $"{IndexPath}：重新打開既有單據後，面板內容是空的。");
        }

        // ════════════════════════════════════════════════════════════════════
        // ② 既有單據的面板不得有 JS 錯誤
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task ExistingDoc_EditPanel_NoJsError()
        {
            var (_, jsErrors) = await CreateThenReopenAsync();

            Assert.True(jsErrors.Count == 0,
                $"{IndexPath}：重新打開既有單據時出現 JS 錯誤：\n{string.Join("\n", jsErrors)}");
        }

        // ════════════════════════════════════════════════════════════════════
        // ③ 面板內每個 data-action 的目標函式都必須存在
        //    （dispatcher 找不到函式時只 console.warn 然後靜默不動作）
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task ExistingDoc_PanelActions_AllHandlersDefined()
        {
            var (page, _) = await CreateThenReopenAsync();

            var missing = await page.EvaluateAsync<string[]>(@"(bodySel) => {
                const panel = document.querySelector(bodySel);
                if (!panel) return ['(找不到面板)'];
                const names = new Set();
                panel.querySelectorAll('*').forEach(el => {
                    for (const attr of el.attributes) {
                        // data-action 與 data-action-{event}（change/input/submit…）
                        if (attr.name === 'data-action' || attr.name.startsWith('data-action-')) {
                            if (attr.value) names.add(attr.value);
                        }
                    }
                });
                return [...names].filter(n => typeof window[n] !== 'function');
            }", PanelBodySel);

            Assert.True(missing.Length == 0,
                $"{IndexPath}：既有單據面板上有 data-action 找不到對應的全域函式：" +
                $"{string.Join("、", missing)}。\n" +
                "dispatcher（wwwroot/js/data-action.js）是用 window[name] 解析，" +
                "找不到只會 console.warn 然後靜默不動作——使用者的體感是「按了沒反應」。" +
                "請確認該函式有掛到 window，且定義所在的 script 區塊在面板載入時真的有執行。");
        }

        // ════════════════════════════════════════════════════════════════════
        // ④ 既有單據仍應有可用的動作按鈕
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task ExistingDoc_PanelHasActionButtons()
        {
            var (page, _) = await CreateThenReopenAsync();

            if (!ReopenedDocHasActions)
            {
                // 終結型單據：不強求動作鈕，改驗唯讀面板有正常重開（fieldset 被 disabled）。
                var readonlyCount = await page.Locator(ReadonlyPanelSelector).CountAsync();
                Assert.True(readonlyCount > 0,
                    $"{IndexPath}：終結型單據重開後，找不到唯讀面板標記" +
                    $"（selector：{ReadonlyPanelSelector}）。應以唯讀狀態正常重開。");
                return;
            }

            var count = await page.Locator(ExpectedActionSelector).CountAsync();
            Assert.True(count > 0,
                $"{IndexPath}：重新打開既有單據後，面板上一個動作按鈕都沒有" +
                $"（selector：{ExpectedActionSelector}）。單據存檔後應仍可往下走。");
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑤ 面板上的動作真的按下去，狀態真的變了
        //    toast 只代表 API 回了 success；這裡直接讀資料庫。
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task ExistingDoc_ActionSteps_ChangeStatus()
        {
            if (ActionSteps.Count == 0) return;
            Assert.True(DocStatusSql != null, $"{IndexPath}：宣告了 ActionSteps 就必須提供 DocStatusSql。");

            var (page, _) = await CreateThenReopenAsync();
            var docId = RequireDocId();
            var alerts = AcceptConfirmsCollectAlerts(page);

            for (var i = 0; i < ActionSteps.Count; i++)
            {
                var step = ActionSteps[i];
                if (i > 0) await ReopenByIdAsync(page, docId);
                await BeforeStepAsync(page, step, docId);

                var btn = page.Locator(step.ButtonSelector != null
                    ? $"{PanelBodySel} {step.ButtonSelector}"
                    : $"{PanelBodySel} [data-doc-action='{step.Action}'], {PanelBodySel} [data-url*='/{step.Action}/']").First;
                if (await btn.CountAsync() == 0)
                {
                    var present = await page.EvaluateAsync<string[]>(@"(sel) =>
                        [...document.querySelectorAll(sel + ' [data-doc-action], ' + sel + ' [data-url], ' + sel + ' button[data-action]')]
                            .map(el => el.dataset.docAction || el.dataset.url || el.dataset.action)", PanelBodySel);
                    Assert.Fail($"{IndexPath} {docId}：第 {i + 1} 步找不到「{step.Action}」按鈕。" +
                                $"面板上現有：{string.Join("、", present)}");
                }

                alerts.Clear();
                await btn.ClickAsync();
                var status = await WaitForStatusAsync(docId, step.ExpectedStatus, alerts);

                Assert.True(alerts.Count == 0,
                    $"{IndexPath} {docId}：按下「{step.Action}」後跳出錯誤訊息：{string.Join(" / ", alerts)}");
                Assert.True(status == step.ExpectedStatus,
                    $"{IndexPath} {docId}：按下「{step.Action}」後狀態應為 {step.ExpectedStatus}，資料庫是 {status ?? "(不存在)"}。");
            }

            await AssertAfterActionsAsync(docId);
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑥ 草稿從列表刪除，資料庫裡真的沒了
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task DraftDoc_ListDelete_RemovesDoc()
        {
            if (!DraftDeletableFromList) return;
            Assert.True(DocStatusSql != null, $"{IndexPath}：DraftDeletableFromList 需搭配 DocStatusSql。");

            var (page, _) = await CreateThenReopenAsync();
            var docId = RequireDocId();
            var alerts = AcceptConfirmsCollectAlerts(page);

            await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var del = page.Locator($"[data-action='deleteItem'][data-id='{docId}']").First;
            Assert.True(await del.CountAsync() > 0,
                $"{IndexPath}：列表上找不到 {docId} 的刪除鈕（data-action='deleteItem'）。");

            await del.ClickAsync();
            var status = await WaitForStatusAsync(docId, expected: null, alerts);

            Assert.True(alerts.Count == 0, $"{IndexPath} {docId}：刪除後跳出錯誤訊息：{string.Join(" / ", alerts)}");
            Assert.True(status == null, $"{IndexPath} {docId}：按了刪除，資料庫裡還在（狀態 {status}）。");
        }

        // ── ⑤⑥ 共用 ─────────────────────────────────────────────────────────

        private string RequireDocId()
        {
            Assert.True(!string.IsNullOrEmpty(_lastDocId),
                $"{IndexPath}：無法從列表按鈕的 data-url 解析出單號，⑤⑥ 無法鎖定這張單。");
            return _lastDocId!;
        }

        /// <summary>data-url 形如 <c>/X/Edit/ID</c> 或 <c>/X/Edit?id=ID</c>。</summary>
        private static string? ParseDocId(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var q = url.IndexOf('?');
            if (q >= 0)
            {
                foreach (var kv in url[(q + 1)..].Split('&'))
                    if (kv.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
                        return Uri.UnescapeDataString(kv[3..]);
                url = url[..q];
            }
            var last = url.TrimEnd('/').Split('/').Last();
            return string.IsNullOrEmpty(last) ? null : Uri.UnescapeDataString(last);
        }

        /// <summary>
        /// confirm 一律按確定（Playwright 預設會按取消，動作就永遠不會送出）；
        /// alert 是動作失敗的訊息管道（docAction／deleteItem 失敗都用 alert），收集起來當斷言內容。
        /// </summary>
        private static List<string> AcceptConfirmsCollectAlerts(IPage page)
        {
            var alerts = new List<string>();
            page.Dialog += async (_, d) =>
            {
                if (d.Type == DialogType.Alert) lock (alerts) alerts.Add(d.Message);
                try { await d.AcceptAsync(); } catch (PlaywrightException) { /* 頁面已重新整理 */ }
            };
            return alerts;
        }

        private async Task ReopenByIdAsync(IPage page, string docId)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            ILocator? openBtn = null;
            foreach (var fragment in OpenUrlFragments)
            {
                var candidate = page.Locator($"[data-url*='{fragment}'][data-url*='{docId}']").First;
                if (await candidate.CountAsync() > 0) { openBtn = candidate; break; }
            }
            Assert.True(openBtn != null, $"{IndexPath}：列表上找不到 {docId} 可重新打開的按鈕。");

            await openBtn!.ClickAsync();
            await page.WaitForSelectorAsync($"{PanelBodySel} {ReopenedPanelReadySelector}",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(600);
        }

        /// <summary>
        /// 輪詢資料庫直到狀態等於預期、出現 alert，或逾時（10 秒）。
        /// 不用固定等待：CPU 被搶時固定等待不夠，閒時又白等。
        /// </summary>
        private async Task<string?> WaitForStatusAsync(string docId, string? expected, List<string> alerts)
        {
            var sql = string.Format(DocStatusSql!, docId.Replace("'", "''"));
            string? status = null;
            for (var waited = 0; waited < 10_000; waited += 250)
            {
                status = await SeedFixture.ScalarAsync<string>(sql);
                if (status == expected) break;
                lock (alerts) if (alerts.Count > 0) break;
                await Task.Delay(250);
            }
            return status;
        }
    }
}
