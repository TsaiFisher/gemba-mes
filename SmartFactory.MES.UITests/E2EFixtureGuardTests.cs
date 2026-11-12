using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// E2E 種子（<c>scripts/seed-e2e-fixture.sql</c>）的守門測試。
    ///
    /// 為什麼需要：種子是手寫 SQL，schema 一旦變動（欄位改名、新增 NOT NULL、
    /// 唯一索引調整）腳本會靜默地少建東西，而流程測試的失敗症狀是「下拉沒有選項」
    /// 或「查無資料」——離真因很遠，很難追。這裡直接斷言關鍵資料在畫面上看得到，
    /// 種子壞掉時第一個紅的是這支，訊息明確指向種子腳本。
    ///
    /// 刻意透過畫面而非直連資料庫驗證，因為這樣會一併驗到資料授權：
    /// IDataAuthEntity 的單據若漏設 CreatedByRoleId，資料存在但全域過濾會把它藏起來，
    /// 查詢結果是空的、開單據直接 404。只查 SQL 是驗不到這件事的。
    /// </summary>
    [Collection("UI Tests")]
    public class E2EFixtureGuardTests
    {
        private const string SeedHint =
            "請確認已對測試資料庫執行 scripts/seed-e2e-fixture.sql（CI 由 nightly.yml 的 " +
            "\"Seed E2E fixture\" 步驟負責）。若已執行仍失敗，多半是 schema 變動導致種子少建資料。";

        private readonly UiTestFixture _fixture;

        public E2EFixtureGuardTests(UiTestFixture fixture) => _fixture = fixture;

        private async Task<IPage> GotoAsync(string path)
        {
            var page = await _fixture.NewLoggedInPageAsync();
            await page.GotoAsync($"{_fixture.BaseUrl}{path}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            return page;
        }

        [Theory]
        [InlineData("/Product",     "?searched=true&searchTerm=E2E-FG01", "E2E-FG01", "品號")]
        [InlineData("/Product",     "?searched=true&searchTerm=E2E-RM01", "E2E-RM01", "品號")]
        // P2P 流程用的免驗直放料號；沒有它，入庫上架會被品質隔離閘門擋在待驗倉。
        [InlineData("/Product",     "?searched=true&searchTerm=E2E-PP01", "E2E-PP01", "免驗直放品號")]
        [InlineData("/Warehouse",   "?searched=true&searchTerm=E2E-WSTG", "E2E-WSTG", "備料倉")]
        [InlineData("/WorkCenter",  "?searched=true&searchTerm=E2E-WC01", "E2E-WC01", "工作中心")]
        // 稅別：採購／銷售明細「有金額就必須指定稅別」，缺了會讓所有單據存不了檔。
        [InlineData("/Tax",         "?searched=true&searchTerm=E2E-TAX",  "E2E-TAX",  "稅別")]
        // 銀行帳戶：付款／收款的資金出入口，缺了 P2P/O2C 的最後一步走不完。
        [InlineData("/BankAccount", "?searched=true&search=E2E-BANK",     "E2E-BANK", "銀行帳戶")]
        public async Task SeededMasterData_IsVisibleOnItsScreen(
            string indexPath, string query, string expectedCode, string what)
        {
            var page = await GotoAsync(indexPath + query);
            var body = await page.InnerTextAsync("body");

            Assert.True(body.Contains(expectedCode, StringComparison.OrdinalIgnoreCase),
                $"{what} {expectedCode} 沒有出現在 {indexPath}。{SeedHint}");
        }

        /// <summary>
        /// 工單與派工是流程測試的起點，且都是 IDataAuthEntity——這支同時驗資料存在
        /// 與資料授權可見。
        /// </summary>
        [Theory]
        [InlineData("/WorkOrder", "E2E-WO0001", "工單")]
        [InlineData("/Dispatch",  "E2E-DP0001", "派工單")]
        public async Task SeededDocument_IsVisibleAndNotHiddenByDataAuth(
            string indexPath, string docId, string what)
        {
            var page = await GotoAsync($"{indexPath}?searched=true&searchText={docId}&pageSize=50");
            var body = await page.InnerTextAsync("body");

            Assert.True(body.Contains(docId, StringComparison.OrdinalIgnoreCase),
                $"{what} {docId} 查不到。可能是種子未執行，也可能是 CreatedByRoleId 未設定" +
                $"而被資料授權過濾隱藏。{SeedHint}");
        }

        /// <summary>
        /// 會計期間為空時，成本相關畫面的期別下拉會是空的（乾淨資料庫的
        /// AccountingPeriods 沒有任何 seeder）。這支確保種子把期間補上了。
        /// </summary>
        [Fact]
        public async Task SeededAccountingPeriods_AreSelectable()
        {
            var page = await GotoAsync("/AccountingPeriod?searched=true");
            var rows = await page.Locator("button[data-action='startClosing']").CountAsync();

            Assert.True(rows > 0,
                $"會計期間一筆都沒有，成本相關畫面的期別下拉會是空的。{SeedHint}");
        }
    }
}
