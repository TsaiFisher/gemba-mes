using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SmartFactory.MES.Tests.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile
{
    /// <summary>
    /// 手機前端寫死的 API 網址，必須對得上後端真的存在的路由。
    ///
    /// <para><b>為什麼要用測試守住：</b>2026-10-09 Android 實測才發現首件檢驗送出永遠 404——
    /// <c>ipqc-process.page.js</c> 打 <c>/api/mobile/mfg/inspection-orders</c>，實際路由是
    /// <c>/api/mobile/inspection-orders</c>，自 7/16 上線起就錯。整合測試直接呼叫 Controller 方法、
    /// 手機冒煙測試把 API 全部 route 掉，兩邊都碰不到「網址字串」本身，所以零守門。</para>
    ///
    /// <para>做法同 <see cref="MobileServiceWorkerTests"/>：用 regex 讀原始碼，不解析語意。
    /// 後端收 <c>[Route]</c>（類別層，partial 依類別名稱合併）＋ <c>[HttpXxx("…")]</c> 組成完整樣板；
    /// 前端收 <c>'/api/…'</c> 與 <c>`/api/…`</c> 字面值，<c>${…}</c> 視為一段參數，查詢字串去掉，
    /// 以 <c>/</c> 結尾的（後面接字串串接）只要求是某條路由的前綴。</para>
    /// </summary>
    public class MobileApiUrlRouteTests
    {
        private static readonly Regex ClassRoute = new(
            @"\[Route\(""([^""]*)""\)\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:sealed\s+|abstract\s+)*(?:partial\s+)?class\s+(\w+)");
        private static readonly Regex ClassDecl = new(@"public\s+(?:sealed\s+|abstract\s+)*(?:partial\s+)?class\s+(\w+)");
        private static readonly Regex HttpAttr = new(@"\[Http(?:Get|Post|Put|Delete|Patch)(?:\(""([^""]*)""[^)]*\))?\]");

        /// <summary>後端所有以 api/ 開頭的路由樣板（前置斜線、無結尾斜線）。</summary>
        private static List<string> BackendRoutes()
        {
            var root = ManualPaths.RepoRoot();
            var files = new[] { "Api", "Controllers", "Modules" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
                .Select(f => (Path: f, Text: File.ReadAllText(f)))
                .ToList();

            // partial class 只有一個檔案掛 [Route]，其餘檔案的動作要沿用它
            var baseByClass = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, text) in files)
                foreach (System.Text.RegularExpressions.Match m in ClassRoute.Matches(text))
                    baseByClass[m.Groups[2].Value] = m.Groups[1].Value;

            var routes = new List<string>();
            foreach (var (_, text) in files)
            {
                var decl = ClassDecl.Match(text);
                var classBase = decl.Success && baseByClass.TryGetValue(decl.Groups[1].Value, out var b) ? b : "";
                foreach (System.Text.RegularExpressions.Match a in HttpAttr.Matches(text))
                {
                    var tpl = a.Groups[1].Success ? a.Groups[1].Value : "";
                    var full = tpl.StartsWith("~/", StringComparison.Ordinal) ? tpl[2..]
                             : tpl.StartsWith('/') ? tpl[1..]
                             : tpl.Length == 0 ? classBase
                             : classBase + "/" + tpl;
                    full = "/" + full.Trim('/');
                    if (full.StartsWith("/api/", StringComparison.Ordinal)) routes.Add(full);
                }
            }
            return routes.Distinct().ToList();
        }

        /// <summary>手機前端（不含 sw.js 與單元測試）寫死的 API 網址：(檔案, 原始字面值)。</summary>
        private static List<(string File, string Url)> FrontendUrls()
        {
            var mobileDir = Path.Combine(ManualPaths.RepoRoot(), "wwwroot", "mobile");
            var result = new List<(string, string)>();
            foreach (var f in Directory.EnumerateFiles(mobileDir, "*.js", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(mobileDir, f).Replace(Path.DirectorySeparatorChar, '/');
                // sw.js 只有快取前綴（另有測試守），__tests__ 裡是假網址
                if (rel == "sw.js" || rel.Contains("__tests__/", StringComparison.Ordinal)) continue;
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(StripComments(File.ReadAllText(f)), @"(['`])(/api/[^'`]*)\1"))
                    result.Add((rel, m.Groups[2].Value));
            }
            return result;
        }

        private static Regex TemplateRegex(string route) =>
            new("^" + Regex.Replace(Regex.Escape(route), @"\\\{[^}]*\}", "[^/]+") + "$");

        [Fact]
        public void 手機前端呼叫的每個API網址都必須對得上後端路由()
        {
            var routes = BackendRoutes();
            Assert.True(routes.Count >= 60, $"只收到 {routes.Count} 條後端 api 路由，掃描方式已失效，本測試形同虛設。");
            var exact = routes.Select(r => TemplateRegex(r)).ToList();

            var urls = FrontendUrls();
            Assert.True(urls.Count >= 50, $"只從手機 JS 收到 {urls.Count} 個 API 網址，掃描方式已失效。");

            var dead = new List<string>();
            foreach (var (file, raw) in urls)
            {
                var path = Regex.Replace(raw.Split('?')[0], @"\$\{[^}]*\}", "X");
                bool ok;
                if (path.EndsWith('/'))
                {
                    // '/api/mobile/dispatches/' + id + '/start' 這種串接：字面值只是開頭
                    var head = path.TrimEnd('/');
                    ok = routes.Any(r => TemplatePrefixMatches(r, head));
                }
                else
                {
                    ok = exact.Any(rx => rx.IsMatch(path));
                }
                if (!ok) dead.Add($"{file}：{raw}");
            }

            Assert.True(dead.Count == 0,
                "下列手機前端網址對不到任何後端路由，送出會 404（現場只看到「操作失敗」）：\n  "
                + string.Join("\n  ", dead.Distinct()));
        }

        [Fact]
        public void 掃描必須抓得到已知的錯誤網址()
        {
            // 自我檢查：拿 10/9 那個真實錯誤網址當反例，確認比對不會把它當成合法
            var routes = BackendRoutes();
            Assert.Contains("/api/mobile/inspection-orders", routes);
            Assert.DoesNotContain(routes, r => TemplateRegex(r).IsMatch("/api/mobile/mfg/inspection-orders"));
        }

        /// <summary>樣板的前幾段是否能吻合 head（head 中的每一段對樣板參數段都算吻合）。</summary>
        private static bool TemplatePrefixMatches(string route, string head)
        {
            var r = route.Split('/');
            var h = head.Split('/');
            if (h.Length > r.Length) return false;
            for (var i = 0; i < h.Length; i++)
                if (!(r[i] == h[i] || (r[i].StartsWith('{') && r[i].EndsWith('}')))) return false;
            return true;
        }

        private static string StripComments(string text)
            => Regex.Replace(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline),
                             @"(?m)^\s*//[^\n]*", "");
    }
}
