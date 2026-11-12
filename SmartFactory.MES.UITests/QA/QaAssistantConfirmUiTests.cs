using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.QA
{
    /// <summary>
    /// 回顯確認面板（§9.2-3／§9.3，狀態管理 §11.3）的瀏覽器層守門。
    ///
    /// <para><b>這一層驗的東西在別處驗不到。</b>服務層的測試能證明「含金額的工具會回
    /// <c>NeedsConfirm</c> 且不執行」，但證明不了使用者**看得到、也改得動**——
    /// 而後者才是 §9.2-3 要的：回顯的意義不是把參數印出來，是讓人在執行前改掉它。
    /// 一個畫得出來卻送不出去的面板，在服務層測試裡完全是綠的。</para>
    ///
    /// <para><b>一顆模型都不需要</b>：兩個端點都由 Playwright 攔下來回合成 JSON，
    /// 理由與 <see cref="QaAssistantDataUiTests"/> 一字不差。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class QaAssistantConfirmUiTests
    {
        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public QaAssistantConfirmUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        /// <summary>一份「含金額，必須先確認」的第一輪回應（§9.3）。</summary>
        private static object NeedsConfirmPayload() => new
        {
            outcome = "NeedsConfirm",
            answer = "",
            toolKey = "fin.margin_analysis",
            paramsJson = "{\"period\":\"LastMonth\",\"topN\":5}",
            headers = (string[]?)null,
            rows = (object[]?)null,
            totalRows = 0,
            truncated = false,
            note = (string?)null,
            elapsedMs = 3210,
            turnId = 4242,
            confirmReason = "Monetary",
            confirmHint = "這份查詢的結果含金額，執行前請先確認查詢條件。",
            candidates = new[]
            {
                new
                {
                    toolKey = "fin.margin_analysis",
                    description = "毛利分析",
                    paramsJson = "{\"period\":\"LastMonth\",\"topN\":5}",
                    properties = new Dictionary<string, object>
                    {
                        ["period"] = new
                        {
                            type = "string",
                            enumValues = new[] { "LastMonth", "ThisMonth", "LastQuarter" },
                            format = (string?)null,
                        },
                        ["topN"] = new { type = "integer", enumValues = (string[]?)null, format = (string?)null },
                    },
                },
            },
        };

        private static object AnsweredPayload() => new
        {
            outcome = "Answered",
            answer = "本月毛利率 32%。",
            toolKey = "fin.margin_analysis",
            paramsJson = "{\"period\":\"ThisMonth\",\"topN\":5}",
            headers = new[] { "產品", "毛利率" },
            rows = new[] { new object[] { "甲產品", "32%" } },
            totalRows = 1,
            truncated = false,
            note = (string?)null,
            elapsedMs = 5000,
            turnId = 4243,
            confirmReason = "None",
            confirmHint = "",
            candidates = Array.Empty<object>(),
        };

        /// <summary>
        /// **確認之前不出表格，確認之後才打第二個端點。**
        ///
        /// <para>這是本項最重要的一條行為：<c>NeedsConfirm</c> 那一輪伺服端一列業務資料都沒查，
        /// 所以畫面上也不該有任何看起來像查詢結果的東西。如果面板畫錯、把
        /// <c>NeedsConfirm</c> 也丟給表格渲染那一段，症狀是**一張空表格**——
        /// 看起來像「查無資料」，而實際上根本還沒查。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 需要確認時_不出表格_按下確認才打ConfirmData()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            await qa.StubAskDataAsync(NeedsConfirmPayload());

            var confirmCount = 0;
            await page.RouteAsync(QaAssistantPage.ConfirmDataRoute, async route =>
            {
                confirmCount++;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = System.Text.Json.JsonSerializer.Serialize(
                        new { success = true, message = (string?)null, data = AnsweredPayload() }),
                });
            });

            await qa.SelectDataModeAsync();
            await qa.AskAsync("上個月毛利多少");

            var confirmButton = qa.LastBubble.Locator("[data-action=\"qaConfirmRun\"]");
            await confirmButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            // 確認之前：沒有表格，也還沒打第二個端點。
            Assert.Equal(0, await qa.LastBubble.Locator("table").CountAsync());
            Assert.Equal(0, confirmCount);

            // 確認理由要顯示出來——它是使用者判斷「為什麼要問我」的唯一依據（§9.3）。
            var text = await qa.LastBubble.InnerTextAsync();
            Assert.Contains("金額", text, StringComparison.Ordinal);
            _out.WriteLine($"確認面板：{text.Replace("\n", " / ")}");

            await confirmButton.ClickAsync();
            await qa.LastBubble.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.Equal(1, confirmCount);
        }

        /// <summary>
        /// **改過的參數真的送出去了**（§11.3「合併參數」）。
        ///
        /// <para>回顯若只是印出來看，使用者發現得了問題卻改不動——那等於把
        /// §9.2-3 做成了唯讀的告示。所以這一支攔的是**送出去的 body**，
        /// 而不只是畫面上的控制項有沒有出現。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 在面板上改掉期間_送出的是改過的值()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            await qa.StubAskDataAsync(NeedsConfirmPayload());

            string? posted = null;
            await page.RouteAsync(QaAssistantPage.ConfirmDataRoute, async route =>
            {
                posted = route.Request.PostData;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = System.Text.Json.JsonSerializer.Serialize(
                        new { success = true, message = (string?)null, data = AnsweredPayload() }),
                });
            });

            await qa.SelectDataModeAsync();
            await qa.AskAsync("上個月毛利多少");

            var period = qa.LastBubble.Locator("[data-qa-param=\"period\"]");
            await period.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            // 列舉必須畫成下拉：沒有合法值可挑的話，使用者只能猜字面，
            // 而猜錯的下場是伺服端回「參數已失效」——看起來像系統壞了。
            Assert.Equal("select", await period.EvaluateAsync<string>("el => el.tagName.toLowerCase()"));

            // 模型填的值要當成初始值回顯出來，否則「它其實查的不是我問的那一段」看不出來。
            Assert.Equal("LastMonth", await period.InputValueAsync());

            await period.SelectOptionAsync("ThisMonth");
            await qa.LastBubble.Locator("[data-action=\"qaConfirmRun\"]").ClickAsync();
            await qa.LastBubble.Locator("table").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            Assert.NotNull(posted);
            _out.WriteLine($"送出的 body：{posted}");

            Assert.Contains("turnId=4242", posted, StringComparison.Ordinal);
            Assert.Contains("p_period=ThisMonth", posted, StringComparison.Ordinal);

            // 沒被改到的參數照樣帶回去，否則第二輪會少送一個條件而沒人察覺。
            Assert.Contains("p_topN=5", posted, StringComparison.Ordinal);
        }

        /// <summary>
        /// **放棄那一輪只動畫面，不打任何端點。**
        ///
        /// <para>刻意不做「取消」端點：它會是一個吃 <c>turnId</c> 的寫入路徑，
        /// 而它買到的只是讓一列稽核資料早幾天消失（那一列由保留期修剪收掉）。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 放棄確認_不打任何端點()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaAssistantPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            await qa.StubAskDataAsync(NeedsConfirmPayload());

            var confirmCount = 0;
            await page.RouteAsync(QaAssistantPage.ConfirmDataRoute, async route =>
            {
                confirmCount++;
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = "{}" });
            });

            await qa.SelectDataModeAsync();
            await qa.AskAsync("上個月毛利多少");

            var discard = qa.LastBubble.Locator("[data-action=\"qaConfirmDiscard\"]");
            await discard.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await discard.ClickAsync();

            await Assertions.Expect(qa.LastBubble.Locator("[data-action=\"qaConfirmRun\"]"))
                .ToHaveCountAsync(0);
            Assert.Equal(0, confirmCount);
        }
    }
}
