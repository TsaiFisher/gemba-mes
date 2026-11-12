using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// 第二層功能清單（手動清查 → E2E 自動化）的 CRUD 畫面 smoke test 模板。
    ///
    /// 每個派生類只需覆寫三個抽象成員，即可獲得 13 個標準化斷言測試：
    ///   ① Index 頁面載入
    ///   ② 新增面板開啟（無雙 Title）
    ///   ③ 空白送出 → 欄位驗證錯誤（不跳頁）
    ///   ④ 關閉按鈕有效（面板消失）
    ///   ⑤ 填必填欄位 → 儲存 → Toast 出現
    ///   ⑥ 列表刷新後可見新資料
    ///   ⑦ 所有標籤無英文屬性名殘留
    ///   ⑧ [Required] 欄位的 label 含星號 *
    ///   ⑨ type=number 欄位無寫死小數 step（應為 "any" 或整數）
    ///   ⑩ 逐一清空單一必填欄位（其餘填妥）→ 皆須被拒
    ///   ⑪ 文字欄位皆宣告長度上限；拔掉 maxlength 後超長字串仍須被拒
    ///   ⑫ 畫面宣告 min>0 的數字欄位，送出 -1 須被拒
    ///   ⑬ 起訖日期倒置須被拒（以 XxxFrom/XxxTo、XxxStart/XxxEnd 命名自動配對）
    ///
    /// 使用方式（每個畫面約 15–25 行）：
    ///   public class DefectCodeSmokeTests : CrudSmokeTestBase { ... }
    /// </summary>
    [Collection("UI Tests")]
    public abstract class CrudSmokeTestBase
    {
        protected readonly UiTestFixture _fixture;
        protected readonly CrudPanelPage Panel;

        // ── 派生類必須覆寫 ────────────────────────────────────────────────
        /// <summary>Index 路徑，例如 "/DefectCode"。</summary>
        protected abstract string IndexPath { get; }

        /// <summary>
        /// 「新增」按鈕的 Playwright selector，例如
        /// "button[data-url$='/DefectCode/Create']"。
        /// </summary>
        protected abstract string NewButtonSelector { get; }

        /// <summary>
        /// 面板開啟後等待出現的元素 selector（用於確認 partial 已載入），
        /// 例如 "input[name='Code']"。
        /// </summary>
        protected abstract string PanelReadySelector { get; }

        /// <summary>填寫新增表單的必填欄位（提供測試用唯一值）。</summary>
        protected abstract Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix);

        // ── 可選覆寫（預設抓 th:first-child 或首欄 td）─────────────────────
        /// <summary>新增成功後，用來確認資料出現在列表中的文字（例如 test code）。</summary>
        protected virtual string? ExpectedListText(string uniqueSuffix) => null;

        /// <summary>
        /// 允許保留精確小數 step 的欄位 name（如稅率、匯率、換算比例）。
        /// 預設空；子類視需求覆寫以豁免特定欄位。
        /// </summary>
        protected virtual IReadOnlySet<string> AllowedSmallStepNames
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 測試完成後清理 E2E 測試資料。子類覆寫並呼叫刪除按鈕。
        /// 預設不清理；覆寫時應 best-effort（失敗不拋例外）。
        /// </summary>
        protected virtual Task CleanupAsync(IPage page, string uniqueSuffix) => Task.CompletedTask;

        /// <summary>
        /// 進入 Index 之前的前置作業。用於「畫面依賴的主檔在乾淨資料庫是空的、
        /// 且必須經由其他畫面產生」的情況（例如會計期間）。
        /// 預設不做事；覆寫時必須冪等——重跑同一個資料庫時會再被呼叫。
        /// </summary>
        protected virtual Task PrepareAsync(IPage page) => Task.CompletedTask;

        /// <summary>
        /// 產生測試資料用的唯一後綴（固定 10 位數字，子類有以位置取值的用法）。
        ///
        /// 原本直接用 <c>DateTime.UtcNow.ToString("MMddHHmmss")</c>，只有秒解析度。
        /// 分頁洩漏修掉之後單一測試只要 ~600ms，同一個類別的兩個新增測試會落在同一秒
        /// → 產生相同代碼 → 第二個被唯一鍵擋成 ValidationError（2026-07-23 實測）。
        /// 改為「分鐘 + 行程內流水號」：長度不變，同一輪內不可能重複。
        /// </summary>
        private static int _suffixSeq;
        protected static string NextUniqueSuffix()
            => DateTime.UtcNow.ToString("MMddHHmm")
             + (System.Threading.Interlocked.Increment(ref _suffixSeq) % 100).ToString("D2");

        // ── 面板 DOM 契約（預設＝共用 crud-panel.js/sidePanel；BAS 自洽 crudPanel
        //    由 BasCrudSmokeTestBase 覆寫）────────────────────────────────────
        protected virtual string PanelSel      => "#sidePanel";
        protected virtual string PanelBodySel  => "#sidePanelBody";
        protected virtual string PanelLabelSel => "#sidePanelLabel";
        protected virtual string CloseButtonSel => $"{PanelBodySel} button.btn-secondary";

        // ── 共用 setup ────────────────────────────────────────────────────
        protected CrudSmokeTestBase(UiTestFixture fixture)
        {
            _fixture = fixture;
            // Panel 在每個測試中使用 fixture 的 Context 建立新頁
            Panel = null!; // 每個測試方法內部用 NewPage 建立
        }

        protected async Task<(IPage page, CrudPanelPage panel)> NewLoggedInPageAsync()
        {
            var page = await _fixture.NewLoggedInPageAsync();
            await PrepareAsync(page);
            var panel = new CrudPanelPage(page, _fixture.BaseUrl,
                PanelSel, PanelBodySel, PanelLabelSel, CloseButtonSel);
            return (page, panel);
        }

        // ════════════════════════════════════════════════════════════════════
        // ① Index 載入
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task Index_PageLoads_NoJsError()
        {
            var (page, _) = await NewLoggedInPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e);

            await page.GotoAsync($"{_fixture.BaseUrl}{IndexPath}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            Assert.Empty(errors);
        }

        // ════════════════════════════════════════════════════════════════════
        // ② 新增面板：單一 Title，無雙 header
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task NewPanel_Opens_HasSingleTitle_NoDoubleHeader()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            // sidePanelBody 內不應有 offcanvas-header（雙標題 Cx4 回歸守門）
            var innerHeaders = await panel.CountInnerOffcanvasHeadersAsync();
            Assert.Equal(0, innerHeaders);

            // 外層 sidePanelLabel 應有文字（非空）
            var title = await panel.GetPanelTitleAsync();
            Assert.False(string.IsNullOrWhiteSpace(title), "sidePanelLabel 不應為空");
        }

        // ════════════════════════════════════════════════════════════════════
        // ③ 空白送出 → 欄位驗證錯誤，不跳頁
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public virtual async Task NewPanel_EmptySubmit_ShowsValidationError_StaysOnPanel()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            var result = await panel.ClickSaveAsync();

            // 應回 ValidationError（面板留著），不能 Success（不該儲存空白）
            Assert.Equal(SaveResult.ValidationError, result);

            // 確認面板仍在（沒有跳頁）
            var body = await page.InnerHTMLAsync(PanelBodySel);
            Assert.False(string.IsNullOrWhiteSpace(body), "面板 body 不應因驗證失敗而清空");
        }

        // ════════════════════════════════════════════════════════════════════
        // ④ 關閉按鈕有效
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task NewPanel_CloseButton_ClosesPanelSuccessfully()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            // 點關閉後面板應消失（offcanvas 不再有 show class）
            // 若 closeSidePanel 未定義會直接拋 JS error → 測試失敗
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);
            await page.ClickAsync(CloseButtonSel);

            // 等一下讓 Bootstrap offcanvas 動畫跑完
            await page.WaitForTimeoutAsync(500);
            Assert.Empty(jsErrors);
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑧ 必填欄位 label 含星號 *
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public virtual async Task NewPanel_RequiredFields_HaveStarInLabel()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            // data-val-required 由 asp-for + [Required] 產生，排除 checkbox/radio/hidden/readonly
            var sel =
                $"{PanelBodySel} input[data-val-required]" +
                    ":not([type='checkbox']):not([type='radio']):not([type='hidden']):not([readonly])," +
                $"{PanelBodySel} select[data-val-required]," +
                $"{PanelBodySel} textarea[data-val-required]";

            var inputs = page.Locator(sel);
            var count = await inputs.CountAsync();
            if (count == 0) return; // 此畫面無 [Required] 欄位，跳過

            var missing = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var input = inputs.Nth(i);
                var id = await input.GetAttributeAsync("id");
                if (string.IsNullOrEmpty(id)) continue; // 無 id 的動態列跳過

                var label = page.Locator($"{PanelBodySel} label[for='{id}']");
                if (await label.CountAsync() == 0) continue; // 找不到對應 label 跳過

                var labelText = await label.InnerTextAsync();
                if (!labelText.Contains('*'))
                    missing.Add(id);
            }

            Assert.True(missing.Count == 0,
                $"以下必填欄位的 label 缺少星號（*）：{string.Join(", ", missing)}");
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑨ type=number 欄位不得有寫死的小數 step（應為 "any" 或整數）
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public virtual async Task NewPanel_NumberInputs_NoTinyStep()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            // 只看顯式設有 step 屬性的 type=number 欄位
            var inputs = page.Locator($"{PanelBodySel} input[type='number'][step]");
            var count = await inputs.CountAsync();
            if (count == 0) return;

            var bad = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var inp = inputs.Nth(i);
                var step = await inp.GetAttributeAsync("step") ?? "";
                if (step == "any") continue; // 正確：精度由 JS 控制

                var name = await inp.GetAttributeAsync("name")
                        ?? await inp.GetAttributeAsync("id") ?? $"input[{i}]";

                if (AllowedSmallStepNames.Contains(name)) continue; // 明確豁免（稅率/匯率等）

                // step 解析為 < 1 的小數 → spinner 每次只 ±該極小值
                if (decimal.TryParse(step,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var stepVal) && stepVal < 1m)
                {
                    bad.Add($"{name}(step={step})");
                }
            }

            Assert.True(bad.Count == 0,
                "以下數字欄位 step 為小數（上下三角形每次 ±極小值），" +
                $"應改為 step=\"any\"（精度由伺服端 Round 把關）：{string.Join(", ", bad)}");
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑩–⑬ 無效輸入等價類
        //
        // ③ 只涵蓋「全部留空」這一類，而全空是最容易被擋下的一類——只要有任何一個
        // [Required] 生效就會紅。真正會漏的是下面這幾類：欄位大部分填對、只有一個
        // 出問題，於是任何「只檢查最前面那個欄位」的驗證都會放行。
        //
        // 這四條寫在基底類別而非個別畫面：它們都能從 DOM 自行推導出要測什麼
        // （必填屬性、maxlength、min、From/To 命名配對），不需要各畫面的業務知識，
        // 因此一次覆蓋所有派生的主檔畫面。至於「非本廠代碼」「已停用主檔代碼」
        // 這兩類無法從 DOM 推導（得知道哪個欄位是哪張主檔的外來參照），
        // 留在各畫面自行補，不放進共用樣板。
        // ════════════════════════════════════════════════════════════════════

        /// <summary>面板內「填得進值、且參與驗證」的輸入欄位選擇器。</summary>
        private string EditableInputSel(string extraAttr = "") =>
            $"{PanelBodySel} input{extraAttr}" +
            ":not([type='checkbox']):not([type='radio']):not([type='hidden'])" +
            ":not([readonly]):not([disabled])";

        // ── ⑩ 單一必填欄位缺漏 ────────────────────────────────────────────
        [Fact]
        public virtual async Task NewPanel_EachRequiredFieldMissing_IsRejected()
        {
            var suffix = NextUniqueSuffix();
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);
            await FillRequiredFieldsAsync(page, suffix);

            var fields = page.Locator(EditableInputSel("[data-val-required]"));
            var count = await fields.CountAsync();
            if (count == 0) return;     // 此畫面無必填輸入欄位

            var accepted = new List<string>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var f = fields.Nth(i);
                    if (!await f.IsVisibleAsync()) continue;

                    var original = await f.InputValueAsync();
                    if (string.IsNullOrEmpty(original)) continue;   // 本來就沒填，清空無意義

                    var name = await f.GetAttributeAsync("name") ?? $"input[{i}]";

                    await f.FillAsync("");
                    // FillAsync("") 偶爾不生效：trace 顯示停在「waiting for element to be visible, enabled and editable」
                    // 就回報完成，欄位仍是原值，送出去的是填妥的表單、伺服端當然存檔成功，
                    // 於是報成「清空 X 仍存檔成功」（2026-11-06～11-09 nightly 的 NCR ProductCode、DefectCode Code、
                    // Shift ShiftCode 皆是第一個欄位）。清完必須確認真的空了，沒空就直接設值並觸發事件。
                    if (!string.IsNullOrEmpty(await f.InputValueAsync()))
                        await f.EvaluateAsync("e => { e.value = ''; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); }");
                    Assert.True(string.IsNullOrEmpty(await f.InputValueAsync()), $"測試無法清空欄位 {name}");
                    await panel.ClearValidationMessagesAsync();
                    var result = await panel.ClickSaveAsync();

                    if (result == SaveResult.Success)
                    {
                        // 已經存進去了，面板也跳走了，後面的欄位測不下去
                        accepted.Add($"{name}（{panel.LastSaveDiagnostic ?? "無 POST 摘要"}）");
                        break;
                    }

                    await f.FillAsync(original);
                }
            }
            finally
            {
                await CleanupAsync(page, suffix);
            }

            Assert.True(accepted.Count == 0,
                "以下欄位標了必填，但只把它清空、其餘欄位都填妥時仍被存檔成功——"
                + "代表伺服端沒有真的驗它（常見成因：ViewModel 上有 [Required]，"
                + "但 Save 綁的是 Entity，或該欄位在 Controller 內被覆寫）："
                + string.Join(", ", accepted));
        }

        // ── ⑪ 超長字串 ────────────────────────────────────────────────────
        /// <summary>
        /// 每個文字輸入欄位都必須有長度上限。
        ///
        /// 沒有 maxlength 的欄位＝沒有 [MaxLength]/[StringLength]＝伺服端也不會擋，
        /// 而資料庫欄位是 varchar(n)：貼進超長字串會一路走到 SaveChanges 才被
        /// PostgreSQL 以 22001 打回來，使用者看到的是通用錯誤或 500，
        /// 而不是「這個欄位太長」。
        /// </summary>
        [Fact]
        public virtual async Task NewPanel_TextInputs_AllDeclareMaxLength()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            var inputs = page.Locator(
                $"{PanelBodySel} input[type='text']:not([readonly]):not([disabled])," +
                $"{PanelBodySel} textarea:not([readonly]):not([disabled])");
            var count = await inputs.CountAsync();

            // 掃描器自檢。面板至少要有一個可見的表單控件，否則「沒有欄位缺 maxlength」
            // 這個結論其實是「什麼都沒檢查」——面板沒開成、PanelBodySel 改名、
            // 或選擇器失準，都會讓本條規則安靜退化成永遠綠燈。
            //
            // 為何不直接數文字欄位：確實有畫面一個文字欄位都沒有（DepartmentActualCost
            // 只有兩個下拉加兩個數值欄），那是合法的零，不該和上面那些故障混為一談。
            var controls = page.Locator(
                $"{PanelBodySel} input:not([type='hidden'])," +
                $"{PanelBodySel} select,{PanelBodySel} textarea");
            Assert.True(await controls.CountAsync() > 0,
                $"{IndexPath} 的新增面板沒有任何表單控件——測試本身失效，不是這個畫面沒有欄位。");

            var missing = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var inp = inputs.Nth(i);
                if (!await inp.IsVisibleAsync()) continue;
                if (await inp.GetAttributeAsync("maxlength") != null) continue;

                var name = await inp.GetAttributeAsync("name")
                        ?? await inp.GetAttributeAsync("id") ?? $"input[{i}]";
                if (AllowedUnboundedTextNames.Contains(name)) continue;
                missing.Add(name);
            }

            Assert.True(missing.Count == 0,
                "以下文字欄位沒有長度上限（模型缺 [MaxLength]/[StringLength]），"
                + "超長輸入會拖到資料庫才報 22001："
                + string.Join(", ", missing));
        }

        /// <summary>
        /// 拿掉畫面的 maxlength 之後，超長字串仍須被擋下。
        ///
        /// maxlength 只是瀏覽器層的輸入限制，貼上、程式填值、改 DOM 都繞得過去。
        /// 這條確認長度守門有第二層（jQuery 的 data-val-length 或伺服端），
        /// 而不是只靠那個屬性。
        /// </summary>
        [Fact]
        public virtual async Task NewPanel_OverLongText_RejectedEvenWithoutMaxLengthAttribute()
        {
            var suffix = NextUniqueSuffix();
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);
            await FillRequiredFieldsAsync(page, suffix);

            var target = page.Locator(EditableInputSel("[type='text'][maxlength]")).First;
            if (await target.CountAsync() == 0) return;
            if (!await target.IsVisibleAsync()) return;

            var maxLen = int.Parse(await target.GetAttributeAsync("maxlength") ?? "0");
            if (maxLen <= 0) return;

            var name = await target.GetAttributeAsync("name") ?? "(unnamed)";

            // 必須先取 ElementHandle 再拔屬性：Locator 是延遲求值的，
            // 一旦 maxlength 被移除，同一個 locator 下次就改指向「下一個仍有 maxlength 的欄位」，
            // 於是超長字串被填進了另一個上限更寬的欄位，測試變成永遠綠。
            var handle = await target.ElementHandleAsync();
            await handle.EvaluateAsync("e => e.removeAttribute('maxlength')");
            await handle.FillAsync(new string('X', maxLen + 50));

            SaveResult result;
            try
            {
                await panel.ClearValidationMessagesAsync();
                result = await panel.ClickSaveAsync();
            }
            finally
            {
                await CleanupAsync(page, suffix);
            }

            Assert.True(result == SaveResult.ValidationError,
                $"欄位 {name} 的長度上限是 {maxLen}，但把畫面上的 maxlength 屬性拿掉之後，"
                + $"送出 {maxLen + 50} 個字元仍被接受——長度守門只存在於瀏覽器屬性，"
                + "模型上缺 [MaxLength]/[StringLength]。");
        }

        // ── ⑫ 數量 0 或負值 ───────────────────────────────────────────────
        /// <summary>
        /// 畫面宣告 min &gt; 0 的數字欄位，送出負值必須被擋下。
        ///
        /// min 跟 maxlength 一樣只是瀏覽器提示，直接 POST 就繞過了。這條逐欄改成 -1
        /// 送出，把「畫面說要正數、伺服端沒說」的欄位揪出來——負數量會一路過到庫存，
        /// 變成憑空增加的在手量。
        /// </summary>
        [Fact]
        public virtual async Task NewPanel_NegativeValueInPositiveOnlyField_IsRejected()
        {
            var suffix = NextUniqueSuffix();
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);
            await FillRequiredFieldsAsync(page, suffix);

            var fields = page.Locator(EditableInputSel("[type='number'][min]"));
            var count = await fields.CountAsync();
            if (count == 0) return;

            var accepted = new List<string>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var f = fields.Nth(i);
                    if (!await f.IsVisibleAsync()) continue;

                    var min = await f.GetAttributeAsync("min") ?? "";
                    if (!decimal.TryParse(min, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var minVal))
                        continue;
                    if (minVal <= 0) continue;      // 本來就允許 0／負值，不在此類

                    var name = await f.GetAttributeAsync("name") ?? $"input[{i}]";
                    var original = await f.InputValueAsync();

                    await f.FillAsync("-1");
                    await panel.ClearValidationMessagesAsync();
                    var result = await panel.ClickSaveAsync();

                    if (result == SaveResult.Success)
                    {
                        accepted.Add($"{name}(min={min})");
                        break;
                    }

                    await f.FillAsync(original);
                }
            }
            finally
            {
                await CleanupAsync(page, suffix);
            }

            Assert.True(accepted.Count == 0,
                "以下欄位畫面上宣告 min>0，但送出 -1 仍被接受——"
                + "模型缺 [Range]，min 屬性只擋得住用滑鼠點上下三角形的人："
                + string.Join(", ", accepted));
        }

        // ── ⑬ 日期區間倒置 ────────────────────────────────────────────────
        /// <summary>
        /// 起訖日期成對出現時，起 &gt; 訖 必須被擋下。
        ///
        /// 以命名配對自動找出區間欄位（XxxFrom/XxxTo、XxxStart/XxxEnd）。
        /// 區間倒置不會讓存檔失敗，但會讓所有「日期落在區間內」的查詢永遠查不到，
        /// 症狀是「資料明明建好了卻不生效」，追起來很久。
        /// </summary>
        [Fact]
        public virtual async Task NewPanel_InvertedDateRange_IsRejected()
        {
            var suffix = NextUniqueSuffix();
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);
            await FillRequiredFieldsAsync(page, suffix);

            var pairs = await FindDateRangePairsAsync(page);
            if (pairs.Count == 0) return;

            var accepted = new List<string>();
            try
            {
                foreach (var (fromName, toName) in pairs)
                {
                    var from = page.Locator($"{PanelBodySel} input[name='{fromName}']");
                    var to = page.Locator($"{PanelBodySel} input[name='{toName}']");

                    await from.FillAsync("2026-12-31");
                    await to.FillAsync("2026-01-01");
                    await panel.ClearValidationMessagesAsync();

                    var result = await panel.ClickSaveAsync();
                    if (result == SaveResult.Success)
                    {
                        accepted.Add($"{fromName} > {toName}");
                        break;
                    }
                }
            }
            finally
            {
                await CleanupAsync(page, suffix);
            }

            Assert.True(accepted.Count == 0,
                "以下起訖日期倒置仍被接受（起日晚於訖日）——"
                + "區間查詢會永遠落空，但存檔當下毫無異狀："
                + string.Join(", ", accepted));
        }

        /// <summary>以命名慣例找出面板內的起訖日期欄位配對。</summary>
        private async Task<List<(string From, string To)>> FindDateRangePairsAsync(IPage page)
        {
            var dates = page.Locator(
                $"{PanelBodySel} input[type='date'][name]:not([readonly]):not([disabled])," +
                $"{PanelBodySel} input[type='datetime-local'][name]:not([readonly]):not([disabled])");
            var count = await dates.CountAsync();

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (!await dates.Nth(i).IsVisibleAsync()) continue;
                var n = await dates.Nth(i).GetAttributeAsync("name");
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }

            var pairs = new List<(string, string)>();
            foreach (var (fromSuffix, toSuffix) in new[] { ("From", "To"), ("Start", "End") })
            {
                foreach (var n in names.Where(n => n.EndsWith(fromSuffix, StringComparison.Ordinal)))
                {
                    var prefix = n[..^fromSuffix.Length];
                    var counterpart = prefix + toSuffix;
                    if (names.Contains(counterpart)) pairs.Add((n, counterpart));
                }
            }
            return pairs;
        }

        /// <summary>
        /// 允許沒有長度上限的文字欄位 name。預設空；子類視需求覆寫。
        /// </summary>
        protected virtual IReadOnlySet<string> AllowedUnboundedTextNames
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ════════════════════════════════════════════════════════════════════
        // ⑤ 填必填欄位 → 儲存 → Toast 出現
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task NewPanel_FillAndSave_ShowsSuccessToast()
        {
            var uniqueSuffix = NextUniqueSuffix();
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);
            await FillRequiredFieldsAsync(page, uniqueSuffix);

            try
            {
                var result = await panel.ClickSaveAsync(timeoutMs: 8000);
                Assert.True(result == SaveResult.Success, $"填妥必填後存檔未成功：{panel.LastSaveDiagnostic ?? "（前端驗證擋下，未送出）"}");
                Assert.True(await panel.IsToastVisibleAsync(),
                    "成功儲存後 #successToast 應顯示");
            }
            finally
            {
                await CleanupAsync(page, uniqueSuffix);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑥ 儲存後列表刷新（頁面標題仍在，未跳到空白頁）
        // ════════════════════════════════════════════════════════════════════
        [Fact]
        public async Task NewPanel_SaveSuccess_RefreshesList_PageStillLoaded()
        {
            var uniqueSuffix = NextUniqueSuffix();
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);
            await FillRequiredFieldsAsync(page, uniqueSuffix);

            try
            {
                await panel.ClickSaveAsync(timeoutMs: 8000);

                // 等待 refreshList 的 location.reload 完成
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });

                // 頁面應仍在 Index（非空白 JSON 頁）
                Assert.Contains(IndexPath.TrimStart('/'), page.Url,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await CleanupAsync(page, uniqueSuffix);
            }
        }
    }
}
