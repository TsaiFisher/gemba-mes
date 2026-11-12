using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.QA
{
    /// <summary>
    /// 手冊 Q&amp;A 對話畫面（S-7）的瀏覽器層守門。
    ///
    /// <para><b>與既有三層的分工，而且分工是刻意的。</b>
    /// `QaCitationPresenterTests`（單元）驗分類函式；`QaAssistantScreenTests`（整合）
    /// 驗權限閘門、AntiForgery、前置檢查、以及「HTML 裡沒有 inline `on*=`」；
    /// `QaAssistantLiveAskTests`（`[LiveOllamaFact]`）真的問一題。
    /// **中間有一段三者都碰不到**：那些字串到了瀏覽器裡被畫成什麼形狀。</para>
    ///
    /// <para>整合測試能斷言「沒有 inline handler」，但那只證明了 CSP 不會擋；
    /// 它證不到 `data-action-submit="qaAsk"` 真的接得上 `window.qaAsk`——
    /// 名字打錯、`@section Scripts` 沒帶 nonce、dispatcher 沒載入，三者的症狀
    /// 都是**點了沒反應、沒有錯誤訊息**，而 HTML 檢查全部照樣通過。</para>
    ///
    /// <para><b>不需要模型，這是刻意的。</b>提問的 POST 由 <see cref="QaAssistantPage.StubAskAsync"/>
    /// 攔下來回合成資料。真的問一題要五十秒 CPU 推論＋Ollama＋嵌入索引，那條路
    /// 已經有專門的測試走過；把它搬到這一層，只會讓這幾支在 CI 上被跳過而失去效力
    /// （同 `ScheduleGanttTests` 的合成資料那一支）。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class QaAssistantUiTests
    {
        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public QaAssistantUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        /// <summary>
        /// 三種引文各一則。刻意讓 <c>rules/*</c> 那則的 <c>url</c> 是空字串——
        /// 伺服端就是這樣送的，畫面必須據此決定「不畫連結」，而不是連到首頁。
        /// </summary>
        private static object ThreeKindCitations() => new
        {
            body = "採購付款時以公司買入外幣的匯率計算。",
            isNoAnswer = false,
            elapsedMs = 1234,
            model = "stub",
            citations = new object[]
            {
                new { kind = "Manual", pageCode = "bas/currency.html", headingPath = "匯率維護",
                      url = "/docs/user/bas/currency.html", text = "" },
                new { kind = "Rule", pageCode = "rules/currency-d151520a.html", headingPath = "匯率類型選擇",
                      url = "", text = "採購付款時，系統以公司買入外幣的匯率計算。" },
                new { kind = "Unverifiable", pageCode = "pur/nonexistent.html", headingPath = "",
                      url = "", text = "" },
            },
        };

        /// <summary>
        /// **委派真的接得上**：送出不會走原生表單送出（頁面不跳轉），
        /// 而且 dispatcher 沒有印出「找不到全域函式」。
        ///
        /// <para>後者是 `data-action.js` 唯一的失敗訊號，而且只有 `console.warn` 一行——
        /// 沒有例外、沒有紅字。少了這一支，`window.qaAsk` 改名或 nonce 掉了，
        /// 症狀是使用者按送出後整頁重載、輸入框清空、什麼也沒發生。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 送出提問走委派_不會原生送出表單()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var warnings = new List<string>();
            var pageErrors = new List<string>();
            page.Console += (_, m) => { if (m.Type is "warning" or "error") warnings.Add(m.Text); };
            page.PageError += (_, e) => pageErrors.Add(e);

            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.StubAskAsync(ThreeKindCitations());

            var urlBefore = page.Url;
            Assert.Equal(1, await qa.EmptyPrompt.CountAsync());

            await qa.AskAsync("採購付款要用買入匯率還是賣出匯率");
            await qa.LastBubble.Locator("details, a, span.small").First
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.Equal(urlBefore, page.Url);

            // 空狀態提示必須被移掉。留著的話第一則問答會疊在「還沒有對話」上面。
            Assert.Equal(0, await qa.EmptyPrompt.CountAsync());

            var dispatcherWarnings = warnings.Where(w => w.Contains("[data-action]")).ToList();
            Assert.True(dispatcherWarnings.Count == 0,
                "data-action dispatcher 找不到對應的全域函式：\n  " +
                string.Join("\n  ", dispatcherWarnings) +
                "\n（qaAsk／qaCancel／qaRebuild 有一個沒掛上 window，或 @section Scripts 的 nonce 掉了。" +
                "症狀是按下去整頁重載而不是提問。）");

            Assert.True(pageErrors.Count == 0,
                $"對話畫面有未攔截的 JS 錯誤：\n  {string.Join("\n  ", pageErrors)}");
        }

        /// <summary>
        /// **三種引文各自的形狀**（定案 #47）。這是本檔最主要的一支。
        ///
        /// <list type="number">
        ///   <item><description><b>Rule</b> → 可摺疊的就地展開，**不得有 `&lt;a&gt;`**。
        ///     磁碟上沒有 `rules/*.html`，而且刻意不產（`wwwroot` 在 `UseAuthentication`
        ///     之前，是匿名可讀的）。畫成連結就是一個 404。</description></item>
        ///   <item><description><b>Manual</b> → 真連結，另開分頁。這是既有行為，
        ///     定案 #47 只動擷取物那一種，它不該被波及。</description></item>
        ///   <item><description><b>Unverifiable</b> → 純文字，**不展開也不連結**。
        ///     展開一個空白區塊會把「模型編了頁碼」偽裝成「這頁剛好沒內容」。</description></item>
        /// </list>
        ///
        /// <para>⚠️ 這三支之所以要在瀏覽器裡驗：伺服端送的是同一個陣列，分支全在
        /// `renderCitations` 的 `c.kind === 'Rule'` 那幾行。列舉一旦掉回數字
        /// （少了 `JsonStringEnumConverter`），比對永遠不相等，**三種全部掉進第三支**——
        /// 正常的手冊連結會被整批標成無法查證，而伺服端的測試一個都不會紅。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 三種引文各自畫成正確的形狀()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.StubAskAsync(ThreeKindCitations());

            await qa.AskAsync("匯率");
            var bubble = qa.LastBubble;
            await bubble.Locator("details").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            // ── Rule：details + summary，內容在裡面，沒有連結 ──
            var details = bubble.Locator("details");
            Assert.Equal(1, await details.CountAsync());

            var summary = await details.Locator("summary").InnerTextAsync();
            Assert.Contains("rules/currency-d151520a.html", summary);
            Assert.Contains("匯率類型選擇", summary);

            Assert.Equal(0, await details.Locator("a").CountAsync());

            // 展開內容必須真的在 DOM 裡（伺服端隨答案一起送），不是點開才去要。
            // ⚠️ 這裡要用 TextContent 不是 InnerText：details 收合時內容不參與排版，
            //    InnerText 一律回空字串——會把「內容根本沒送來」與「還沒點開」混為一談。
            var expanded = await details.Locator("div").TextContentAsync();
            Assert.Contains("買入外幣", expanded ?? "");

            // 點開之後看得到，才算真的可就地展開（而不是一個永遠打不開的殼）。
            await details.Locator("summary").ClickAsync();
            Assert.True(await details.Locator("div").IsVisibleAsync(),
                "點開摺疊區塊後內容仍不可見——展開的是一個空殼。");

            // ── Manual：真連結，另開分頁 ──
            var link = bubble.Locator("a[href]");
            Assert.Equal(1, await link.CountAsync());
            Assert.Equal("/docs/user/bas/currency.html", await link.GetAttributeAsync("href"));
            Assert.Equal("_blank", await link.GetAttributeAsync("target"));

            // ── Unverifiable：純文字，既不是連結也不是摺疊區塊 ──
            //
            // ⚠️ 不能只用 `> div.mb-2`：區塊最上面那個「參考來源」標籤也帶 mb-2，
            //    會多算一列。這裡改用「裡面有畫出東西」當條件——每一則引文必定被畫成
            //    details／a／span 三者之一，而標籤只有文字節點。
            var citationRows = bubble.Locator("div.border-top > div.mb-2:has(details, a, span)");
            Assert.Equal(3, await citationRows.CountAsync());

            var third = citationRows.Nth(2);
            Assert.Equal(0, await third.Locator("a, details").CountAsync());
            Assert.Contains("pur/nonexistent.html", await third.InnerTextAsync());

            _out.WriteLine($"摺疊標題：{summary}");
            _out.WriteLine($"無法查證那列：{await third.InnerTextAsync()}");
        }

        /// <summary>
        /// **模型輸出一律當文字，不當 HTML**（規格 §9.4 I2、對話型畫面契約 §12.3）。
        ///
        /// <para>這是 S-7 最高風險的一項，而且**只有瀏覽器驗得到**：伺服端回傳的
        /// JSON 字串長什麼樣，整合測試看得見；那個字串被 `textContent` 還是
        /// `innerHTML` 吃掉，只有 DOM 知道。</para>
        ///
        /// <para>三個位置都要驗，因為它們在畫面裡是三段不同的程式碼：
        /// 答案本文、摺疊區塊的標題（`summary`）、摺疊區塊的內容。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 模型輸出與引文一律以文字呈現_不得注入節點()
        {
            const string Payload = "<img src=x onerror=\"window.__xss=1\">";

            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.StubAskAsync(new
            {
                body = "答案本文 " + Payload,
                isNoAnswer = false,
                elapsedMs = 1,
                model = "stub",
                citations = new object[]
                {
                    new { kind = "Rule", pageCode = "rules/x-00000000.html",
                          headingPath = "標題 " + Payload, url = "", text = "內容 " + Payload },
                },
            });

            await qa.AskAsync("注入測試");
            await qa.LastBubble.Locator("details").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.Equal(0, await qa.Log.Locator("img").CountAsync());
            Assert.Equal(0, await qa.Log.Locator("script").CountAsync());

            var pwned = await page.EvaluateAsync<bool>("() => window.__xss === 1");
            Assert.False(pwned, "引文或答案被當成 HTML 執行了——textContent 被換成 innerHTML。");

            // 反面也要成立：不是「被過濾掉」而是「原樣顯示成文字」。
            // sanitize 掉會讓使用者看不到模型實際說了什麼，那是另一種壞法。
            //
            // ⚠️ 三個位置分開比對。只讀整顆氣泡的話，答案本文帶著 payload 就足以讓
            //    斷言成立，而摺疊區塊裡的兩段即使空的也照樣通過——那正是要抓的東西。
            var details = qa.LastBubble.Locator("details");

            // 答案本文是氣泡的第一個子節點，而且是**文字節點**（`waiting.textContent = d.body`），
            // 選擇器選不到它——引文區塊與耗時是之後才 append 上去的元素。
            var answerText = await qa.LastBubble.EvaluateAsync<string>(
                "el => el.childNodes[0] ? el.childNodes[0].textContent : ''");
            Assert.Contains(Payload, answerText);
            Assert.Contains(Payload, await details.Locator("summary").InnerTextAsync());
            Assert.Contains(Payload, await details.Locator("div").TextContentAsync() ?? "");
        }

        /// <summary>
        /// **等待中可取消，取消後回得去**（對話型畫面契約 §12.5）。
        ///
        /// <para>定案模型實測 47～53 秒／題（CPU 推論），「按鈕轉個圈」在這個量級上不夠，
        /// 所以送出後立即鎖住輸入並顯示取消鍵。取消鍵的顯示靠 `u-display-none` 的
        /// **classList 增刪**——本專案記錄有案的坑是改用 `style.display` 切換，
        /// 那會與 utility class 打架，症狀是元素**永久隱藏**（點不到取消，
        /// 那顆 CPU 就要一路跑完）。</para>
        ///
        /// <para>取消之後答案不得再落地：那是「取消」與「隱藏等待動畫」的差別。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 等待中可取消_取消後輸入解鎖且答案不落地()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.StubAskAsync(new
            {
                body = "這個答案不該出現在畫面上",
                isNoAnswer = false,
                elapsedMs = 1,
                model = "stub",
                citations = Array.Empty<object>(),
            }, delayMs: 5_000);

            await qa.AskAsync("慢慢想");

            await qa.Cancel.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000,
            });
            Assert.True(await qa.IsWaitingAsync(),
                "送出後沒有進入等待狀態（輸入未鎖住或取消鍵沒現身）。");

            await qa.Cancel.ClickAsync();

            // 回到可輸入狀態。這裡等的是 class，不是 style——切錯機制的話這裡會逾時。
            await qa.Cancel.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 10_000,
            });
            Assert.False(await qa.Question.IsDisabledAsync(), "取消後輸入框仍然是鎖住的。");
            Assert.False(await qa.Send.IsDisabledAsync(), "取消後送出鍵仍然是鎖住的。");

            var bubble = await qa.LastBubble.InnerTextAsync();
            _out.WriteLine($"取消後的氣泡：{bubble}");
            Assert.DoesNotContain("這個答案不該出現在畫面上", bubble);
        }

        /// <summary>
        /// **重建索引要先問過**，而且問了說不就真的不送。
        ///
        /// <para>重建會打滿一顆 CPU 好幾分鐘，並與所有人的提問搶同一個推論槽——
        /// 這正是這個動作的閘門是 `CanUpdate` 而不是 `CanRead` 的理由。
        /// `confirm()` 誤點就送出去，等於那個區分白做了。</para>
        ///
        /// <para>Playwright 預設會**自動關掉** dialog（等同按取消），所以第一段
        /// 不必額外設定；第二段才掛上 accept 的處理器。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 重建索引先確認_按取消就不送請求()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            var rebuild = page.Locator("#qaRebuild");
            Assert.True(await rebuild.CountAsync() == 1,
                "admin 看不到重建索引按鈕——ViewBag.CanUpdate 沒有傳到、或權限矩陣沒給 Full。");

            var posted = 0;
            await page.RouteAsync("**/QaAssistant/Rebuild", async route =>
            {
                posted++;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"success\":true,\"message\":\"stub\"}",
                });
            });

            // ① 預設行為＝關掉 dialog（按取消）
            await rebuild.ClickAsync();
            await page.WaitForTimeoutAsync(500);
            Assert.Equal(0, posted);

            // ② 按確定才會真的送出去
            page.Dialog += async (_, d) => await d.AcceptAsync();
            await rebuild.ClickAsync();
            await page.WaitForFunctionAsync("() => true");
            for (var i = 0; i < 20 && posted == 0; i++) await page.WaitForTimeoutAsync(250);

            Assert.Equal(1, posted);
        }
    }
}
