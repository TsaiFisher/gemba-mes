using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 權限收窄的角色走一遍畫面：看得到、按鈕不該出現、直接打 API 也要被擋。
    ///
    /// <para><b>為什麼要有這一支</b>：其餘 UI 測試全部以 admin 登入，所以
    /// 「沒有權限的人看不看得到按鈕」「按鈕藏了但直接 POST 會不會過」在瀏覽器層從來沒被走過。
    /// 後端的權限檢查有整合測試，可是整合測試答不了畫面那一半——按鈕漏藏只是難看，
    /// 按鈕藏了而後端漏擋才是真的洞，兩半要一起看才知道是哪一種。</para>
    ///
    /// <para><b>帳號與角色</b>：seed-e2e-fixture.sql ⑳ 的 e2e.readonly／E2E-RO，
    /// 只有 JournalEntry 與 APDebitNote 的 CanRead。用專屬角色而不收窄既有角色，
    /// 理由見種子檔該段註解。</para>
    ///
    /// <para><b>API 呼叫要用唯讀帳號自己的 antiforgery token</b>：token 綁使用者身分，
    /// 拿 admin 的 token 送出會得到 400——那跟 403 一樣「不是 200」，測試會假綠。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ReadOnlyRoleUiTests
    {
        private const string UserName = "e2e.readonly";
        private const string Password = "E2eReadOnly!2026";
        private const string DraftNoteId = "E2E-DN01";

        private readonly UiTestFixture _fixture;
        public ReadOnlyRoleUiTests(UiTestFixture fixture) => _fixture = fixture;

        private async Task<(IBrowserContext context, IPage page)> SignInAsReadOnlyAsync()
        {
            await SeedFixture.ResetAsync();

            var (context, page) = await _fixture.NewAnonymousPageAsync();
            var login = new LoginPage(page, _fixture.BaseUrl);
            await login.GotoAsync();
            await login.LoginAsync(UserName, Password);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            Assert.DoesNotContain("/Identity/Account/Login", page.Url, StringComparison.OrdinalIgnoreCase);
            return (context, page);
        }

        /// <summary>以目前頁面（唯讀帳號）的 antiforgery token 直接 POST，回傳狀態碼與內容。</summary>
        private static async Task<(int status, string body)> PostAsUserAsync(IPage page, string url)
        {
            var token = await page.Locator("input[name='__RequestVerificationToken']").First.InputValueAsync();
            Assert.False(string.IsNullOrEmpty(token), "頁面上沒有 antiforgery token，無法以使用者身分送出請求。");

            var response = await page.APIRequest.PostAsync(url, new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["X-Plant-Context"] = await page.Locator("meta[name='mes-plant-context']").GetAttributeAsync("content") ?? "",
                    ["X-Requested-With"] = "XMLHttpRequest",
                },
                Form = page.APIRequest.CreateFormData().Set("__RequestVerificationToken", token),
                MaxRedirects = 0,
            });
            return (response.Status, await response.TextAsync());
        }

        private static void AssertRejected(int status, string body, string what)
        {
            // 400 是 antiforgery 驗證失敗，代表請求根本沒進到權限檢查——那樣的「被擋」什麼都沒證明。
            Assert.True(status != 400, $"{what}：回 400（antiforgery 驗證失敗），權限檢查沒有被走到。內容：{body}");
            var rejected = status is 302 or 403
                        || (status == 200 && body.Contains("\"success\":false"));
            Assert.True(rejected, $"{what}：唯讀角色的寫入請求沒有被擋下（HTTP {status}）：{body}");
        }

        [Fact]
        public async Task 手動傳票_看得到列表_沒有新增鈕_直接送出也被擋()
        {
            var (context, page) = await SignInAsReadOnlyAsync();
            await using (context)
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/JournalEntry?searched=true");
                Assert.Contains("/JournalEntry", page.Url);
                await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
                Assert.Equal(0, await page.Locator("[data-url$='/JournalEntry/Create']").CountAsync());

                var before = await SeedFixture.ScalarAsync<long>(
                    @"SELECT count(*) FROM ""JournalEntries"" WHERE ""SourceDocType"" = 'Manual'");

                var (status, body) = await PostAsUserAsync(page, $"{_fixture.BaseUrl}/JournalEntry/Create");
                AssertRejected(status, body, "POST /JournalEntry/Create");

                var after = await SeedFixture.ScalarAsync<long>(
                    @"SELECT count(*) FROM ""JournalEntries"" WHERE ""SourceDocType"" = 'Manual'");
                Assert.Equal(before, after);
            }
        }

        [Fact]
        public async Task 折讓單_打得開草稿但沒有確認鈕_直接確認也被擋()
        {
            var (context, page) = await SignInAsReadOnlyAsync();
            await using (context)
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/APDebitNote?searched=true");
                var open = page.Locator($"[data-url$='/APDebitNote/Details/{DraftNoteId}']");
                Assert.True(await open.CountAsync() > 0,
                    $"唯讀角色在列表上看不到草稿 {DraftNoteId}。可能是資料授權（CreatedByRoleId）把別的角色建的單藏起來了——" +
                    "那是另一個問題，但這支測試就走不下去。");

                // 列表列上與面板上都不該有確認／作廢
                Assert.Equal(0, await page.Locator("[data-action='confirmNote'], [data-action='voidNote']").CountAsync());
                await open.First.ClickAsync();
                await Assertions.Expect(page.Locator("#sidePanelBody code, #sidePanelBody dl, #sidePanelBody table").First)
                    .ToBeVisibleAsync();
                Assert.Equal(0, await page.Locator("#sidePanelBody [data-action='confirmNote']").CountAsync());

                var (status, body) = await PostAsUserAsync(page, $"{_fixture.BaseUrl}/APDebitNote/Confirm/{DraftNoteId}");
                AssertRejected(status, body, $"POST /APDebitNote/Confirm/{DraftNoteId}");

                var noteStatus = await SeedFixture.ScalarAsync<int>(
                    $@"SELECT ""Status"" FROM ""APDebitNotes"" WHERE ""APDebitNoteId"" = '{DraftNoteId}'");
                Assert.Equal(0, noteStatus);   // 仍是草稿
            }
        }

        [Fact]
        public async Task 沒有權限的畫面進不去()
        {
            var (context, page) = await SignInAsReadOnlyAsync();
            await using (context)
            {
                var response = await page.GotoAsync($"{_fixture.BaseUrl}/PurchaseOrder?searched=true");
                var landedOnTarget = page.Url.Contains("/PurchaseOrder", StringComparison.OrdinalIgnoreCase)
                                  && response is { Status: < 400 };
                Assert.False(landedOnTarget,
                    $"唯讀角色沒有 PurchaseOrder 的任何權限，卻打開了 {page.Url}（HTTP {response?.Status}）。");
            }
        }
    }
}
