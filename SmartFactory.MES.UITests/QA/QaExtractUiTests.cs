using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.QA
{
    /// <summary>
    /// 擷取物一覽（`/QaExtract`）的瀏覽器層守門。
    ///
    /// <para><b>為什麼一個唯讀清單也值得測。</b>全站 38 支 Index 都用
    /// 「`ViewBag.Searched` 決定要不要渲染結果」這個 pattern，而 `NcrController`
    /// 曾經整支漏設 `ViewBag.Searched = searched;`——查詢跑了、Model 有列、
    /// 畫面永遠停在「請先查詢」。**沒有例外、沒有紅燈**，是本層專門要防的靜默壞法。</para>
    ///
    /// <para><b>對零資料的態度。</b>擷取物要進 DB 得先跑索引，而索引要嵌入模型；
    /// 乾淨部署上這張表是空的，那是**正常狀態**不是缺陷。把它判成失敗會讓 nightly
    /// 常態紅，紅久了就沒人看。所以下面每一支都拆成「不依賴資料的契約」與
    /// 「有資料時才多驗的部分」，並把走了哪一條印出來（同 `ScheduleGanttTests`）。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class QaExtractUiTests
    {
        private readonly UiTestFixture _fx;
        private readonly ITestOutputHelper _out;

        public QaExtractUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fx = fixture;
            _out = output;
        }

        /// <summary>
        /// 進頁面是「請先查詢」，按下查詢才渲染表格。
        /// 兩個方向都要驗：只驗後者的話，一支永遠顯示表格的畫面也會通過。
        /// </summary>
        [QaScreenFact]
        public async Task 未查詢時顯示提示_查詢後才渲染表格()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var pageErrors = new List<string>();
            page.PageError += (_, e) => pageErrors.Add(e);

            var qa = new QaExtractPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            Assert.Equal(1, await qa.SearchPrompt.CountAsync());
            Assert.Equal(0, await qa.Table.CountAsync());

            await qa.SearchAsync();

            Assert.Equal(0, await qa.SearchPrompt.CountAsync());
            Assert.Equal(1, await qa.Table.CountAsync());

            var rows = await qa.Rows.CountAsync();
            _out.WriteLine(rows == 1 && await qa.Rows.First.Locator("td[colspan]").CountAsync() == 1
                ? "走到「查詢到 0 筆」分支（索引是空的，乾淨部署的正常狀態）"
                : $"走到「有資料」分支：{rows} 列");

            Assert.True(pageErrors.Count == 0,
                $"擷取物一覽有未攔截的 JS 錯誤：\n  {string.Join("\n  ", pageErrors)}");
        }

        /// <summary>
        /// **排序連結必須帶著篩選條件走。**
        ///
        /// <para>漏帶是 `asp-route-*` 清單畫面的經典缺陷：使用者篩了「未核可」再點排序，
        /// 條件被洗掉、回到全部——而畫面看起來完全正常（有表格、有資料、排序也對），
        /// 只是他篩的東西不見了。這一支不依賴任何資料：表頭在 `hasSearched` 為真時
        /// 一定渲染，0 筆也一樣。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 排序連結帶著查詢條件_不會把篩選洗掉()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaExtractPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            await qa.SearchAsync(state: "pending", term: "匯率");

            var href = await qa.HeadingSortLink.GetAttributeAsync("href");
            _out.WriteLine($"排序連結：{href}");
            Assert.False(string.IsNullOrEmpty(href));

            Assert.Contains("state=pending", href);
            Assert.Contains("searched=true", href);
            Assert.Contains("sortOrder=", href);

            // 關鍵字有做 URL 編碼，不能直接比對中文字面。
            Assert.Contains("searchTerm=", href);
            Assert.Contains(Uri.EscapeDataString("匯率"), href);

            // 點下去之後條件仍在（連結對了但 Controller 沒接住，症狀一模一樣）。
            await qa.HeadingSortLink.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            Assert.Equal("pending", await qa.StateSelect.InputValueAsync());
            Assert.Equal("匯率", await qa.SearchTerm.InputValueAsync());
        }

        /// <summary>
        /// **三個總數是這一頁的主角**：它存在的理由就是讓「有東西被排除了」一眼看得到，
        /// 而分頁後的清單本身回答不了這件事。讀不出數字（空白、`NaN`、被版面吃掉）
        /// 等於這頁沒有履行它的職責。
        ///
        /// <para>有資料時再多驗一件伺服端測不到的事：**未核可的列不顯示內文**。
        /// 未核可＝還沒有人審過那段文字，畫面照樣秀出來的話，這個一覽就從
        /// 「看得到什麼被排除了」變成「未經審核的內容照樣看得到」。</para>
        /// </summary>
        [QaScreenFact]
        public async Task 三個總數讀得出數字_未核可的列不顯示內文()
        {
            var page = await _fx.NewLoggedInPageAsync();
            var qa = new QaExtractPage(page, _fx.BaseUrl);
            await qa.GotoAsync();

            var (effective, pending, stale) = await qa.CountsAsync();
            _out.WriteLine($"生效 {effective}／未核可 {pending}／過期 {stale}");

            Assert.True(effective >= 0 && pending >= 0 && stale >= 0,
                $"三個總數至少有一個不是數字（生效 {effective}／未核可 {pending}／過期 {stale}）——" +
                "ViewBag 沒傳到，或版面把它吃掉了。");

            await qa.SearchAsync(state: "pending");

            var rows = await qa.Rows.CountAsync();
            if (rows == 0 || await qa.Rows.First.Locator("td[colspan]").CountAsync() == 1)
            {
                _out.WriteLine("索引裡沒有未核可的列，跳過內文遮蔽的驗證（不判失敗）");
                return;
            }

            // 未核可列的最後一欄必須是斜體的替代文字，不是內文本身。
            var firstRowText = qa.Rows.First.Locator("td").Last;
            Assert.Equal(1, await firstRowText.Locator("span.fst-italic").CountAsync());

            var badge = await qa.Rows.First.Locator("span.badge").InnerTextAsync();
            _out.WriteLine($"第一列狀態：{badge}／內文欄：{await firstRowText.InnerTextAsync()}");
        }
    }
}
