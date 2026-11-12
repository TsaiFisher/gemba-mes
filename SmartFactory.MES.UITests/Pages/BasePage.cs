using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Pages
{
    public abstract class BasePage
    {
        protected readonly IPage _page;
        protected readonly string _baseUrl;

        protected BasePage(IPage page, string baseUrl)
        {
            _page = page;
            _baseUrl = baseUrl;
        }

        public async Task GotoAsync(string path)
        {
            await _page.GotoAsync($"{_baseUrl}{path}");
        }

        public async Task<string> GetTitleAsync()
        {
            return await _page.TitleAsync();
        }
    }
}
