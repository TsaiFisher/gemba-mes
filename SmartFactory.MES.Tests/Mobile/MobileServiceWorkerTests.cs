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
    /// 手機 PWA Service Worker（<c>wwwroot/mobile/sw.js</c>）的離線行為守門。
    ///
    /// <para><b>為什麼要用測試守住：</b>這一類缺陷只有「離線 ＋ 帶查詢字串 ＋ 該頁沒被預快取」
    /// 三個條件同時成立才看得到，正常開發與 UI 測試都在有網路的情況下跑，
    /// 永遠不會踩到。實際情形是這兩個缺陷從 Phase E1 一路存在到 2026-08-12 才被抓出來。</para>
    ///
    /// <para>與 <see cref="MobileI18nKeyTests"/> 相同，這裡用 regex 讀 JS 而不解析它——
    /// 需要驗的是「這幾個保護還在不在」，不是執行語意。</para>
    /// </summary>
    public class MobileServiceWorkerTests
    {
        private static string RepoRoot() => ManualPaths.RepoRoot();

        private static string SwPath() =>
            Path.Combine(RepoRoot(), "wwwroot", "mobile", "sw.js");

        private static string SwText() => File.ReadAllText(SwPath());

        /// <summary>取出 STATIC_ASSETS 陣列裡的每一個路徑字面值。</summary>
        private static List<string> StaticAssets()
        {
            var text = SwText();
            var start = text.IndexOf("const STATIC_ASSETS = [", StringComparison.Ordinal);
            Assert.True(start >= 0, "sw.js 找不到 STATIC_ASSETS 宣告，本測試需要同步調整。");
            var end = text.IndexOf("];", start, StringComparison.Ordinal);
            Assert.True(end > start, "sw.js 的 STATIC_ASSETS 陣列沒有結尾，解析失敗。");

            // 陣列裡混了字面值與 OFFLINE_PAGE 這個常數參照，先把常數換回它的值再解析，
            // 否則離線頁會被當成「不在預快取清單裡」。
            var offlinePage = Regex.Match(text, @"const OFFLINE_PAGE = '(/[^']+)';");
            Assert.True(offlinePage.Success, "sw.js 找不到 OFFLINE_PAGE 常數，本測試需要同步調整。");

            var segment = text[start..end].Replace("OFFLINE_PAGE", $"'{offlinePage.Groups[1].Value}'");
            var paths = Regex.Matches(segment, @"'(/[^']+)'")
                             .Select(m => m.Groups[1].Value)
                             .Distinct()
                             .ToList();

            // 健全性：引號風格或陣列寫法改掉時，下面所有斷言都會因為「空集合」而假綠。
            Assert.True(paths.Count > 10,
                $"只從 STATIC_ASSETS 解析到 {paths.Count} 筆路徑，解析方式已失效，本測試形同虛設。");
            return paths;
        }

        /// <summary>把 sw.js 的絕對路徑對應回 wwwroot 下的實體檔案。</summary>
        private static string ToPhysical(string urlPath) =>
            Path.Combine(RepoRoot(), "wwwroot", urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        [Fact]
        public void 導覽比對必須忽略查詢字串()
        {
            var text = SwText();

            // 現場最常走的動線都帶參數（sessions→reports、list→sessions、ipqc/fpi→ipqc-process）。
            // 少了 ignoreSearch，這些頁在離線時一律 cache miss，即使剛剛才看過同一頁。
            Assert.Contains("ignoreSearch", text);

            var navigate = ExtractFunction(text, "handleNavigate");
            Assert.Contains("ignoreSearch", navigate);
        }

        [Fact]
        public void 導覽失敗必須回離線替代頁而不是讓請求整個失敗()
        {
            var navigate = ExtractFunction(SwText(), "handleNavigate");

            // catch 區塊要真的回東西。沒接住的話 respondWith 收到 rejected promise，
            // 瀏覽器直接顯示原生錯誤頁——那一頁不會告訴使用者佇列中的報工還在。
            Assert.Contains("catch", navigate);
            Assert.Contains("OFFLINE_PAGE", navigate);
        }

        [Fact]
        public void 離線替代頁必須存在且已預快取()
        {
            var assets = StaticAssets();
            Assert.Contains("/mobile/offline.html", assets);
            Assert.True(File.Exists(ToPhysical("/mobile/offline.html")),
                "sw.js 指定了離線替代頁，但檔案不存在——離線時會退回 503 空白頁。");
        }

        [Fact]
        public void 除簽核外每個手機頁面與它引用的本機資源都必須預快取()
        {
            // 2026-10-05 離線實測：預快取只有 MFG 4 頁的 HTML，各頁 *.page.js 一支都沒有。
            // 沒先連線開過的頁面離線時 HTML 拿得到、腳本是 503——版面出得來、按鈕全死、資料不載入，
            // 現場看到的是一堆「網路錯誤」。HTML、腳本、樣式要三件一起進。
            // 簽核頁刻意不預快取（sw.js 檔頭 v13／v14：簽核絕不能離線做），所以排除。
            var assets = StaticAssets().ToHashSet(StringComparer.Ordinal);
            var mobileDir = Path.Combine(RepoRoot(), "wwwroot", "mobile");
            var missing = new List<string>();

            var pages = Directory.EnumerateFiles(mobileDir, "*.html", SearchOption.AllDirectories)
                .Select(f => "/mobile/" + Path.GetRelativePath(mobileDir, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(u => !u.StartsWith("/mobile/approval/", StringComparison.Ordinal))
                .ToList();
            Assert.True(pages.Count >= 15, $"只找到 {pages.Count} 個手機頁面，掃描方式已失效。");

            foreach (var page in pages)
            {
                if (!assets.Contains(page)) missing.Add(page);
                var html = File.ReadAllText(ToPhysical(page));
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(html, @"(?:src|href)=""(/(?:mobile|lib|js)/[^""?#]+\.(?:js|css))"""))
                    if (!assets.Contains(m.Groups[1].Value))
                        missing.Add($"{m.Groups[1].Value}（{page} 引用）");
            }

            Assert.True(missing.Count == 0,
                "下列手機頁面或其腳本／樣式不在 STATIC_ASSETS，沒連線開過就離線時會整頁失效：\n  "
                + string.Join("\n  ", missing.Distinct()));
        }

        [Fact]
        public void 預快取清單裡的每個檔案都必須真的存在()
        {
            // v19 起預快取逐檔盡力而為（原本 addAll 全有全無，一筆 404 整批作廢），
            // 所以打錯一個路徑不再拖垮全部——但那一檔會**永遠**缺席，只留一行 console.warn，
            // 正式環境沒有人看得到。離線替代頁例外：它缺了 install 直接失敗。
            var missing = StaticAssets()
                .Where(p => !File.Exists(ToPhysical(p)))
                .ToList();

            Assert.True(missing.Count == 0,
                "STATIC_ASSETS 有檔案不存在，會讓整批預快取靜默失敗：\n  " + string.Join("\n  ", missing));
        }

        [Fact]
        public void 離線替代頁只能依賴已預快取的資源()
        {
            // 離線頁自己要是引用了沒預快取的 CSS/JS，離線時它會是一頁沒有樣式的裸 HTML，
            // 反而比原生錯誤頁更難看懂。
            var html = File.ReadAllText(ToPhysical("/mobile/offline.html"));
            var assets = StaticAssets();

            var referenced = Regex.Matches(html, @"(?:src|href)=""(/[^""]+)""")
                                  .Select(m => m.Groups[1].Value)
                                  .Where(p => p.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                                           || p.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                                  .Distinct()
                                  .ToList();

            Assert.NotEmpty(referenced);

            var notCached = referenced.Where(p => !assets.Contains(p)).ToList();
            Assert.True(notCached.Count == 0,
                "offline.html 引用了未預快取的資源，離線時會缺樣式或缺行為：\n  " + string.Join("\n  ", notCached));
        }

        [Fact]
        public void 預快取必須跳過瀏覽器HTTP快取()
        {
            // 2026-10-09 Android 實測 P-5：bump 版本名後手機裝了新 SW，但 cache.add(url) 走預設快取模式，
            // 靜態檔又沒有 Cache-Control，約 80 個檔只有 1 個打到伺服器，其餘全是 HTTP 快取裡的舊檔——
            // 改版等於沒送出去。install 裡每一個 cache.add 都必須帶 cache:'reload' 的 Request。
            var text = StripComments(SwText());
            var start = text.IndexOf("addEventListener('install'", StringComparison.Ordinal);
            Assert.True(start >= 0, "sw.js 找不到 install 處理器，本測試需要同步調整。");
            var end = text.IndexOf("addEventListener('activate'", start, StringComparison.Ordinal);
            var install = text[start..(end < 0 ? text.Length : end)];

            Assert.Contains("cache: 'reload'", install);
            var bare = Regex.Matches(install, @"cache\.add(?:All)?\(\s*(?!fresh\()([^)]*)\)")
                            .Select(m => m.Value).ToList();
            Assert.True(bare.Count == 0,
                "install 裡有 cache.add 沒經過 fresh()（cache:'reload'），會收到 HTTP 快取的舊檔：\n  "
                + string.Join("\n  ", bare));
        }

        [Fact]
        public void 改動預快取內容時必須同時提高快取版本號()
        {
            // Cache-First ＋ 舊版本號 ＝ 既有裝置永遠拿到舊檔，而且完全沒有徵兆。
            // 這裡只能守住「版本號長得對」，真正的紀律靠 sw.js 檔頭那段註解。
            var m = Regex.Match(SwText(), @"const CACHE_NAME = 'mes-mobile-v(\d+)';");
            Assert.True(m.Success, "sw.js 的 CACHE_NAME 命名格式改了（預期 mes-mobile-vN），本測試需要同步調整。");
            Assert.True(int.Parse(m.Groups[1].Value) >= 12,
                "預快取內容已於 v11 變更（offline.html／data-action.js）、v12 再動（主檔快取保留），版本號不得倒退。");
        }

        [Fact]
        public void 每個開起來的快取都必須列在_activate_的保留清單裡()
        {
            // activate 的清理是「保留清單以外一律刪」，所以**漏列一個快取 = 每次 SW 更新
            // 都把它清空**，而且沒有任何錯誤訊息。
            //
            // 實際踩過（2026-09-02 修）：MASTER_CACHE 原本是 handleMasterCache 的區域常數，
            // activate 看不到它，於是主檔快取每次版本更新都被刪掉。症狀是「主檔離線看不到」，
            // 但看起來像 30 分鐘 TTL 到期——**快取被刪與快取過期在畫面上長得一模一樣**。
            // 而檔頭又明文要求「改預快取檔案就必須 bump」，等於更新越勤、主檔快取活得越短。
            //
            // 判準刻意用 `caches.open(...)` 而不是「所有 mes-* 字面值」：後者會誤抓
            // 推播通知的 tag（'mes-push'），那不是快取，不該進保留清單。
            // 要守的不變量是「開得起來的快取都要被保留」，就照字面查開了哪些。
            var text = SwText();

            // v30 起 API 一律 Network-Only（斷線保護），只剩殼層 CACHE_NAME 一個快取；
            // 日後若再加快取，這條會繼續把它跟保留清單對起來。
            var opened = Regex.Matches(text, @"caches\.open\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)")
                .Select(m => m.Groups[1].Value)
                .Distinct().ToList();
            Assert.True(opened.Contains("CACHE_NAME"),
                $"找不到 caches.open(CACHE_NAME)（只找到 {string.Join(", ", opened)}），寫法可能改了，本測試需同步調整。");

            var activate = ActivateBody(text);
            var unkept = opened.Where(c => !activate.Contains(c, StringComparison.Ordinal)).ToList();

            Assert.True(unkept.Count == 0,
                "以下快取被 caches.open() 開啟卻沒列在 activate 的保留清單，"
                + "每次 SW 更新都會被清空（無任何徵兆）：\n  " + string.Join("\n  ", unkept));
        }

        [Fact]
        public void API一律不經SW快取_連不上就是連不上()
        {
            // v24–v29 的派工／機台查詢「網路優先、連不上回快取」：只有開過的那幾筆、而且是舊的，
            // 現場看了會以為手機可以離線作業（2026-10-10 改為斷線保護，mobile-pwa.md §5.2）。
            // 讀取一律要連線，連不上由 api.js 的 apiGet 提示「需要連線」。
            //
            // 守的是：fetch 處理器裡**不得再有任何 /api/mobile/... 的快取分支**，
            // 而且 /api/ 一律直接 return（不呼叫 respondWith）。
            // ⚠️ 比對前先剝掉註解——檔頭變更紀錄會提到當年的分支。
            var code = StripComments(SwText());

            var apiBranches = Regex.Matches(code, @"(?:startsWith\(|pathname\s*===\s*)'(/api/[^']+)'")
                .Select(m => m.Groups[1].Value)
                .Where(p => p != "/api/")
                .Distinct().ToList();
            Assert.True(apiBranches.Count == 0,
                "sw.js 又出現了 API 的快取分支（讀取必須一律走網路）：\n  " + string.Join("\n  ", apiBranches));

            Assert.Matches(@"startsWith\('/api/'\)\)\s*return;", code);
        }

        [Fact]
        public void 啟動網址必須落在SW作用域內且已預快取()
        {
            // PWA 從主畫面圖示啟動時走的是 manifest 的 start_url，**不是**任何人點過的連結。
            // 它有兩個條件要同時成立，而兩個都只在「已安裝 ＋ 離線 ＋ 冷啟動」時才看得出來——
            // 線上、或在瀏覽器分頁裡測，一切正常。
            //
            // ① **作用域**：SW 的 scope 比對是**字串前綴**，不是路徑層級。scope 為 `/mobile/`
            //    時，`/mobile`（少一個尾斜線）不在作用域內 → 那一次導覽完全不經過 SW →
            //    離線時是瀏覽器原生錯誤頁，連 offline.html 都到不了。這正是
            //    <see cref="導覽失敗必須回離線替代頁而不是讓請求整個失敗"/> 想避免的畫面，
            //    卻從**最常走的那一條進入路徑**整個繞過去。
            // ② **快取鍵**：即使在作用域內，handleNavigate 的 ignoreSearch 只忽略查詢字串、
            //    不忽略路徑。start_url 沒被預快取就必定 miss。
            //
            // 實際踩過（2026-09-24 修）：start_url 是 `/mobile`，線上靠 Program.cs 的 302
            // 轉到 index.html，所以完全看不出問題；離線冷啟動則兩個條件一起不成立。
            var startUrl = JsonDocument
                .Parse(File.ReadAllText(ToPhysical("/mobile/manifest.json")))
                .RootElement.GetProperty("start_url").GetString();

            Assert.False(string.IsNullOrWhiteSpace(startUrl), "manifest.json 沒有 start_url。");

            // 作用域從**註冊端**讀，不在測試裡另抄一份字面值：抄一份的話，
            // 有人改了 register() 的 scope 而這裡沒跟著改，測試會照樣綠。
            var scopes = new[] { "/mobile/index.page.js", "/mobile/home.page.js" }
                .SelectMany(f => Regex.Matches(File.ReadAllText(ToPhysical(f)), @"scope:\s*'(/[^']*)'")
                                      .Select(m => m.Groups[1].Value))
                .Distinct()
                .ToList();

            Assert.True(scopes.Count == 1,
                "兩支註冊 SW 的 page.js 給的 scope 不一致（或解析失敗）：["
                + string.Join(", ", scopes) + "]。兩處必須同值，否則實際作用域取決於誰先註冊。");

            var scope = scopes[0];
            Assert.True(startUrl!.StartsWith(scope, StringComparison.Ordinal),
                $"manifest 的 start_url（{startUrl}）不在 SW 作用域（{scope}）內。"
                + "作用域是字串前綴比對，少一個尾斜線就是兩個不同的東西——"
                + "從主畫面圖示啟動的那一次導覽不會經過 SW，離線時會落到瀏覽器原生錯誤頁。");

            Assert.True(StaticAssets().Contains(startUrl),
                $"manifest 的 start_url（{startUrl}）不在 STATIC_ASSETS 裡。"
                + "離線冷啟動時 handleNavigate 找不到這個鍵（ignoreSearch 只忽略查詢字串不忽略路徑），"
                + "已安裝的 PWA 一開就是離線頁。");
        }

        /// <summary>activate 監聽器的主體（到下一個 addEventListener 為止）。</summary>
        private static string ActivateBody(string text)
        {
            var start = text.IndexOf("addEventListener('activate'", StringComparison.Ordinal);
            Assert.True(start >= 0, "sw.js 找不到 activate 監聽器，本測試需要同步調整。");
            var next = text.IndexOf("addEventListener('fetch'", start, StringComparison.Ordinal);
            return next < 0 ? text[start..] : text[start..next];
        }

        /// <summary>剝掉 // 與 /* */ 註解，避免把說明文字當成程式碼比對。</summary>
        private static string StripComments(string text)
            => Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline),
                             @"//[^\n]*", "");

        /// <summary>粗略截出某個 async function 的主體，供區域性斷言用。</summary>
        private static string ExtractFunction(string text, string name)
        {
            var start = text.IndexOf($"async function {name}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"sw.js 找不到 {name}()，本測試需要同步調整。");
            var next = text.IndexOf("\nasync function ", start + 1, StringComparison.Ordinal);
            return next < 0 ? text[start..] : text[start..next];
        }
    }
}
