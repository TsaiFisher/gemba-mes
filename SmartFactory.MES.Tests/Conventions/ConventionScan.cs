using System.Text;
using System.Text.RegularExpressions;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 一致性框架建置期把關（R1–R5）的共用掃描工具。
    ///
    /// Baseline 機制：首次執行若無 baseline 檔，會把目前的既有違規寫入並通過
    /// （approval-test 模式）。之後任何「新」違規（不在 baseline）即讓建置失敗，
    /// 從源頭擋住 Vibe Coding 遺漏。Phase B 回補時逐行刪除 baseline 即收斂。
    /// </summary>
    internal static class ConventionScan
    {
        private static readonly Regex Cjk = new(@"\p{IsCJKUnifiedIdeographs}",
            RegexOptions.Compiled);

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null &&
                   !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("找不到 ProjectZ 專案根目錄。");
            return dir.FullName;
        }

        internal static bool HasCjk(string s) => Cjk.IsMatch(s);

        internal static IEnumerable<string> Files(string relDir, string pattern) =>
            Directory.Exists(Path.Combine(RepoRoot(), relDir))
                ? Directory.EnumerateFiles(Path.Combine(RepoRoot(), relDir), pattern,
                    SearchOption.AllDirectories)
                : Enumerable.Empty<string>();

        internal static string Rel(string full) =>
            Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');

        /// <summary>resx 內所有 data name="..."。</summary>
        internal static HashSet<string> ResxKeys(string resxPath)
        {
            if (!File.Exists(resxPath)) return new HashSet<string>();
            var text = File.ReadAllText(resxPath);
            return Regex.Matches(text, @"<data\s+name=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .ToHashSet();
        }

        /// <summary>resx 內 key → value 對照（同 key 後者覆蓋）。</summary>
        internal static Dictionary<string, string> ResxValues(string resxPath)
        {
            var map = new Dictionary<string, string>();
            if (!File.Exists(resxPath)) return map;
            var text = File.ReadAllText(resxPath);
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text,
                @"<data\s+name=""([^""]+)""[^>]*>\s*<value>(.*?)</value>",
                RegexOptions.Singleline))
                map[m.Groups[1].Value] = m.Groups[2].Value;
            return map;
        }

        /// <summary>
        /// 移除 C# 原始碼的註解（<c>//</c> 與 <c>/* */</c>），字串／字元字面值內原樣保留。
        ///
        /// 供「以原始碼樣式為準」的規則使用（R22）。若不去註解，規則就會逼人不敢在註解裡
        /// 寫出被禁的寫法——而說明「為什麼不可以用 X」本來就得把 X 寫出來。
        /// 字面值必須跳過，否則 <c>"https://…"</c> 的 <c>//</c> 會被當成註解起點，
        /// 把該行後半段連同可能的真違規一起吃掉（靜默漏報）。
        /// </summary>
        internal static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                // 字串／逐字字串／字元字面值：整段照抄
                if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && text[i - 1] == '@';
                    sb.Append(c);
                    for (i++; i < text.Length; i++)
                    {
                        if (!verbatim && text[i] == '\\')
                        {
                            sb.Append(text[i]);
                            if (++i < text.Length) sb.Append(text[i]);
                            continue;
                        }
                        // 逐字字串內的 "" 是跳脫的引號，不是結尾
                        if (verbatim && text[i] == '"'
                            && i + 1 < text.Length && text[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i++;
                            continue;
                        }
                        sb.Append(text[i]);
                        if (text[i] == c) break;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    if (i < text.Length) sb.Append('\n');   // 保留換行，行數不變
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var stop = end < 0 ? text.Length : end + 2;
                    // 吃掉的換行要補回來，行數不變（規則若改以行號定位才不會錯位）
                    for (var j = i; j < stop; j++)
                        if (text[j] == '\n') sb.Append('\n');
                    i = stop - 1;
                    continue;
                }

                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 比對「目前違規」與 baseline；首次自動寫入 baseline 並視為通過。
        /// 回傳 (是否通過, 失敗訊息)。
        /// </summary>
        internal static (bool ok, string message) CheckAgainstBaseline(
            string ruleId, IEnumerable<string> violations)
        {
            var current = violations.Distinct().OrderBy(x => x).ToList();
            var baselineFile = Path.Combine(RepoRoot(),
                "SmartFactory.MES.Tests", "Conventions", $"baseline-{ruleId}.txt");

            if (!File.Exists(baselineFile))
            {
                var header =
                    $"# {ruleId} baseline — 既有違規清單（Phase B 回補時逐行刪除）。\n" +
                    "# 不在此清單的新違規會讓建置失敗。\n";
                File.WriteAllText(baselineFile,
                    header + string.Join('\n', current) + "\n", Encoding.UTF8);
                return (true, $"{ruleId}: baseline 已建立（{current.Count} 筆既有違規）。");
            }

            var baseline = File.ReadAllLines(baselineFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToHashSet();

            var newOnes = current.Where(v => !baseline.Contains(v)).ToList();
            if (newOnes.Count == 0)
                return (true, $"{ruleId}: 無新違規（baseline {baseline.Count} 筆）。");

            return (false,
                $"{ruleId}: 偵測到 {newOnes.Count} 筆新違規（不在 baseline）：\n  " +
                string.Join("\n  ", newOnes) +
                $"\n→ 請修正，或（僅既有程式）將其加入 " +
                $"SmartFactory.MES.Tests/Conventions/baseline-{ruleId}.txt");
        }
    }
}
