using System.Text.RegularExpressions;
using Xunit;
using Match = System.Text.RegularExpressions.Match;   // 與 Moq.Match 同名，明確指定

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 使用手冊「作業流程圖」守門：
    /// 1. 每個作業模組（含首頁與手機版）的 index.html 都有流程圖區塊，且引用共用樣式 flow.css。
    /// 2. 流程圖節點的連結都指向實際存在的手冊頁（畫面改名/刪頁時立即紅燈）。
    /// 3. 節點/圖例使用的模組色 class（m-xxx）都在 flow.css 有定義，否則會顯示成無色方塊。
    /// </summary>
    public class UserManualFlowChartTests
    {
        /// <summary>需具備流程圖的手冊頁（相對 wwwroot/docs/user）。sys 為單一設定畫面，無作業流程。</summary>
        private static readonly string[] FlowPages =
        {
            "index.html",
            "bas/index.html", "mfg/index.html", "pur/index.html", "osp/index.html",
            "ord/index.html", "qcr/index.html", "inv/index.html", "fin/index.html",
            "act/index.html", "rpt/index.html", "mobile/index.html",
        };

        [Fact]
        public void EveryModuleIndex_HasFlowChartSection()
        {
            var missing = new List<string>();
            foreach (var page in FlowPages)
            {
                var html = File.ReadAllText(Path.Combine(ManualRoot(), page.Replace('/', Path.DirectorySeparatorChar)));
                if (!html.Contains("id=\"flow\"")) missing.Add($"{page}：缺少 id=\"flow\" 的流程圖章節");
                if (!html.Contains("class=\"flow\"")) missing.Add($"{page}：缺少 <div class=\"flow\"> 流程圖");
                if (!html.Contains("flow.css")) missing.Add($"{page}：未引用 flow.css");
            }

            Assert.True(missing.Count == 0, string.Join("\n", missing));
        }

        [Fact]
        public void EveryFlowNodeLink_PointsToExistingManualPage()
        {
            var broken = new List<string>();
            foreach (var page in FlowPages)
            {
                var file = Path.Combine(ManualRoot(), page.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(file)!;
                var html = File.ReadAllText(file);

                foreach (Match m in Regex.Matches(html, "<a class=\"flow-node[^\"]*\" href=\"([^\"]+)\""))
                {
                    var href = m.Groups[1].Value;
                    var path = href.Split('#')[0];
                    if (path.Length == 0) continue;   // 純錨點連結
                    var target = Path.GetFullPath(Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(target)) broken.Add($"{page} -> {href}");
                }
            }

            Assert.True(broken.Count == 0,
                "以下流程圖節點連到不存在的手冊頁：\n" + string.Join("\n", broken));
        }

        [Fact]
        public void EveryFlowNodeLink_IsUsedAtLeastOnce_AndModuleColorsAreDefined()
        {
            var css = File.ReadAllText(Path.Combine(ManualRoot(), "flow.css"));
            var defined = Regex.Matches(css, @"^\.(m-[a-z]+)\s*\{", RegexOptions.Multiline)
                               .Select(m => m.Groups[1].Value)
                               .ToHashSet();
            Assert.NotEmpty(defined);

            var undefined = new List<string>();
            var totalNodes = 0;
            foreach (var page in FlowPages)
            {
                var html = File.ReadAllText(Path.Combine(ManualRoot(), page.Replace('/', Path.DirectorySeparatorChar)));
                foreach (Match m in Regex.Matches(html, "class=\"(flow-node|flow-legend-item)([^\"]*)\""))
                {
                    if (m.Groups[1].Value == "flow-node") totalNodes++;
                    foreach (var cls in m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (cls.StartsWith("m-") && !defined.Contains(cls))
                            undefined.Add($"{page}：{cls}");
                }
            }

            Assert.True(undefined.Count == 0,
                "以下流程圖使用了 flow.css 未定義的模組色 class：\n" + string.Join("\n", undefined));
            Assert.True(totalNodes >= FlowPages.Length * 5, $"流程圖節點數過少（{totalNodes}），可能有頁面漏做");
        }

        [Fact]
        public void FlowNodes_AreAlwaysSeparatedByArrowOrDivider()
        {
            // 兩個節點直接相鄰時，讀者無法判斷是「先後順序」還是「並列」，
            // 一律要求中間有箭頭（flow-arrow）或分隔符（flow-plus / flow-cond）。
            var offenders = new List<string>();
            foreach (var page in FlowPages)
            {
                var html = File.ReadAllText(Path.Combine(ManualRoot(), page.Replace('/', Path.DirectorySeparatorChar)));
                foreach (Match steps in Regex.Matches(html, "<div class=\"flow-steps\">(.*?)\n\\s*</div>", RegexOptions.Singleline))
                {
                    var classes = Regex.Matches(steps.Groups[1].Value, "<(?:a|span|i)[^>]*class=\"([^\"]+)\"")
                                       .Select(m => m.Groups[1].Value)
                                       .Where(c => !c.Contains("flow-mod") && !c.Contains("flow-name"))
                                       .ToList();
                    for (int i = 1; i < classes.Count; i++)
                        if (classes[i - 1].Contains("flow-node") && classes[i].Contains("flow-node"))
                            offenders.Add($"{page}：{classes[i - 1]} 與 {classes[i]} 之間缺少箭頭或分隔符");
                }
            }

            Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        }

        private static string ManualRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null &&
                   !File.Exists(Path.Combine(dir.FullName, "SmartFactory.MES.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "wwwroot", "docs", "user");
        }
    }
}
