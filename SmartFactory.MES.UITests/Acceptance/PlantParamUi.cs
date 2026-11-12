using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 用桌機「廠別」畫面設定廠別參數。value＝null 代表取消覆寫（回到繼承）。
    /// 必須走畫面存檔：PlantParamService 有 10 分鐘快取，直接改 PlantParams 資料表 app 看不到。
    /// </summary>
    internal static class PlantParamUi
    {
        public static async Task SetAsync(UiTestFixture fixture, string plant, params (string Key, string? Value)[] kv)
        {
            var page = await fixture.NewPageAsync();
            await fixture.SignInAsync(page);
            await page.GotoAsync($"{fixture.BaseUrl}/Plant?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.Locator($"[data-url$='/Plant/Edit/{plant}']").First.ClickAsync();
            await page.WaitForSelectorAsync("#crudPanelBody #panelForm");

            foreach (var (key, value) in kv)
            {
                var ok = await page.EvaluateAsync<bool>(@"([key, value]) => {
                    const ovr = document.getElementById('ovr_' + key);
                    const val = document.getElementById('val_' + key);
                    if (!ovr || !val) return false;
                    ovr.checked = value !== null;
                    if (value !== null) {
                        if (val.type === 'checkbox') val.checked = value === 'true';
                        else val.value = value;
                    }
                    return true;
                }", new object?[] { key, value });
                Assert.True(ok, $"廠別參數畫面找不到 {key}");
            }

            var resp = page.WaitForResponseAsync(r => r.Request.Method == "POST" && r.Url.Contains("/Plant/Edit"));
            await page.Locator("#crudPanelBody #panelForm button[type='submit']").Last.ClickAsync();
            var r = await resp;
            Assert.True(r.Ok, $"廠別參數存檔失敗（HTTP {r.Status}）：{await r.TextAsync()}");
            await page.CloseAsync();

            foreach (var (key, value) in kv)
            {
                var stored = await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "Value" FROM "PlantParams" WHERE "Key"='{key}' AND "PlantId"='{plant}'""");
                Assert.True(value == null ? stored == null : string.Equals(stored, value, StringComparison.OrdinalIgnoreCase),
                    $"廠別參數 {key} 存檔後是「{stored}」，預期「{value}」");
            }
        }

    }
}
