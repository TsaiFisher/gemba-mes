using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>
    /// 廠商單價目錄（PUR / pur-domain.md §2）CRUD smoke test。
    ///
    /// 這支的用意不只是再加一個畫面：它是全站第一個帶「生效起訖」欄位對的
    /// CrudSmokeTestBase 派生類，樣板裡的 ⑬ 日期區間倒置在它出現之前
    /// 探測不到任何畫面（BAS 16 個主檔畫面沒有一個有 From/To 日期對），
    /// 等於那條斷言一直是空跑的。
    ///
    /// 三個必填皆為 FK 下拉，一律取現有的第一筆選項，不寫死任何代碼——
    /// 乾淨部署沒有主檔，寫死代碼的測試會在 SelectOptionAsync 空等到逾時，
    /// 而症狀（30 秒逾時）離真因（那筆主檔不存在）很遠。
    /// </summary>
    public class VendorPriceListSmokeTests : CrudSmokeTestBase
    {
        private string? _supplier;
        private string? _product;
        private string? _validFrom;

        public VendorPriceListSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/VendorPriceList";
        protected override string NewButtonSelector  => "button[data-url$='/VendorPriceList/Create']";
        protected override string PanelReadySelector => "select[name='SupplierCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.SelectOptionAsync($"{PanelBodySel} select[name='SupplierCode']", new SelectOptionValue { Index = 1 });
            await page.SelectOptionAsync($"{PanelBodySel} select[name='ProductCode']",  new SelectOptionValue { Index = 1 });
            await page.SelectOptionAsync($"{PanelBodySel} select[name='CurrencyCode']", new SelectOptionValue { Index = 1 });

            _supplier = await page.InputValueAsync($"{PanelBodySel} select[name='SupplierCode']");
            _product  = await page.InputValueAsync($"{PanelBodySel} select[name='ProductCode']");

            // 唯一鍵含生效起日：起日必須每次不同，否則同一輪內第二個新增測試會撞唯一鍵，
            // 被判成 ValidationError → 看起來像驗證出錯，其實是測試自己撞的。
            // 用 2090 這種不可能與真實資料重疊的年份，順便讓清理時認得出是誰建的。
            // 不再用「尾碼 % 365」：分鐘＋序號取模會撞號（2026-11-07 nightly 撞唯一鍵 6 次），
            // 清理失敗留下的舊列也會撞。改成取庫內 2090 年起已用到的最大起日再往後一天。
            var maxUsed = await SeedFixture.ScalarAsync<string>(
                """SELECT to_char(max("ValidFrom"), 'YYYY-MM-DD') FROM "VendorPriceLists" WHERE "ValidFrom" >= '2090-01-01'""");
            var next = string.IsNullOrEmpty(maxUsed) ? new DateTime(2090, 1, 1) : DateTime.Parse(maxUsed).AddDays(1);
            _validFrom = next.ToString("yyyy-MM-dd");

            await page.FillAsync($"{PanelBodySel} input[name='ValidFrom']", _validFrom);
            await page.FillAsync($"{PanelBodySel} input[name='UnitPrice']", "100");
        }

        protected override async Task CleanupAsync(IPage page, string uniqueSuffix)
        {
            try
            {
                if (string.IsNullOrEmpty(_supplier) || string.IsNullOrEmpty(_product) ||
                    string.IsNullOrEmpty(_validFrom)) return;

                await page.GotoAsync(
                    $"{_fixture.BaseUrl}{IndexPath}?searched=true&activeOnly=false" +
                    $"&supplierCode={Uri.EscapeDataString(_supplier)}" +
                    $"&productCode={Uri.EscapeDataString(_product)}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                // 只刪掉這一輪自己建的那筆（以生效起日認人），不動測試庫既有資料
                var row = page.Locator($"tr:has-text('{_validFrom}')");
                if (await row.CountAsync() == 0) return;

                var deleteBtn = row.First.Locator("button[data-action='deleteItem']");
                if (await deleteBtn.CountAsync() == 0) return;

                void OnDialog(object? _, IDialog d) => _ = d.AcceptAsync();
                page.Dialog += OnDialog;
                try
                {
                    await deleteBtn.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                }
                finally
                {
                    page.Dialog -= OnDialog;
                }
            }
            catch { /* best-effort */ }
        }
    }
}
