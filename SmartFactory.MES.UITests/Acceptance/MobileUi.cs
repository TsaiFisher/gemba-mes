using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 手機頁端到端的共用輔助：手機尺寸 context 登入、等下一個 toast、填欄位後觸發 change。
    /// 用真伺服器（不攔截 API）；真手機才有的部分（相機、觸控、安裝成 App、tunnel）不在這裡。
    /// </summary>
    internal static class MobileUi
    {
        /// <summary>手機尺寸 context，從 /mobile/index.html 以指定帳號登入。confirm 一律按確定。</summary>
        public static async Task<(IBrowserContext Context, IPage Page)> SignInAsync(UiTestFixture fixture,
            string? userName = null, string? password = null)
        {
            var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 390, Height = 844 },
                IsMobile = true,
                HasTouch = true,
                Locale = "zh-TW",
            });
            var page = await context.NewPageAsync();
            page.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch (PlaywrightException) { } };
            await page.GotoAsync($"{fixture.BaseUrl}/mobile/index.html");
            await page.FillAsync("#username", userName ?? fixture.AdminUserName);
            await page.FillAsync("#password", password ?? fixture.AdminPassword);
            await page.ClickAsync("button[data-action='login']");
            await page.WaitForURLAsync(new Regex("/mobile/home\\.html"), new PageWaitForURLOptions { Timeout = 20_000 });
            return (context, page);
        }

        /// <summary>做一個動作，回傳它造成的下一個 toast（文字、是否為成功色）。</summary>
        public static async Task<(string Text, bool Success)> ToastAfterAsync(IPage page, Func<Task> act)
        {
            var before = await page.Locator("#toast-container .toast").CountAsync();
            await act();
            await page.WaitForFunctionAsync("n => document.querySelectorAll('#toast-container .toast').length > n",
                before, new PageWaitForFunctionOptions { Timeout = 20_000 });
            var last = page.Locator("#toast-container .toast").Last;
            var cls = await last.GetAttributeAsync("class") ?? "";
            return ((await last.InnerTextAsync()).Trim(), cls.Contains("text-bg-success"));
        }

        public static async Task FillChangeAsync(ILocator input, string value)
        {
            await input.FillAsync(value);
            await input.DispatchEventAsync("change");
        }
    }
}
