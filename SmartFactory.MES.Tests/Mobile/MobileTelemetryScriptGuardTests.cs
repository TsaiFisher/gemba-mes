using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile
{
    /// <summary>
    /// 每一支會打 API 的手機頁都要<b>第一個</b>載入 telemetry.js。
    ///
    /// <para>它要包住 window.fetch（api.js、offline-queue.js 都用它），也要在其他腳本出錯前就掛好 onerror；
    /// 排在後面就量不到前面腳本的錯誤，排漏了那一頁在「現場遙測」上就是一片空白——
    /// 而空白看起來跟「那一頁很健康」一模一樣。新增手機頁時最容易漏的就是這一行。</para>
    /// </summary>
    public class MobileTelemetryScriptGuardTests
    {
        [Fact]
        public void 載入api_js的手機頁_都第一個載入telemetry_js()
        {
            var violations = new List<string>();
            var scanned = 0;
            foreach (var html in ConventionScan.Files("wwwroot/mobile", "*.html"))
            {
                var text = File.ReadAllText(html);
                var api = text.IndexOf("/mobile/js/api.js", StringComparison.Ordinal);
                if (api < 0) continue;                          // 不打 API 的頁（離線備援頁）不需要
                scanned++;

                var tel = text.IndexOf("/mobile/js/telemetry.js", StringComparison.Ordinal);
                var firstScript = text.IndexOf("<script", StringComparison.Ordinal);
                if (tel < 0)
                    violations.Add($"{ConventionScan.Rel(html)}：沒有載入 telemetry.js");
                else if (text.LastIndexOf("<script", tel, StringComparison.Ordinal) != firstScript)
                    violations.Add($"{ConventionScan.Rel(html)}：telemetry.js 不是第一個 <script>");
            }
            // 一頁都沒掃到也會是綠的（路徑錯、檔案搬家）——那樣這支測試什麼都沒守
            Assert.True(scanned >= 20, $"只掃到 {scanned} 支載入 api.js 的手機頁，掃描範圍可能錯了");
            Assert.True(violations.Count == 0, "手機頁遙測腳本：\n  " + string.Join("\n  ", violations));
        }
    }
}
