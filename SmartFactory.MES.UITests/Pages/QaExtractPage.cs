using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages
{
    /// <summary>
    /// 擷取物一覽（`/QaExtract`）的頁面物件。唯讀畫面：查詢列 ＋ 三個總數 ＋ 表格 ＋ 分頁。
    /// </summary>
    public class QaExtractPage : BasePage
    {
        public QaExtractPage(IPage page, string baseUrl) : base(page, baseUrl) { }

        public const string Path = "/QaExtract";

        public ILocator SourceSelect => _page.Locator("select[name='sourcePath']");
        public ILocator StateSelect => _page.Locator("select[name='state']");
        public ILocator SearchTerm => _page.Locator("input[name='searchTerm']");
        /// <summary>
        /// ⚠️ 必須用查詢列自己的欄位鎖定這個表單。裸的 `form button[type='submit']`
        /// 會先命中導覽列下拉裡的「登出」——它藏在收合的 dropdown 裡，於是症狀是
        /// 「等元素可見等到逾時 30 秒」，看起來像畫面壞了。`LoginPage` 也踩過同一個坑。
        /// </summary>
        public ILocator SearchButton =>
            _page.Locator("form:has(select[name='sourcePath']) button[type='submit']");

        /// <summary>未查詢時的提示區塊（`hasSearched == false` 的那一支）。</summary>
        public ILocator SearchPrompt => _page.Locator(".alert.alert-light");

        public ILocator Table => _page.Locator("table");
        public ILocator Rows => _page.Locator("table tbody tr");

        /// <summary>表頭「麵包屑」的排序連結。分頁與排序有沒有把篩選條件帶著走，看它。</summary>
        public ILocator HeadingSortLink => _page.Locator("table thead th a").First;

        public async Task GotoAsync()
        {
            await base.GotoAsync(Path);
            await _page.WaitForSelectorAsync("form select[name='sourcePath']");
        }

        /// <summary>
        /// 三個總數卡片的值（生效／未核可／過期）。
        /// 這一頁存在的理由就是讓「有東西被排除了」一眼看得到，所以總數是它的主角，
        /// 不是裝飾——讀不出數字等於這頁沒有履行它的職責。
        /// </summary>
        public async Task<(int Effective, int Pending, int Stale)> CountsAsync()
        {
            var cards = _page.Locator(".card .fs-4");
            var values = new int[3];
            for (var i = 0; i < 3; i++)
            {
                var text = (await cards.Nth(i).InnerTextAsync()).Trim();
                values[i] = int.TryParse(text, out var n) ? n : -1;
            }
            return (values[0], values[1], values[2]);
        }

        /// <summary>填查詢列並送出。`searched=true` 是表單內的 hidden，不必自己補。</summary>
        public async Task SearchAsync(string? state = null, string? term = null)
        {
            if (state is not null) await StateSelect.SelectOptionAsync(state);
            if (term is not null) await SearchTerm.FillAsync(term);

            await SearchButton.First.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }
}
