using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機 PWA 靜態檔必須帶 <c>Cache-Control: no-cache</c>。
    /// 2026-10-09 Android 實測 P-5：靜態檔只有 ETag／Last-Modified，瀏覽器依啟發式新鮮度直接用舊檔，
    /// bump sw.js 版本名之後預快取約 80 個檔只有 1 個打到伺服器——改版送不到手機。
    /// 從真的管線驗標頭有跟著靜態檔一起送出（Core 由 Program.cs、部署專案可另以模組設定）。
    ///
    /// 掃 <c>wwwroot/mobile</c> 底下實際存在的 html／js，而不是寫死檔名：Core 只有首頁兩頁、
    /// 部署專案另有 sw.js 與各模組頁，同一支測試兩邊都適用，新增的頁面也自動納入。
    /// </summary>
    [Collection("Integration")]
    public class MobileStaticCacheHeaderTests
    {
        private readonly TestWebApplicationFactory _factory;

        public MobileStaticCacheHeaderTests(TestWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task 手機靜態檔必須要求每次重驗()
        {
            var webRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
            var mobileRoot = Path.Combine(webRoot, "mobile");
            var paths = Directory.EnumerateFiles(mobileRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}__tests__{Path.DirectorySeparatorChar}"))
                .Select(f => "/" + Path.GetRelativePath(webRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // 掃描器自我檢查：一個都沒找到＝路徑算錯，規則等於關閉
            Assert.NotEmpty(paths);

            var client = _factory.CreateClient();
            var missing = new List<string>();
            foreach (var path in paths)
            {
                var resp = await client.GetAsync(path);
                if (!resp.IsSuccessStatusCode) { missing.Add($"{path}（HTTP {(int)resp.StatusCode}）"); continue; }
                if (resp.Headers.CacheControl?.NoCache != true)
                    missing.Add($"{path}（實際：{resp.Headers.CacheControl}）");
            }

            Assert.True(missing.Count == 0,
                "以下手機靜態檔沒有 Cache-Control: no-cache，改版後手機會沿用 HTTP 快取裡的舊檔：\n"
                + string.Join("\n", missing));
        }
    }
}
