using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartFactory.MES.Tests.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile
{
    /// <summary>
    /// 手機 PWA「外殼」的守門：主畫面圖示與兩個**順序**約束。
    ///
    /// <para>這三件事的共通點是 <b>失效時完全沒有錯誤訊息</b>，而且只在特定裝置上發生：
    /// 圖示缺的是 iOS 專用的那一個、權限請求的時序問題只在 WebKit 上炸、
    /// 佇列徽章的缺口只在**離線**時看得到。三者都不可能靠桌機開發或 Chromium 的
    /// Playwright 發現，所以改用讀原始碼的方式釘住。</para>
    ///
    /// <para>與 <see cref="MobileServiceWorkerTests"/> 的分工：那邊守 <c>sw.js</c> 的快取語意，
    /// 這邊守的是頁面與 manifest 這一層。</para>
    /// </summary>
    public class MobilePwaShellTests
    {
        private static string MobileDir() => Path.Combine(ManualPaths.RepoRoot(), "wwwroot", "mobile");

        private static string ToPhysical(string urlPath) =>
            Path.Combine(ManualPaths.RepoRoot(), "wwwroot",
                urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        private static IEnumerable<string> PagesWithManifest() =>
            Directory.EnumerateFiles(MobileDir(), "*.html", SearchOption.AllDirectories)
                     .Where(f => File.ReadAllText(f).Contains("rel=\"manifest\"", StringComparison.Ordinal));

        /// <summary>PNG 檔頭；用來確認圖示不是改了副檔名的 SVG。</summary>
        private static void AssertIsPng(string physical, string label)
        {
            Assert.True(File.Exists(physical), $"{label} 指向的檔案不存在：{physical}");
            var head = new byte[8];
            using (var fs = File.OpenRead(physical)) fs.ReadExactly(head, 0, 8);
            Assert.True(head.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                $"{label} 不是 PNG（{physical}）。iOS 與 Android 的安裝流程都不吃改了副檔名的 SVG。");
        }

        /// <summary>
        /// 剝掉註解再比對順序。⚠️ 這不是可有可無的：程式裡**解釋這條規則本身**的註解
        /// （「這一段原本排在 requireAuth() 之後」）會被當成程式碼抓出來，
        /// 讓修好的順序照樣判成違規。初版就踩到了，`MobileServiceWorkerTests`
        /// 的死路由那支當年也踩過同一個。home.page.js 內沒有含 `//` 的字串字面值。
        /// </summary>
        private static string StripComments(string text)
            => Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline),
                             @"//[^\n]*", "");

        [Fact]
        public void 每個掛了manifest的手機頁面都必須有iOS主畫面圖示()
        {
            // iOS 的「加入主畫面」**不讀 manifest 的 icons**，只認 <link rel="apple-touch-icon">，
            // 而且不支援 SVG。沒有這一行時 iOS 會拿頁面縮圖或白底當圖示——
            // 桌機、Android、Playwright 全部看不出異常，只有拿 iPhone 裝過的人才會發現。
            //
            // 判準綁在「有 manifest 的頁」而不是「所有 html」：純轉址頁
            // （workorderexecution/list.html 是一個 meta refresh）沒有安裝意義，不該被強迫。
            var pages = PagesWithManifest().ToList();
            Assert.True(pages.Count >= 15,
                $"只找到 {pages.Count} 頁掛了 manifest，解析多半壞了，本測試會形同虛設。");

            var missing = pages.Where(f => !File.ReadAllText(f).Contains("apple-touch-icon", StringComparison.Ordinal))
                               .Select(f => Path.GetRelativePath(ManualPaths.RepoRoot(), f))
                               .ToList();

            Assert.True(missing.Count == 0,
                "以下手機頁面沒有 apple-touch-icon，從這幾頁加到 iOS 主畫面會拿到白底或頁面縮圖：\n  "
                + string.Join("\n  ", missing));

            // 指到的檔要真的存在、且真的是 PNG。
            foreach (var f in pages)
            {
                var m = Regex.Match(File.ReadAllText(f), @"rel=""apple-touch-icon""[^>]*href=""(/[^""]+)""");
                Assert.True(m.Success, $"{Path.GetRelativePath(ManualPaths.RepoRoot(), f)} 的 apple-touch-icon 沒有 href。");
                AssertIsPng(ToPhysical(m.Groups[1].Value), "apple-touch-icon");
            }
        }

        [Fact]
        public void manifest必須提供192與512的PNG圖示()
        {
            // Android 的安裝條件與圖示品質都看 manifest 的 icons。原本整份只有一張 SVG，
            // 而 SVG 在各家的支援度不一致——這種「可能可以、可能不行」的東西不該是唯一的那一份。
            var icons = JsonDocument.Parse(File.ReadAllText(ToPhysical("/mobile/manifest.json")))
                .RootElement.GetProperty("icons").EnumerateArray()
                .Select(e => (
                    Src: e.GetProperty("src").GetString() ?? "",
                    Sizes: e.TryGetProperty("sizes", out var s) ? s.GetString() ?? "" : "",
                    Type: e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : ""))
                .ToList();

            foreach (var want in new[] { "192x192", "512x512" })
            {
                var hit = icons.FirstOrDefault(i => i.Sizes == want && i.Type == "image/png");
                Assert.False(string.IsNullOrEmpty(hit.Src),
                    $"manifest 沒有 {want} 的 PNG 圖示。Android 的安裝提示與主畫面圖示都取自這裡。");
                AssertIsPng(ToPhysical(hit.Src), $"manifest 的 {want} 圖示");
            }
        }

        [Fact]
        public void 離線佇列初始化必須排在requireAuth之前()
        {
            // 離線時 requireAuth() 的 /auth/me 必然失敗 → 回 null → 主流程 early return。
            // 佇列初始化若排在它後面，**離線時右下角的待同步徽章永遠不會出現**——
            // 而「還有幾筆沒送出」正是離線最需要顯示的一件事。
            // 佇列是純本地的 IndexedDB，讀它不需要任何認證，所以它不該排在認證後面。
            var text = StripComments(File.ReadAllText(Path.Combine(MobileDir(), "home.page.js")));

            var queue = text.IndexOf("offlineQueue.ready()", StringComparison.Ordinal);
            var auth  = text.IndexOf("requireAuth()", StringComparison.Ordinal);

            Assert.True(queue >= 0, "home.page.js 找不到 offlineQueue.ready()，本測試需要同步調整。");
            Assert.True(auth  >= 0, "home.page.js 找不到 requireAuth()，本測試需要同步調整。");
            Assert.True(queue < auth,
                "home.page.js 的離線佇列初始化排在 requireAuth() 之後："
                + "離線時主流程會在認證那一步 return，佇列徽章一次都不會出現。");
        }

        [Fact]
        public void 推播權限請求必須排在togglePush的第一個await之前()
        {
            // WebKit 要求 Notification.requestPermission() 落在使用者手勢的有效期內。
            // 先 await 一次後端（原本是去拿 VAPID 公鑰）就足以讓手勢失效 →
            // **iOS 上按鈴鐺完全沒有反應**：沒有對話框、沒有錯誤、沒有 Toast。
            // Android 的手勢有效期較寬鬆而照常運作，於是這個缺陷只在一半的裝置上出現。
            var text = StripComments(File.ReadAllText(Path.Combine(MobileDir(), "home.page.js")));

            var start = text.IndexOf("async function togglePush(", StringComparison.Ordinal);
            Assert.True(start >= 0, "home.page.js 找不到 togglePush()，本測試需要同步調整。");
            var next = text.IndexOf("async function ", start + 20, StringComparison.Ordinal);
            var body = next < 0 ? text[start..] : text[start..next];

            var permission = body.IndexOf("Notification.requestPermission", StringComparison.Ordinal);
            Assert.True(permission >= 0, "togglePush() 裡找不到 Notification.requestPermission()。");

            // 只看「訂閱」那條路上的網路往返：unsubscribe 分支的 await 排在前面是正常的
            // （那條路不需要權限），所以比對的對象是取 VAPID 公鑰那一次。
            var vapid = body.IndexOf("push/vapid-key", StringComparison.Ordinal);
            Assert.True(vapid >= 0, "togglePush() 裡找不到取 VAPID 公鑰的呼叫，本測試需要同步調整。");

            Assert.True(permission < vapid,
                "togglePush() 先 await 了一次後端才呼叫 Notification.requestPermission()："
                + "iOS 會因為使用者手勢已過期而靜默失敗（按了沒反應），Android 則正常——"
                + "這種只壞一半裝置的缺陷不會有人回報成「推播壞了」。");
        }
    }
}
