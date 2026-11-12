using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.QA
{
    /// <summary>
    /// 對話畫面**資料查詢模式**（Phase 2）的瀏覽器層守門，與
    /// <see cref="QaAssistantUiTests"/>（手冊問答模式）同一個畫面、不同的那一半。
    ///
    /// <para><b>為什麼另開一支而不是加在既有那一檔。</b>兩種模式在畫面上共用輸入框與氣泡，
    /// 但送去的端點、回來的形狀、渲染函式（`renderCitations` vs `renderData`）
    /// 三者完全不同。混在一起的話，一支測試紅了要先分辨它在說哪一種模式。</para>
    ///
    /// <para><b>同樣一顆模型都不需要。</b>`/QaAssistant/AskData` 由
    /// <see cref="QaAssistantPage.StubAskDataAsync"/> 攔下來回合成資料——
    /// 真的問一題要兩趟推論（選工具、寫摘要），在筆電上是分鐘級。
    /// 這一層要驗的是那份 JSON 到了瀏覽器裡被畫成什麼形狀，那與模型無關。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class QaAssistantDataUiTests
    {
        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public QaAssistantDataUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        /// <summary>一份「查得到、也寫得出摘要」的完整回應。</summary>
        private static object AnsweredPayload() => new
        {
            outcome = "Answered",
            answer = "上個月銷售金額最高的是甲客戶。",
            toolKey = "ord.sales_ranking",
            paramsJson = "{\"period\":\"LastMonth\",\"topN\":5}",
            headers = new[] { "客戶", "數量" },
            rows = new[]
            {
                new object[] { "甲客戶", 120 },
                new object[] { "乙客戶", 80 },
            },
            totalRows = 2,
            truncated = false,
            note = (string?)null,
            elapsedMs = 4321,
        };

        /// <summary>
        /// **模式切換真的換掉了端點。**
        ///
        /// <para>這是本檔最基本、也最容易靜默壞掉的一件事：`currentMode()` 讀錯 id、
        /// radio 的 <c>name</c> 打錯、或三元運算寫反，症狀都是**畫面完全正常**——
        /// 有氣泡、有答案、有耗時，只是那個答案來自手冊語料而使用者以為它查了資料庫。
        /// 所以兩個端點都掛上計數器，並且斷言另一支是 <b>0</b>：只驗「AskData 有被打到」
        /// 的話，兩支都打的實作也會通過。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 切到資料查詢後_打的是AskData而不是Ask()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            var askCount = 0;
            await page.RouteAsync(QaAssistantPage.AskRoute, async route =>
            {
                askCount++;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"success\":true,\"data\":{\"body\":\"手冊答案\",\"isNoAnswer\":false," +
                           "\"elapsedMs\":1,\"model\":\"stub\",\"citations\":[]}}",
                });
            });

            var dataCount = 0;
            await page.RouteAsync(QaAssistantPage.AskDataRoute, async route =>
            {
                dataCount++;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = System.Text.Json.JsonSerializer.Serialize(
                        new { success = true, message = (string?)null, data = AnsweredPayload() }),
                });
            });

            // 預設是手冊問答（整合測試驗過 HTML 上的 checked，這裡驗它實際的行為後果）。
            Assert.True(await qa.ModeManual.IsCheckedAsync());
            var hintBefore = await qa.ModeHint.InnerTextAsync();

            await qa.SelectDataModeAsync();

            // 提示文字必須跟著換。它是使用者判斷「我現在問的是哪一邊」的唯一依據。
            var hintAfter = await qa.ModeHint.InnerTextAsync();
            Assert.NotEqual(hintBefore, hintAfter);
            _out.WriteLine($"模式提示：{hintBefore} → {hintAfter}");

            await qa.AskAsync("上個月哪個客戶買最多");
            await qa.LastBubble.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.Equal(1, dataCount);
            Assert.True(askCount == 0,
                $"切到資料查詢後仍打了 /QaAssistant/Ask {askCount} 次——" +
                "模式判斷沒生效，使用者拿到的是手冊語料的答案而不是查詢結果。");
        }

        /// <summary>
        /// **表格、筆數、以及查詢條件回顯**（§9.2-3）。
        ///
        /// <para>回顯那一段是這個模式的安全機制而不是裝飾：實測模型會自行填入問題裡
        /// 沒提到的參數（`dateBasis`、`sortBy`），而填出來的數字看起來完全合理、也自洽。
        /// 開展「查詢條件」是使用者唯一能發現「它查的不是我問的那一段」的地方，
        /// 所以它必須**真的在 DOM 裡**、而且點得開。</para>
        ///
        /// <para>⚠️ 內容要用 <c>TextContent</c> 不能用 <c>InnerText</c>：<c>details</c> 收合時
        /// 內容不參與排版，<c>InnerText</c> 一律回空字串——會把「參數根本沒送來」
        /// 與「還沒點開」混為一談。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 查詢結果畫成表格_查詢條件回顯得出來()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var pageErrors = new List<string>();
            page.PageError += (_, e) => pageErrors.Add(e);

            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.StubAskDataAsync(AnsweredPayload());
            await qa.SelectDataModeAsync();

            await qa.AskAsync("上個月哪個客戶買最多");
            var bubble = qa.LastBubble;
            await bubble.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            // ── 表格 ──
            var headers = await bubble.Locator("table thead th").AllInnerTextsAsync();
            Assert.Equal(new[] { "客戶", "數量" }, headers);
            Assert.Equal(2, await bubble.Locator("table tbody tr").CountAsync());
            Assert.Equal("甲客戶",
                await bubble.Locator("table tbody tr").First.Locator("td").First.InnerTextAsync());

            // ── 查詢條件回顯 ──
            var cond = bubble.Locator("details");
            Assert.Equal(1, await cond.CountAsync());

            var sum = await cond.Locator("summary").InnerTextAsync();
            Assert.Contains("ord.sales_ranking", sum);

            var body = await cond.Locator("div").TextContentAsync();
            Assert.Contains("LastMonth", body ?? "");
            Assert.Contains("topN", body ?? "");

            await cond.Locator("summary").ClickAsync();
            Assert.True(await cond.Locator("div").IsVisibleAsync(),
                "點開「查詢條件」後仍看不到參數——展開的是一個空殼，等於沒有回顯。");

            // ── 筆數 ──
            Assert.Contains("2", await bubble.InnerTextAsync());

            Assert.True(pageErrors.Count == 0,
                $"資料查詢有未攔截的 JS 錯誤：\n  {string.Join("\n  ", pageErrors)}");
        }

        /// <summary>
        /// **儲存格內容一律當文字**（§9.4 I2）。
        ///
        /// <para>這一支的攻擊面與手冊問答那一支**不是同一個**，所以不能靠它涵蓋：
        /// 那邊的來源是模型輸出，這邊的來源是**資料庫裡的業務資料**——客戶名稱、
        /// 品名、備註全是使用者可自行輸入的欄位。任何人只要建一筆客戶叫
        /// <c>&lt;img src=x onerror=...&gt;</c>，就能讓每個查這張報表的人執行它。</para>
        ///
        /// <para>四個位置分開驗（摘要、欄名、儲存格、查詢條件），因為它們在
        /// `renderData` 裡是四段各自獨立的程式碼——只驗一處，另外三處換成
        /// <c>innerHTML</c> 也照樣通過。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 業務資料一律以文字呈現_不得注入節點()
        {
            const string Payload = "<img src=x onerror=\"window.__xss=1\">";

            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.StubAskDataAsync(new
            {
                outcome = "Answered",
                answer = "摘要 " + Payload,
                toolKey = "ord.sales_ranking",
                paramsJson = "{\"note\":\"" + "參數 " + "\"}",
                headers = new[] { "欄名 " + Payload },
                rows = new[] { new object[] { "儲存格 " + Payload } },
                totalRows = 1,
                truncated = false,
                note = (string?)null,
                elapsedMs = 1,
            });
            await qa.SelectDataModeAsync();

            await qa.AskAsync("注入測試");
            var bubble = qa.LastBubble;
            await bubble.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.Equal(0, await qa.Log.Locator("img").CountAsync());
            Assert.Equal(0, await qa.Log.Locator("script").CountAsync());

            var pwned = await page.EvaluateAsync<bool>("() => window.__xss === 1");
            Assert.False(pwned, "業務資料被當成 HTML 執行了——td/th 的 textContent 被換成 innerHTML。");

            // 反面：不是被過濾掉，而是原樣顯示成文字。
            // sanitize 掉會讓使用者看不到資料庫裡實際存了什麼，那是另一種壞法。
            //
            // 摘要是氣泡的第一個文字節點（`container.textContent = d.answer`），
            // 選擇器選不到它——表格與查詢條件都是之後才 append 的元素。
            var answerText = await bubble.EvaluateAsync<string>(
                "el => el.childNodes[0] ? el.childNodes[0].textContent : ''");
            Assert.Contains(Payload, answerText);
            Assert.Contains(Payload, await bubble.Locator("table thead th").First.InnerTextAsync());
            Assert.Contains(Payload, await bubble.Locator("table tbody td").First.InnerTextAsync());
        }

        /// <summary>
        /// **四種結局各有各的形狀**（§9.1／§9.2）——這是 `renderData` 刻意不收斂成一種的理由。
        ///
        /// <list type="number">
        ///   <item><description><b>SummarySkipped</b>：摘要沒寫成，但**表格照樣給**。
        ///     資料早就查到了，因為第二趟推論逾時就把它丟掉，等於讓人白等兩分鐘。
        ///     同時 `note` 必須看得見——降級要出聲（§17.8），否則使用者會以為
        ///     「這就是全部了」。</description></item>
        ///   <item><description><b>NoTool</b>：模型說沒有對應的工具。這是**正常路徑不是錯誤**，
        ///     所以畫成灰字斜體的狀態，而且**不得出現表格或查詢條件**——
        ///     沒有查詢發生過，畫一個空表格就是在暗示「查了、但沒有資料」。</description></item>
        /// </list>
        /// </summary>
        [QaScreenFact]
        public async Task 摘要降級時表格照樣給_而沒有工具時不畫表格()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();
            await qa.SelectDataModeAsync();

            // ① SummarySkipped：有表格、有 note
            await qa.StubAskDataAsync(new
            {
                outcome = "SummarySkipped",
                answer = (string?)null,
                toolKey = "ord.sales_ranking",
                paramsJson = "{\"period\":\"LastMonth\"}",
                headers = new[] { "客戶", "數量" },
                rows = new[] { new object[] { "甲客戶", 120 } },
                totalRows = 1,
                truncated = false,
                note = "摘要逾時，以下直接列出查詢結果。",
                elapsedMs = 9999,
            });

            await qa.AskAsync("上個月哪個客戶買最多");
            var first = qa.LastBubble;
            await first.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.Equal(1, await first.Locator("table tbody tr").CountAsync());
            var note = first.Locator(".alert");
            Assert.Equal(1, await note.CountAsync());
            Assert.Contains("摘要逾時", await note.InnerTextAsync());

            // ② NoTool：純狀態，不畫表格也不畫查詢條件
            await page.UnrouteAsync(QaAssistantPage.AskDataRoute);
            await qa.StubAskDataAsync(new
            {
                outcome = "NoTool",
                answer = "找不到可以回答這個問題的報表。",
                toolKey = (string?)null,
                paramsJson = (string?)null,
                headers = (string[]?)null,
                rows = (object[]?)null,
                totalRows = 0,
                truncated = false,
                note = (string?)null,
                elapsedMs = 1,
            });

            await qa.AskAsync("今天天氣如何");
            await qa.Question.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            var second = qa.LastBubble;
            await Assertions.Expect(second).ToContainTextAsync("找不到可以回答",
                new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

            Assert.Equal(0, await second.Locator("table").CountAsync());
            Assert.Equal(0, await second.Locator("details").CountAsync());
            _out.WriteLine($"NoTool 氣泡：{await second.InnerTextAsync()}");
        }
    
        /// <summary>
        /// **另存為常用查詢**（Phase 3，§十三）。
        ///
        /// <para>兩件事在這一層才驗得到：① 存的憑據是 <c>turnId</c>，
        /// 畫面**不送**工具鍵與參數——送了的話，「另存」就成了一個可以憑空寫入
        /// 任意查詢定義的入口，而那張定義日後可能被掛上排程；
        /// ② 伺服端回的訊息（可能夾帶「哪些條件沒存進去」）要真的顯示出來，
        /// 那不是裝飾，是這張常用查詢與剛才那一題的差異。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 另存常用查詢_只送turnId_並把回覆訊息顯示出來()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            await qa.StubAskDataAsync(new
            {
                outcome = "Answered",
                answer = "共 2 筆。",
                toolKey = "ord.sales_ranking",
                paramsJson = "{\"period\":\"LastMonth\",\"topN\":5}",
                headers = new[] { "客戶", "數量" },
                rows = new[] { new object[] { "甲客戶", 120 } },
                totalRows = 1,
                truncated = false,
                note = (string?)null,
                elapsedMs = 4321,
                turnId = 9001,
                confirmReason = "None",
                confirmHint = "",
                candidates = Array.Empty<object>(),
            });

            string? posted = null;
            await page.RouteAsync(QaAssistantPage.SaveQueryRoute, async route =>
            {
                posted = route.Request.PostData;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = "{\"success\":true,\"message\":\"已儲存。（topN 這幾個條件報表畫面沒有對應欄位）\"," +
                           "\"data\":{\"id\":7,\"name\":\"上月排行\",\"dropped\":\"topN\"}}",
                });
            });

            await qa.SelectDataModeAsync();
            await qa.AskAsync("上個月哪個客戶買最多");
            await qa.LastBubble.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            await qa.LastBubble.Locator("[data-qa-save-name]").FillAsync("上月排行");
            await qa.LastBubble.Locator("[data-action=\"qaSaveQuery\"]").ClickAsync();

            await Assertions.Expect(qa.LastBubble.Locator("[data-qa-save-status]"))
                .ToContainTextAsync("已儲存");

            Assert.NotNull(posted);
            _out.WriteLine($"送出的 body：{posted}");

            Assert.Contains("turnId=9001", posted, StringComparison.Ordinal);

            // 🚨 工具鍵與參數**不得**出現在送出的 body 裡。
            Assert.DoesNotContain("sales_ranking", posted, StringComparison.Ordinal);
            Assert.DoesNotContain("LastMonth", posted, StringComparison.Ordinal);

            // 被丟掉的條件要顯示給使用者看——那張常用查詢的範圍比剛才那一題寬。
            await Assertions.Expect(qa.LastBubble.Locator("[data-qa-save-status]"))
                .ToContainTextAsync("topN");
        }
}
}
