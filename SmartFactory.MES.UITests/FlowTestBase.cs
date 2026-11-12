using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// 流程（跨畫面、跨單據）E2E 測試的共用基底。
    ///
    /// 與 <see cref="CrudSmokeTestBase"/> 的差別：CRUD smoke test 自給自足（自己建、
    /// 自己刪，一個畫面之內），流程測試會推進單據狀態並消耗庫存，跑完之後資料庫**不再**
    /// 處於起始狀態。因此每個流程測試開跑前都先重新執行 <c>scripts/seed-e2e-fixture.sql</c>
    /// 把整組 E2E 資料歸零——那份 SQL 的數量與狀態欄位本來就是設計成覆寫回基準值的。
    ///
    /// 刻意重用同一份 SQL，而不是在測試裡另寫一套重置邏輯：兩份會漂移，而漂移的症狀是
    /// 「流程測試偶爾紅」，最難追。
    /// </summary>
    [Collection("UI Tests")]
    public abstract class FlowTestBase
    {
        protected readonly UiTestFixture _fixture;

        /// <summary>本輪流程測試蒐集到的 JS 錯誤；流程結束時一併斷言為空。</summary>
        protected readonly List<string> JsErrors = new();

        /// <summary>
        /// 流程中出現過的對話框訊息（confirm 與 alert 都收）。
        ///
        /// 非收不可：畫面的動作按鈕失敗時是用 <c>alert(result.message)</c> 把後端訊息吐出來的，
        /// 而流程測試必須自動接受對話框才能往下走——不收的話，後端明明回了
        /// 「第 N 行未指定來源儲位」這種精確訊息，測試卻只會在好幾步之後以
        /// 「狀態不對」的形式失敗，真因完全看不到。
        /// </summary>
        protected readonly List<string> DialogMessages = new();

        protected FlowTestBase(UiTestFixture fixture) => _fixture = fixture;

        // ════════════════════════════════════════════════════════════════════
        // 起始狀態重置
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 重新執行 E2E 種子，把 E2E- 那組資料歸零回基準值。
        /// 連線字串取自 <c>TestDbConnection</c>，未設定時退回
        /// <c>ConnectionStrings__DefaultConnection</c>（CI 的 job 層 env 已有這個）。
        /// </summary>
        protected static Task ResetFixtureAsync() => SeedFixture.ResetAsync();

        // ════════════════════════════════════════════════════════════════════
        // 頁面
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 開一個流程測試用分頁：已登入、自動接受 confirm 對話框、收集 JS 錯誤。
        ///
        /// 對話框在這裡統一處理而非各步驟自行掛載——流程中有多個 confirm（確認、揀料完成、
        /// 作廢…），各步驟各掛一個 handler 會讓同一個 dialog 被處理兩次而打掛 test host。
        /// </summary>
        protected async Task<IPage> NewFlowPageAsync()
        {
            var page = await _fixture.NewLoggedInPageAsync();
            page.PageError += (_, e) => JsErrors.Add(e);
            page.Dialog += (_, d) =>
            {
                DialogMessages.Add(d.Message);
                _ = d.AcceptAsync();
            };
            return page;
        }

        // ════════════════════════════════════════════════════════════════════
        // 共用操作
        // ════════════════════════════════════════════════════════════════════

        protected async Task GotoAsync(IPage page, string path)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}{path}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        /// <summary>
        /// 點某一列的面板按鈕（以 data-url 片段辨識），並等面板內容出現。
        /// </summary>
        protected static async Task OpenRowPanelAsync(
            IPage page, string rowText, string urlFragment, string readySelector, int timeoutMs = 10_000)
        {
            var row = page.Locator("table tbody tr", new PageLocatorOptions { HasText = rowText }).First;
            await row.Locator($"[data-url*='{urlFragment}']").First.ClickAsync();
            await page.WaitForSelectorAsync(readySelector, new PageWaitForSelectorOptions { Timeout = timeoutMs });
        }

        /// <summary>
        /// 點面板內的動作按鈕（確認／揀料完成／送審…），等後端回應後頁面安定。
        /// 這些按鈕走 fetch + toast，不是表單送出，故以 NetworkIdle 收斂而非等導航。
        /// </summary>
        protected static async Task ClickPanelActionAsync(IPage page, string urlFragment)
        {
            var btn = page.Locator($"[data-url*='{urlFragment}']").First;
            await btn.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            // 動作成功後畫面多半 reload；給 toast/reload 一點時間收斂。
            await page.WaitForTimeoutAsync(1200);
        }

        /// <summary>
        /// 點面板／列表上以 <c>data-action</c> 委派的按鈕（確認、匯入、送審…）。
        ///
        /// 與 <see cref="ClickPanelActionAsync"/> 並存而非取代：INV/MFG 的面板動作鈕是
        /// 以 <c>data-url</c> 指向端點，PUR/ORD/FIN 則是以 <c>data-action</c> 指定 handler
        /// 名稱、URL 寫在 handler 裡。兩種寫法在專案裡都是現況，流程測試兩種都要能點。
        /// </summary>
        protected static async Task ClickActionAsync(IPage page, string dataAction, int settleMs = 1500)
        {
            var btn = page.Locator($"[data-action='{dataAction}']").First;
            await btn.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(settleMs);
        }

        /// <summary>
        /// 按面板的存檔鈕，並在伺服端驗證擋下時**當場**把欄位錯誤吐出來。
        ///
        /// 非做不可：存檔被擋時面板只是重新渲染 _Form 並帶著欄位錯誤，測試若逕自往下走，
        /// 症狀會變成好幾步之後的「列表沒有這張單」或「匯入數量超過待處理量」——
        /// 離真因非常遠。這與 DocActionSmokeTestBase 撈驗證訊息是同一個理由。
        /// </summary>
        protected async Task SavePanelAsync(IPage page, string saveButtonSelector, string step)
        {
            // 送出前先問出表單的 action。存檔是否真的發生，唯一可靠的證據是那個 POST——
            // 「面板上沒有驗證訊息」不是證據：表單**根本沒送出**時面板同樣乾乾淨淨。
            // 2026-08-19 實測過這個破口：合計列重算讓版面在點擊瞬間長高，滑鼠落在頁尾
            // padding 上被吃掉，沒有 JS 錯誤也沒有驗證訊息，於是本方法回報成功，症狀被
            // 推遲到五步之後變成「匯入數量超過可出貨量」。
            var action = await page.EvaluateAsync<string?>(
                @"sel => { const b = document.querySelector(sel); return b && b.form ? b.form.getAttribute('action') : null; }",
                saveButtonSelector);

            var submitted = false;
            void OnResponse(object? _, IResponse r)
            {
                if (action is not null
                    && r.Request.Method == "POST"
                    && r.Url.Contains(action, StringComparison.OrdinalIgnoreCase))
                    submitted = true;
            }

            page.Response += OnResponse;
            try
            {
                await page.ClickAsync(saveButtonSelector);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1800);
            }
            finally
            {
                page.Response -= OnResponse;
            }

            var messages = await page.EvaluateAsync<string[]>(@"() => {
                const panel = document.querySelector('#sidePanelBody');
                if (!panel) return [];
                const out = [];
                panel.querySelectorAll('[data-valmsg-for]').forEach(el => {
                    const t = (el.textContent || '').trim();
                    if (t) out.push(el.getAttribute('data-valmsg-for') + '：' + t);
                });
                panel.querySelectorAll('.validation-summary-errors li, .field-validation-error').forEach(el => {
                    const t = (el.textContent || '').trim();
                    if (t && !out.some(x => x.endsWith(t))) out.push(t);
                });
                return out;
            }");

            Assert.True(messages.Length == 0,
                $"{step}：存檔被伺服端驗證擋下。\n面板上的驗證訊息：\n  - " +
                $"{string.Join("\n  - ", messages)}{DescribeDialogs()}");

            // 驗證訊息是空的，但 POST 也沒發生 → 表單沒送出（點擊被吃掉、按鈕被蓋住、
            // JS 沒綁上…）。這一條要在後面，因為伺服端擋下時上一條的訊息更有診斷價值。
            Assert.True(submitted,
                $"{step}：按下存檔後，往 {action ?? "(取不到 form action)"} 的 POST 一個都沒有發生"
                + "——表單根本沒有送出，而且面板上沒有任何驗證訊息。"
                + "常見成因：點擊落在按鈕以外的元素（版面在點擊瞬間位移）、按鈕被覆蓋、"
                + $"或 submit 事件被攔截。{DescribeDialogs()}");
        }

        /// <summary>
        /// 讀列表第一列的單號（第一個資料格的文字）。
        ///
        /// 單號由 DocNumberService 產生，測試事先不知道，只能存檔後回列表撈。一律搭配
        /// <c>sortOrder=id_desc</c> 使用——單號含日期前綴且逐號遞增，降冪排序後剛建的
        /// 那張必定在第一列。各單據列表的第一格有的是 <c>&lt;code&gt;</c>、有的是
        /// <c>&lt;a&gt;</c>，故取整格的 innerText 而非特定子元素。
        /// </summary>
        protected async Task<string> ReadFirstRowDocIdAsync(
            IPage page, string what, string? mustDifferFrom = null)
        {
            var cell = page.Locator("table tbody tr td").First;
            Assert.True(await cell.CountAsync() > 0,
                $"{what}：列表沒有任何資料列，取不到剛建立的單號。{DescribeDialogs()}");

            var id = (await cell.InnerTextAsync()).Trim();
            Assert.False(string.IsNullOrWhiteSpace(id),
                $"{what}：列表第一列的單號欄是空的。{DescribeDialogs()}");

            // 「第一列就是我剛建的那張」只在**列表確實多了一筆**時成立。存檔若靜默失敗，
            // 第一列會是別人留下的單，測試卻會拿它一路往下跑——2026-08-19 就是這樣把
            // 「存檔沒送出」偽裝成五步之後的「匯入數量超過可出貨量」。呼叫端把建立前
            // 的第一列傳進來，這裡就能當場戳破。
            Assert.False(mustDifferFrom is not null && id == mustDifferFrom,
                $"{what}：列表第一列還是建立前的那張單（{id}），代表這一步**沒有新增任何單據**。"
                + $"往上找存檔那一步的失敗原因，不要相信後續步驟的症狀。{DescribeDialogs()}");
            return id;
        }

        /// <summary>
        /// 讀列表第一列的單號，但列表可能是空的（乾淨資料庫第一次跑）。
        /// 專供「建立前先記一筆」用，故空列表回傳 null 而不是失敗。
        /// </summary>
        protected static async Task<string?> TryReadFirstRowDocIdAsync(IPage page)
        {
            var cell = page.Locator("table tbody tr td").First;
            if (await cell.CountAsync() == 0) return null;
            var id = (await cell.InnerTextAsync()).Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>
        /// 在匯入 Modal 的候選明細中，找出屬於指定來源單的那一列並填入本次數量。
        ///
        /// 必須鎖定單號而不是取第一列：這些 Modal 列的是「所有未收貨完的採購明細」，
        /// 而各 CRUD smoke test 每跑一輪都會留下新的採購單／訂單，第一列早就不是自己那張。
        /// </summary>
        protected static async Task FillImportQtyAsync(
            IPage page, string tbodySelector, string sourceDocId, string qtyInputClass, decimal qty)
        {
            var row = page.Locator($"{tbodySelector} tr", new PageLocatorOptions { HasText = sourceDocId }).First;
            await row.Locator($".{qtyInputClass}").First.FillAsync(qty.ToString());
        }

        /// <summary>
        /// 斷言某個代碼出現在查詢結果中。失敗訊息帶上畫面實際文字，省去重跑除錯。
        /// </summary>
        protected async Task AssertVisibleInListAsync(IPage page, string expected, string what)
        {
            var body = await page.InnerTextAsync("body");
            Assert.True(body.Contains(expected, StringComparison.OrdinalIgnoreCase),
                $"{what}：預期在畫面上看到「{expected}」。{DescribeDialogs()}\n實際內容：\n{Truncate(body, 1500)}");
        }

        // ════════════════════════════════════════════════════════════════════
        // 倉庫作業（/WarehouseOps）
        // ════════════════════════════════════════════════════════════════════
        //
        // 入庫上架與出庫下架是所有實體單據的共同下游：進貨、退貨、調撥、出貨都在這裡
        // 真正動到庫存。放在基底類別而非各流程測試內，是因為後續要補的流程（委外、
        // 銷貨退回…）走的是同一個畫面、同一組 selector。

        /// <summary>
        /// 入庫上架：搜尋待入庫明細 → 選取該單的那一列 → 指定倉庫／儲位 → 確認上架，並斷言上架成功。
        ///
        /// <para>必須看回應：失敗只會跳一個 3 秒就消失的 toast，不是 alert，<see cref="DialogMessages"/>
        /// 收不到。之前這裡只按不驗，品質隔離閘門擋下上架時測試照樣往下走，症狀變成好幾步之後的
        /// 「庫存餘量查無資料」。</para>
        /// </summary>
        protected async Task PutAwayAsync(
            IPage page, string docNo, string whCode, string locationCode, string step,
            string? lotNo = null, decimal? qty = null)
        {
            var (ok, message) = await TryPutAwayAsync(page, docNo, whCode, locationCode, step, lotNo, qty);
            Assert.True(ok,
                $"{step}：上架到 {whCode}/{locationCode} 被後端拒絕：{message}{DescribeDialogs()}");
        }

        /// <summary>
        /// 同 <see cref="PutAwayAsync"/>，但不斷言結果，回傳後端的成功與否與訊息——
        /// 給「預期被擋」的情境用（例如未品檢的貨上架到一般倉）。
        /// </summary>
        /// <param name="lotNo">批號；null＝不填（批號管制料號會被後端擋下）。</param>
        /// <param name="qty">本次上架量；null＝沿用畫面預帶的剩餘量。</param>
        protected async Task<(bool Ok, string Message)> TryPutAwayAsync(
            IPage page, string docNo, string whCode, string locationCode, string step,
            string? lotNo = null, decimal? qty = null)
        {
            await GotoAsync(page, "/WarehouseOps");

            await page.FillAsync("#inboundSearch", docNo);
            await ClickActionAsync(page, "loadPendingInbound", 800);

            var row = page.Locator("#inboundList tr", new PageLocatorOptions { HasText = docNo }).First;
            Assert.True(await row.CountAsync() > 0,
                $"{step}：待入庫清單找不到 {docNo}。待入庫的條件是單據 Status=Confirmed 且 " +
                $"QtyPutAway < StockQty——多半是上一步的「確認」沒有成立。{DescribeDialogs()}");
            await row.ClickAsync();
            await page.WaitForTimeoutAsync(500);

            // 先選倉庫（觸發 onWhSelect 篩選儲位），再選儲位。順序相反會被篩選清單覆寫掉。
            await page.SelectOptionAsync("#pa_whCodeSel", whCode);
            await page.WaitForTimeoutAsync(300);
            await page.SelectOptionAsync("#pa_locationCode", locationCode);
            if (lotNo != null) await page.FillAsync("#pa_lotNo", lotNo);
            if (qty != null)
                await page.FillAsync("#pa_qty", qty.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var response = await page.RunAndWaitForResponseAsync(
                () => page.ClickAsync("#pa_submit"),
                r => r.Url.Contains("/api/warehouse/put-away", StringComparison.OrdinalIgnoreCase)
                     && r.Request.Method == "POST",
                new PageRunAndWaitForResponseOptions { Timeout = 15_000 });
            var json = await response.JsonAsync();
            var success = response.Ok
                && json?.TryGetProperty("success", out var s) == true && s.GetBoolean();
            var message = json?.TryGetProperty("message", out var m) == true ? m.GetString() ?? "" : "";

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
            return (success, $"HTTP {response.Status} {message}");
        }

        /// <summary>
        /// 出庫下架：切到出庫頁籤 → 搜尋待出庫明細 → 選取該單的那一列 →
        /// 在指定儲位的可用庫存列填數量 → 確認下架。
        /// </summary>
        /// <param name="lotNo">指定從哪個批號揀；null＝該儲位第一列。</param>
        protected async Task PickAsync(
            IPage page, string docNo, string locationCode, decimal qty, string step, string? lotNo = null)
        {
            await GotoAsync(page, "/WarehouseOps");

            await page.ClickAsync("#tab-outbound");
            await page.WaitForTimeoutAsync(600);

            await page.FillAsync("#outboundSearch", docNo);
            await ClickActionAsync(page, "loadPendingOutbound", 800);

            var row = page.Locator("#outboundList tr", new PageLocatorOptions { HasText = docNo }).First;
            Assert.True(await row.CountAsync() > 0,
                $"{step}：待出庫清單找不到 {docNo}。待出庫的條件是單據 Status=Confirmed 且 " +
                $"QtyPutAway < StockQty——多半是上一步的「確認」沒有成立。{DescribeDialogs()}");
            await row.ClickAsync();
            await page.WaitForTimeoutAsync(800);

            var stockRow = page.Locator("#pickStockTable tr", new PageLocatorOptions { HasText = locationCode });
            if (lotNo != null) stockRow = stockRow.Filter(new LocatorFilterOptions { HasText = lotNo });
            stockRow = stockRow.First;
            Assert.True(await stockRow.CountAsync() > 0,
                $"{step}：可用庫存清單沒有 {locationCode} 的批次，無法揀料。{DescribeDialogs()}");
            await stockRow.Locator(".pick-qty").First.FillAsync(qty.ToString());

            await page.ClickAsync("#pk_submit");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
        }

        // ════════════════════════════════════════════════════════════════════
        // 委外收貨（/OutsourceReceipt）
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 委外收貨單：選委外採購單帶出明細 → 改收貨量 → 存檔 → 送審（未設簽核即核准）→ 確認，
        /// 並斷言確認後的單據狀態（預設 1＝已確認待上架；工序委外與簡化模式確認即過帳＝3）。
        ///
        /// <para>前提：種子每輪清掉該委外採購單上一輪的收貨單，所以篩這張委外採購單只會有剛建的這一張。</para>
        /// </summary>
        protected async Task<string> CreateAndConfirmOutsourceReceiptAsync(
            IPage page, string opo, decimal qty, string step, int expectedStatus = 1)
        {
            var listUrl = $"/OutsourceReceipt?searched=true&searchOpo={opo}&pageSize=50";

            await GotoAsync(page, listUrl);
            await page.ClickAsync("button[data-url$='/OutsourceReceipt/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #panelForm",
                new PageWaitForSelectorOptions { Timeout = 10_000 });

            await page.SelectOptionAsync("#sidePanelBody #opoSelect", opo);
            await page.WaitForSelectorAsync("#sidePanelBody #lineBody tr",
                new PageWaitForSelectorOptions { Timeout = 10_000 });
            await page.Locator("#sidePanelBody select[name='SupplierCode']").SelectOptionAsync("E2E-CO01");

            var lineRows = await page.Locator("#sidePanelBody #lineBody tr").CountAsync();
            Assert.True(lineRows == 1,
                $"{step}：選委外採購單 {opo} 後應帶出 1 行明細，實際 {lineRows} 行。{DescribeDialogs()}");
            await page.Locator("#sidePanelBody #lineBody .line-qty").First.FillAsync(qty.ToString());

            await SavePanelAsync(page, "#sidePanelBody #panelForm button[type='submit']", $"{step}：委外收貨單存檔");

            await GotoAsync(page, listUrl);
            var receiptId = await ReadFirstRowDocIdAsync(page, $"{step}：委外收貨單列表");

            await page.Locator($"[data-action='submitDoc'][data-id='{receiptId}']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            await GotoAsync(page, listUrl);
            var confirm = page.Locator($"[data-action='confirmDoc'][data-id='{receiptId}']");
            Assert.True(await confirm.CountAsync() > 0,
                $"{step}：{receiptId} 送審後沒有「確認」按鈕（未設簽核流程時送審應直接核准）。{DescribeDialogs()}");
            await confirm.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);

            var status = await SeedFixture.ScalarAsync<int>(
                $"SELECT \"Status\" FROM \"OutsourceReceipts\" WHERE \"OutsourceReceiptId\" = '{receiptId}'");
            Assert.True(status == expectedStatus,
                $"{step}：{receiptId} 確認後狀態應為 {expectedStatus}（1＝已確認待上架、3＝已過帳），實際 {status}。{DescribeDialogs()}");
            return receiptId;
        }

        /// <summary>把流程中出現過的對話框訊息附在失敗訊息裡——後端的業務錯誤都在這。</summary>
        protected string DescribeDialogs()
            => DialogMessages.Count == 0
                ? ""
                : $"\n流程中的對話框訊息（後端錯誤多半在此）：\n  - {string.Join("\n  - ", DialogMessages)}";

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..max] + $"…（其餘 {s.Length - max} 字省略）";
    }
}
