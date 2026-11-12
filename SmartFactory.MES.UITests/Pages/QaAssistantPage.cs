using System.Text.Json;
using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages
{
    /// <summary>
    /// 手冊 Q&amp;A 對話畫面（`/QaAssistant`，規格 S-7）的頁面物件。
    ///
    /// <para><b>這一支的關鍵設計是「把 /QaAssistant/Ask 攔下來」</b>——
    /// 見 <see cref="StubAskAsync"/>。真的問一題要花五十秒的 CPU 推論、要有 Ollama、
    /// 還要有嵌入索引，那條路已經由 `QaAssistantLiveAskTests` 走過了。
    /// 這一層要驗的是**瀏覽器裡才驗得到的那一半**：委派接不接得上、
    /// 引文畫成什麼形狀、字串有沒有被當成 HTML。那一半不需要模型，
    /// 也**不該**需要——需要模型的測試在 CI 上只會被跳過，等於沒有。</para>
    /// </summary>
    public class QaAssistantPage : BasePage
    {
        public QaAssistantPage(IPage page, string baseUrl) : base(page, baseUrl) { }

        public const string Path = "/QaAssistant";

        /// <summary>畫面 fetch 的目標。攔截用的樣式要涵蓋絕對與相對兩種形式。</summary>
        public const string AskRoute = "**/QaAssistant/Ask";

        /// <summary>
        /// 串流提問（§11.2 C4）。<b>手冊模式在現代瀏覽器上實際打的是這一支</b>——
        /// <see cref="AskRoute"/> 的樣式結尾是 <c>Ask</c>，匹配不到它。
        /// </summary>
        public const string AskStreamRoute = "**/QaAssistant/AskStream";

        /// <summary>
        /// 資料查詢（Phase 2）的 POST 目標。**與 <see cref="AskRoute"/> 是兩個端點，
        /// 不是同一個端點的兩個參數**——手冊問答讀 `DocChunks`（沒有廠別與資料級過濾），
        /// 資料查詢讀營運資料（現有機制全部經過）。畫面選了哪一種，看的就是打到哪一支。
        /// </summary>
        public const string AskDataRoute = "**/QaAssistant/AskData";

        /// <summary>
        /// 回顯確認後的第二輪（§11.3）。**第三個端點**——`AskData` 的攔截樣式
        /// （結尾是 <c>AskData</c>）不會匹配到它，兩者必須各自 stub。
        /// </summary>
        public const string ConfirmDataRoute = "**/QaAssistant/ConfirmData";

        /// <summary>把一次自然語言查詢另存為常用查詢（Phase 3）。</summary>
        public const string SaveQueryRoute = "**/QaAssistant/SaveQuery";

        public ILocator Log => _page.Locator("#qaLog");
        public ILocator Question => _page.Locator("#qaQuestion");
        public ILocator Send => _page.Locator("#qaSend");
        public ILocator Cancel => _page.Locator("#qaCancel");
        public ILocator EmptyPrompt => _page.Locator("#qaEmpty");

        public ILocator ModeManual => _page.Locator("#qaModeManual");
        public ILocator ModeData => _page.Locator("#qaModeData");
        public ILocator ModeHint => _page.Locator("#qaModeHint");

        /// <summary>最後一則助理氣泡的內容區（`bubble()` 造出來的第二個 div）。</summary>
        public ILocator LastBubble => _page.Locator("#qaLog > div > div:nth-child(2)").Last;

        public async Task GotoAsync()
        {
            await base.GotoAsync(Path);
            await _page.WaitForSelectorAsync("#qaForm");
        }

        /// <summary>
        /// 攔下提問的 POST，回一份合成的成功回應。
        ///
        /// <para><paramref name="delayMs"/> 用來模擬「模型正在想」那段時間：
        /// 等待狀態（輸入鎖住、取消鍵現身）只在飛行中存在，不拖長就抓不到。</para>
        /// </summary>
        public async Task StubAskAsync(object data, int delayMs = 0)
        {
            var json = JsonSerializer.Serialize(new { success = true, message = (string?)null, data });

            await _page.RouteAsync(AskRoute, async route =>
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = json,
                });
            });

            // 🚨 **手冊模式在支援 ReadableStream 的瀏覽器上打的是 AskStream，不是 Ask**
            //    （§11.2 C4，2026-08-21）。Chromium 一律支援，所以只攔 Ask 的話，
            //    這些測試打的其實是真端點——有 Ollama 就逾時、沒有就拿到錯誤訊框，
            //    而且**兩邊都紅得像畫面壞了**。C4 上線時漏改，四支因此死了三天。
            //    兩條都攔，測試意圖（「問一題，回這份答案」）才與模式選擇無關。
            await StubAskStreamAsync(data, delayMs);
        }

        /// <summary>
        /// 攔下串流提問的 POST，回一份合成的 NDJSON。形狀對齊
        /// <c>QaAssistantController.AskStream</c>：先一段 <c>{"t":…}</c>，再一個 <c>{"done":{…}}</c>。
        ///
        /// <para>刻意送**兩個以上**的訊框而不是只送 done：畫面端的 pump 要能跨行切割、
        /// 要在第一個 token 到達時把「思考中」換成答案樣式。只送 done 的話那兩段程式碼一行都沒跑到。</para>
        ///
        /// <para>同理，前面兩格 <c>stage</c> 訊框也是刻意送的（C4-A）。少了它們，
        /// 畫面端依階段換標籤的那段程式碼在**整個 UI 測試套件裡一行都不會跑到**——
        /// 而它壞掉的樣子（標籤永遠停在「思考中」）與正常等待幾乎分不出來。</para>
        /// </summary>
        public async Task StubAskStreamAsync(object data, int delayMs = 0)
        {
            // done 的負載就是非串流那一份；body 拿來當第一個文字訊框，
            // 讓「畫面累積原始輸出、done 再用權威版本覆蓋」那條路徑真的被走過。
            var payload = JsonSerializer.SerializeToElement(data);
            var body = payload.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            var ndjson =
                JsonSerializer.Serialize(new { stage = "search" }) + "\n" +
                JsonSerializer.Serialize(new { stage = "infer", chunks = 3, chars = 3500 }) + "\n" +
                JsonSerializer.Serialize(new { t = body }) + "\n" +
                JsonSerializer.Serialize(new { done = data }) + "\n";

            await _page.RouteAsync(AskStreamRoute, async route =>
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/x-ndjson; charset=utf-8",
                    Body = ndjson,
                });
            });
        }

        /// <summary>
        /// 攔下資料查詢的 POST，回一份合成的成功回應。<paramref name="data"/> 的形狀
        /// 對齊 `QaAssistantController.AskData`：outcome／answer／toolKey／paramsJson／
        /// headers／rows／totalRows／truncated／note／elapsedMs。
        /// </summary>
        public async Task StubAskDataAsync(object data, int delayMs = 0)
        {
            var json = JsonSerializer.Serialize(new { success = true, message = (string?)null, data });

            await _page.RouteAsync(AskDataRoute, async route =>
            {
                if (delayMs > 0) await Task.Delay(delayMs);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = json,
                });
            });
        }

        /// <summary>
        /// 切到「資料查詢」模式。
        ///
        /// <para>⚠️ 點的是 <c>label</c> 不是 <c>input</c>：Bootstrap 的 <c>btn-check</c>
        /// 把 radio 本身藏起來（<c>position:absolute;clip</c>），直接點 input 會因
        /// 「元素不可見」而逾時。點 label 也才是使用者真正做的那個動作。</para>
        /// </summary>
        public async Task SelectDataModeAsync()
        {
            await _page.Locator("label[for=\"qaModeData\"]").ClickAsync();
            await _page.WaitForFunctionAsync("() => document.getElementById('qaModeData').checked");
        }

        /// <summary>填入問題並按送出。不等待回應——等什麼由測試自己決定。</summary>
        public async Task AskAsync(string question)
        {
            await Question.FillAsync(question);
            await Send.ClickAsync();
        }

        /// <summary>
        /// 送出後畫面是否處於「等待中」：輸入鎖住、取消鍵可見。
        /// 取消鍵的顯示是靠 `u-display-none` 的 classList 增刪，不是 `style.display`
        /// （切錯會與 utility class 打架而永久隱藏，是本專案記錄有案的坑）。
        /// </summary>
        public async Task<bool> IsWaitingAsync() =>
            await Question.IsDisabledAsync() && await Cancel.IsVisibleAsync();
    }
}
