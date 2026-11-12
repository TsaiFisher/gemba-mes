using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 程式與測試裡宣稱的人工驗收缺陷編號（`W2-F13` 這種），
    /// 必須在 `manual-test-plan.md` 裡找得到。
    ///
    /// <para><b>為什麼需要這一條</b>——2026-09-24 查證委外前置時發現：程式裡有
    /// <b>21 個</b> `Wx-Fn` 編號、計畫裡只有 <b>10 個</b>，而 Wave 2 的 10 個
    /// <b>一個都沒記</b>。於是 §14.2 一路寫著「Wave 2–5 未執行」，
    /// 與同一份文件隔 25 行的 §5.4（標題就是「2-10／2-11 的**實測結果**」）自相矛盾三週，
    /// 還害兩次狀態回報都給錯了答案。</para>
    ///
    /// <para><b>R29／R35 防不到這一類</b>：R29 對的是 §11 ↔ `STATUS.md`，
    /// R35 對的是 §12 ↔ §11。它們對的都是**計畫內部**或計畫與另一份文件，
    /// 沒有一條把**程式裡的事實**拉回來跟計畫對。而人工驗收的缺陷天生就先落在程式裡
    /// （修的人當下在改程式），最後才有人想起要回填文件——「最後」常常等於沒有。</para>
    ///
    /// <para><b>方向是單向的</b>：程式 → 計畫。計畫裡有而程式裡沒有是正常的
    /// （缺陷可能是設定或資料問題，不留程式痕跡，例如 W1-F4～F8）。</para>
    ///
    /// <para><b>基線</b>：`baseline-manual-defect-ids.txt` 收的是這條規則上線當下就已經
    /// 缺漏的編號。Wave 2 那批已於同日回填 §5.5，故基線只剩 Wave 3／4 的六個，
    /// 那是待辦不是豁免——回填一個就從基線刪一個。</para>
    /// </summary>
    public class ManualAcceptanceDefectIdTests
    {
        private static readonly string RepoRoot = FindRepoRoot();

        /// <summary>`W2-F13`：W + 波次 + -F + 流水號。</summary>
        private static readonly Regex DefectId = new(@"\bW\d+-F\d+\b", RegexOptions.Compiled);

        [Fact]
        public void 程式裡宣稱的人工驗收缺陷編號必須記在測試計畫裡()
        {
            var inPlan = IdsIn(PlanText());
            var inCode = IdsInCode();
            var baseline = Baseline();

            Assert.True(inCode.Count >= 15,
                $"只在程式裡掃到 {inCode.Count} 個缺陷編號——正規式或掃描範圍已失效，規則形同關閉");

            var missing = inCode.Except(inPlan).Except(baseline)
                                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.True(missing.Count == 0,
                "以下缺陷編號只存在於程式／測試，`manual-test-plan.md` 裡找不到。\n"
                + "人工驗收找到的缺陷若只留在程式註解裡，計畫的「還剩什麼」就會長期說錯話"
                + "（2026-09-24 就是這樣連錯兩次）。請回填到對應波次的缺陷清單：\n  "
                + string.Join("\n  ", missing.Select(id => $"{id}  ←  {FirstMention(id)}")));
        }

        /// <summary>
        /// 反向驗證：基線不得收錄「其實已經回填」的編號。
        /// 基線是待辦清單不是垃圾桶——留著已完成的項目，下次就沒人敢刪它。
        /// </summary>
        [Fact]
        public void 基線不得收錄已經回填到計畫裡的編號()
        {
            var inPlan = IdsIn(PlanText());
            var stale = Baseline().Intersect(inPlan).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.True(stale.Count == 0,
                "以下編號已經寫進 manual-test-plan.md，請從 baseline-manual-defect-ids.txt 移除：\n  "
                + string.Join(", ", stale));
        }

        /// <summary>反向驗證：規則真的在看東西。</summary>
        [Fact]
        public void 規則本身不是空轉()
        {
            var inPlan = IdsIn(PlanText());

            // ① 回填後的 Wave 2 編號在計畫裡找得到（這條規則誕生的理由）
            Assert.Contains("W2-F13", inPlan);
            // ② 計畫獨有的編號存在，證明「單向」這個設計是有意義的
            Assert.Contains("W1-F4", inPlan);
            Assert.DoesNotContain("W1-F4", IdsInCode());
            // ③ 正規式抓得到實際寫法
            Assert.Equal(new[] { "W2-F13", "W4-F7" },
                IdsIn("見人工驗收紀錄 W2-F13 與 W4-F7。").OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        // ── 輔助方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 計畫本文＋已完成部分的封存（2026-09-30 起 Wave 0–5 等已執行章節搬到
        /// `archive/manual-test-plan-archive.md`）。缺陷編號記在哪一份都算「記在計畫裡」，
        /// 只讀本文的話，封存那一刻所有 Wave 2／4 的編號都會變成「找不到」。
        /// </summary>
        private static string PlanText()
        {
            var p = Path.Combine(RepoRoot, ".claude", "specifications", "manual-test-plan.md");
            Assert.True(File.Exists(p), $"找不到測試計畫：{p}");
            var a = Path.Combine(RepoRoot, ".claude", "specifications", "archive", "manual-test-plan-archive.md");
            return File.ReadAllText(p) + "\n" + (File.Exists(a) ? File.ReadAllText(a) : string.Empty);
        }

        private static HashSet<string> IdsIn(string text)
            => DefectId.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        private static IEnumerable<string> CodeFiles()
            => new[] { "Modules", "SmartFactory.MES.Tests" }
                .Select(d => Path.Combine(RepoRoot, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         // 排除本檔：它的註解與反向驗證都會提到編號，掃到自己等於
                         // 「因為我提了 W1-F4，所以計畫裡必須有 W1-F4」——規則會開始
                         // 要求它自己舉的例子。第一版就是這樣紅的。
                         && Path.GetFileName(f) != "ManualAcceptanceDefectIdTests.cs");

        private static HashSet<string> IdsInCode()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in CodeFiles()) ids.UnionWith(IdsIn(File.ReadAllText(f)));
            return ids;
        }

        /// <summary>第一個提到該編號的檔案與行號——訊息要指得出去哪裡找，否則回填的人得自己 grep。</summary>
        private static string FirstMention(string id)
        {
            foreach (var f in CodeFiles().OrderBy(x => x, StringComparer.Ordinal))
            {
                var lines = File.ReadAllLines(f);
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(id, StringComparison.Ordinal))
                        return $"{Path.GetRelativePath(RepoRoot, f)}:{i + 1}";
            }
            return "(找不到，掃描範圍可能已變)";
        }

        private static HashSet<string> Baseline()
        {
            var p = Path.Combine(RepoRoot, "scripts", "baseline-manual-defect-ids.txt");
            if (!File.Exists(p)) return new HashSet<string>(StringComparer.Ordinal);
            return File.ReadAllLines(p)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".claude"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
