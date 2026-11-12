using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 每一個 inline &lt;script&gt;（沒有 src）都必須帶 nonce。CSP 的 script-src 只放行
    /// 'self' 與當次請求的 nonce，漏帶的 inline script 會被瀏覽器<b>靜默</b>擋掉——
    /// 畫面看起來只是按鈕沒反應，server log 也什麼都沒有。2026-10-20 補（Core 原本沒有這道守門）。
    /// 外部 script（有 src）走 'self'，不需要 nonce。
    /// </summary>
    public class InlineScriptNonceGuardTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Program.cs"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根目錄（Program.cs）");
        }

        private static readonly Regex ScriptTag = new(@"<script\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Fact]
        public void EveryInlineScript_CarriesNonce()
        {
            var root = RepoRoot();
            var files = new[] { "Views", "Pages", "Areas", "Modules" }
                .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cshtml", SearchOption.AllDirectories))
                .ToList();

            var scanned = 0;
            var offenders = new List<string>();
            foreach (var f in files)
            {
                // 去掉 Razor 註解 @* … *@，避免說明文字裡的範例被當成違規
                var text = Regex.Replace(File.ReadAllText(f), @"@\*.*?\*@",
                    c => new string('\n', c.Value.Count(ch => ch == '\n')), RegexOptions.Singleline);
                var blockEnd = -1;   // 目前所在 script 區塊的結尾；區塊內字串或註解裡的「<script」不是新標籤
                foreach (System.Text.RegularExpressions.Match m in ScriptTag.Matches(text))
                {
                    if (m.Index < blockEnd) continue;
                    var close = text.IndexOf("</script", m.Index + m.Length, StringComparison.OrdinalIgnoreCase);
                    blockEnd = close < 0 ? text.Length : close;
                    var attrs = m.Groups[1].Value;
                    if (Regex.IsMatch(attrs, @"\bsrc\s*=", RegexOptions.IgnoreCase)) continue;
                    // type="text/template" 之類不執行的區塊不受 script-src 管制
                    if (Regex.IsMatch(attrs, @"\btype\s*=\s*""(text/template|text/html|application/json|application/ld\+json)""", RegexOptions.IgnoreCase)) continue;
                    scanned++;
                    if (!Regex.IsMatch(attrs, @"\bnonce\s*=", RegexOptions.IgnoreCase))
                    {
                        var line = text[..m.Index].Count(c => c == '\n') + 1;
                        offenders.Add($"{Path.GetRelativePath(root, f)}:{line}");
                    }
                }
            }

            Assert.True(scanned > 0, "一個 inline script 都沒掃到＝掃描路徑失效，規則等於關閉");
            Assert.True(offenders.Count == 0,
                "以下 inline <script> 沒有 nonce，會被 CSP 靜默擋掉：\n" + string.Join("\n", offenders)
                + "\n補上 nonce=\"@(Context.Items[SmartFactory.MES.Middleware.CspNonceMiddleware.ItemKey] as string)\"。");
        }
    }
}
