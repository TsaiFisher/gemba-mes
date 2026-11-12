using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages
{
    /// <summary>
    /// 單據明細表單（side panel 內含可動態增刪的明細列）共用互動操作。
    ///
    /// 用於 P2 CSP 回補（inline on*= → data-action）前後的「行為守門」：
    /// addLine/removeLine/onProductChange/onUomChange 等互動，單元測試覆蓋不到，
    /// 必須以 E2E 驗證。selector 一律避免依賴 <c>onclick=</c> 或 <c>data-action=</c>
    /// 屬性，改用穩定的圖示 class（bi-plus-lg / bi-x-lg）與欄位 class（product-select /
    /// line-uom）＋ <c>#linesTbody tr</c>，使同一份測試在回補前後皆適用、能抓出
    /// 委派失聯（handler 沒被觸發 → 列沒新增/沒移除 / 冒 JS error）。
    /// </summary>
    public class DocFormPage : CrudPanelPage
    {
        // DOM 契約（與 INV 單據 _Form.cshtml 一致）
        private const string PanelBodySel  = "#sidePanelBody";
        private const string LinesTbodySel = "#linesTbody";
        private const string LineRowSel    = "#linesTbody tr";

        // 圖示 class 穩定（不隨在地化/回補變動）→ 用於定位「新增列 / 移除列」按鈕
        private const string AddLineBtnSel    = "#sidePanelBody button:has(i.bi-plus-lg)";
        private const string RemoveLastBtnSel = "#linesTbody tr:last-child button:has(i.bi-x-lg)";

        public DocFormPage(IPage page, string baseUrl) : base(page, baseUrl) { }

        /// <summary>暴露底層 IPage，供測試訂閱 PageError 等事件。</summary>
        public IPage Page => _page;

        /// <summary>目前明細列數。</summary>
        public Task<int> LineCountAsync()
            => _page.Locator(LineRowSel).CountAsync();

        /// <summary>
        /// 點「新增明細列」並等候列數增加。回傳新的列數。
        /// 若 addLine handler 失聯（inline→data-action 回補出錯），列數不會增加 → 等候逾時拋例外。
        /// </summary>
        public async Task<int> AddLineAsync(int timeoutMs = 4000)
        {
            var before = await LineCountAsync();
            await _page.ClickAsync(AddLineBtnSel);
            await _page.WaitForFunctionAsync(
                "n => document.querySelectorAll('#linesTbody tr').length > n",
                before,
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
            return await LineCountAsync();
        }

        /// <summary>
        /// 點最後一列的「移除」並等候列數減少。回傳新的列數。
        /// removeLine handler 失聯時列數不減 → 逾時拋例外。
        /// </summary>
        public async Task<int> RemoveLastLineAsync(int timeoutMs = 4000)
        {
            var before = await LineCountAsync();
            await _page.ClickAsync(RemoveLastBtnSel);
            await _page.WaitForFunctionAsync(
                "n => document.querySelectorAll('#linesTbody tr').length < n",
                before,
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
            return await LineCountAsync();
        }

        /// <summary>最後一列的品號下拉可選值數（不含空白佔位）。0 表示無種子資料。</summary>
        public async Task<int> LastLineProductOptionCountAsync()
        {
            var opts = _page.Locator("#linesTbody tr:last-child select.product-select option[value]:not([value=''])");
            return await opts.CountAsync();
        }

        /// <summary>
        /// 在最後一列選第一個有效品號，觸發 onProductChange（onchange）。
        /// 需有種子品號；呼叫前請先以 <see cref="LastLineProductOptionCountAsync"/> 判斷。
        /// </summary>
        public async Task SelectFirstProductInLastLineAsync()
        {
            var sel = _page.Locator("#linesTbody tr:last-child select.product-select");
            var value = await sel.Locator("option[value]:not([value=''])").First.GetAttributeAsync("value");
            await sel.SelectOptionAsync(value!);
        }

        /// <summary>最後一列 UOM 下拉可選值數。</summary>
        public async Task<int> LastLineUomOptionCountAsync()
        {
            var opts = _page.Locator("#linesTbody tr:last-child select.line-uom option[value]:not([value=''])");
            return await opts.CountAsync();
        }

        /// <summary>在最後一列選第一個 UOM，觸發 onUomChange（onchange）。</summary>
        public async Task SelectFirstUomInLastLineAsync()
        {
            var sel = _page.Locator("#linesTbody tr:last-child select.line-uom");
            var value = await sel.Locator("option[value]:not([value=''])").First.GetAttributeAsync("value");
            await sel.SelectOptionAsync(value!);
        }

        /// <summary>明細表存在（確認 _Form 已載入且含明細區）。</summary>
        public Task<bool> HasLinesTableAsync()
            => _page.Locator(LinesTbodySel).CountAsync().ContinueWith(t => t.Result > 0);

        /// <summary>
        /// 選擇指定 name 的下拉第一個有效選項（如供應商/客戶）。
        /// 「從XX單匯入」類按鈕多半要求先選定往來對象才會開啟 Modal，測試前置作業用。
        /// 無可選值時回傳 false（呼叫端可視為缺種子資料跳過）。
        /// </summary>
        public async Task<bool> SelectFirstOptionAsync(string selectName)
        {
            var sel = _page.Locator($"{PanelBodySel} select[name='{selectName}']");
            var opt = sel.Locator("option[value]:not([value=''])").First;
            if (await opt.CountAsync() == 0) return false;
            var value = await opt.GetAttributeAsync("value");
            await sel.SelectOptionAsync(value!);
            return true;
        }

        /// <summary>
        /// 點擊 data-action 觸發的「匯入」按鈕，等候對應 Bootstrap Modal 變為可見。
        /// 這條路徑專門守 execScripts 重建 &lt;script&gt; 的解析/執行失敗——一旦面板整段
        /// inline script 因語法錯誤（如變數重複宣告）而沒有執行，data-action 對應的全域
        /// 函式就不存在，按鈕點擊會完全無反應，Modal 不會出現（見 mfg-domain.md 外部
        /// 事故記錄：PurchaseOrder onSupplierChange 參數/區域變數同名 sel 重複宣告)。
        /// </summary>
        public async Task<bool> OpenImportModalAsync(string dataAction, string modalId, int timeoutMs = 4000)
        {
            await _page.ClickAsync($"{PanelBodySel} [data-action='{dataAction}']");
            try
            {
                await _page.WaitForSelectorAsync($"#{modalId}.show",
                    new PageWaitForSelectorOptions { Timeout = timeoutMs, State = WaitForSelectorState.Visible });
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }
}
