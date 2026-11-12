using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 稽核日誌的 Action 字串必須出自 <see cref="AuditAction"/>，而且每一種都要顯示得出來。
    ///
    /// <para><b>這一條守的是什麼</b>：在 <see cref="AuditAction"/> 之前，Action 欄位的寫入端與
    /// 顯示端是兩份互不相干的手打清單。兩邊對不上時**沒有任何訊號**——例外不會丟、測試不會紅、
    /// 日誌不會印，畫面上只是一個空白的表格欄位；而查詢頁的「操作」下拉選不到的那種 Action，
    /// 使用者連「有東西沒顯示」都看不出來。實際後果是 183 筆登入/登出紀錄的操作欄長期空白，
    /// 外加 <c>MobilePermissionController</c> 手寫的 <c>"Update"</c>（全站叫 <c>"Modified"</c>）
    /// 兩筆完全篩不出來。</para>
    ///
    /// <para><b>所以要守的是「新增一種 Action 時漏做某一步」</b>：漏登錄 <see cref="AuditAction.All"/>、
    /// 漏補資源鍵、漏補譯文、或是在別處另外手打一個字串——四種漏法各對應下面一支測試。</para>
    /// </summary>
    public class AuditActionGuardTests
    {
        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
        }

        private static IReadOnlyList<string> DeclaredConstants() =>
            typeof(AuditAction)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();

        /// <summary>登錄了常數卻忘了加進 All，它就不會出現在篩選下拉裡（Login2faRequired 原本就是這樣）。</summary>
        [Fact]
        public void EveryDeclaredConstant_IsListedInAll()
        {
            Assert.Equal(
                DeclaredConstants().OrderBy(x => x, StringComparer.Ordinal),
                AuditAction.All.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>沒有資源鍵就沒有譯文，畫面只能退回顯示原始英文字串。</summary>
        [Fact]
        public void EveryKnownAction_HasAResourceKeyAndABadgeStyle()
        {
            foreach (var action in AuditAction.All)
            {
                Assert.False(string.IsNullOrEmpty(AuditAction.ResourceKey(action)),
                    $"{action} 沒有對應的資源鍵——畫面會顯示原始字串而不是譯文。");
                Assert.False(string.IsNullOrWhiteSpace(AuditAction.BadgeCss(action)),
                    $"{action} 沒有對應的 badge 樣式。");
            }
        }

        /// <summary>
        /// 資源鍵有了、resx 裡卻沒有那一筆，<c>IViewLocalizer</c> 會**原樣回傳鍵名**而不是拋例外——
        /// 於是中文模式底下顯示英文，看起來像忘了翻譯，其實是根本沒有這個資源檔或這個鍵。
        /// 詳情頁整頁顯示英文就是這麼來的（<c>Details.*.resx</c> 兩個檔當時並不存在）。
        /// </summary>
        /// <param name="view">
        ///   兩個消費端都要檢查：<c>_ActionBadge</c> 畫 badge，而 <c>Index</c> 的篩選下拉
        ///   也是用同一組資源鍵逐項列出選項。只守其中一邊的話，另一邊照樣會顯示鍵名。
        /// </param>
        [Theory]
        [InlineData("_ActionBadge", "zh-TW")]
        [InlineData("_ActionBadge", "en-US")]
        [InlineData("Index", "zh-TW")]
        [InlineData("Index", "en-US")]
        public void EveryResourceKey_IsTranslatedInBothCultures(string view, string culture)
        {
            var names = ResourceKeysOf(view, culture);
            foreach (var action in AuditAction.All)
            {
                var key = AuditAction.ResourceKey(action)!;
                Assert.True(names.Contains(key), $"{view}.{culture} 缺少資源鍵 {key}（Action = {action}）。");
            }
        }

        /// <summary>
        /// 稽核日誌四個 View 直接寫在 <c>@Localizer["..."]</c> 裡的鍵也必須有譯文。
        /// 這一條與上一條是兩種漏法：上面守的是「新增 Action 忘了補譯文」，
        /// 這裡守的是「資源檔整個不存在或少一筆」——詳情頁在中文模式下整頁英文，
        /// 正是因為 <c>Details.*.resx</c> 當時根本沒有建立。
        /// </summary>
        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public void EveryLiteralKeyInTheAuditLogViews_IsTranslated(string culture)
        {
            var viewDir = Path.Combine(RepoRoot(), "Views", "AuditLog");
            var missing = new List<string>();

            foreach (var file in Directory.GetFiles(viewDir, "*.cshtml"))
            {
                var view = Path.GetFileNameWithoutExtension(file);
                var used = Regex.Matches(File.ReadAllText(file), @"Localizer\[""(?<k>[^""]+)""\]")
                                .Select(m => m.Groups["k"].Value)
                                .ToHashSet(StringComparer.Ordinal);
                if (used.Count == 0) continue;

                var names = ResourceKeysOf(view, culture);
                missing.AddRange(used.Except(names).Select(k => $"{view}.{culture}:{k}"));
            }

            Assert.True(missing.Count == 0,
                "下列資源鍵沒有譯文，畫面會直接顯示鍵名：" + string.Join("、", missing.OrderBy(x => x, StringComparer.Ordinal)));
        }

        private static HashSet<string> ResourceKeysOf(string view, string culture)
        {
            var path = Path.Combine(RepoRoot(), "Resources", "Views", "AuditLog", $"{view}.{culture}.resx");
            Assert.True(File.Exists(path), $"缺少資源檔 {path}；IViewLocalizer 找不到檔案時不會報錯，只會顯示鍵名。");

            return XDocument.Load(path).Root!
                .Elements("data")
                .Select(d => d.Attribute("name")!.Value)
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// 稽核日誌的三個顯示端（列表、詳情頁、側面板）不得自己列舉 Action——
        /// 一律走 <c>_ActionBadge</c> partial。原本三份 if/else 鏈裡有兩份是六種、
        /// 一份只有三種且沒有 else，差異沒有任何機制看得見。
        /// </summary>
        [Fact]
        public void AuditLogViews_DoNotEnumerateActionsThemselves()
        {
            var viewDir = Path.Combine(RepoRoot(), "Views", "AuditLog");
            var offenders = new List<string>();

            foreach (var file in Directory.GetFiles(viewDir, "*.cshtml"))
            {
                if (Path.GetFileName(file) == "_ActionBadge.cshtml") continue;   // 這一份就是唯一的列舉處
                var text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"\.Action\s*==\s*"""))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }

            Assert.True(offenders.Count == 0,
                "下列 View 自行比對 Action 字面值，請改用 _ActionBadge partial：" + string.Join("、", offenders));
        }

        /// <summary>
        /// 產品程式碼不得在建立 <c>AuditLog</c> 時手打 Action 字面值。
        /// <c>MobilePermissionController</c> 當初寫的 <c>"Update"</c> 就是這樣混進資料表的，
        /// 而它與全站其他地方的 <c>"Modified"</c> 不一致這件事，沒有任何測試看得到。
        /// </summary>
        [Fact]
        public void NoProductCode_AssignsAnActionLiteralWhenBuildingAnAuditLog()
        {
            var root = RepoRoot();
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "bin", "obj", "node_modules", ".git", ".vs",
                "SmartFactory.MES.Tests", "SmartFactory.MES.IntegrationTests",
                "SmartFactory.MES.UITests", "SmartFactory.MES.MachineId",
                "SmartFactory.MES.LicenseAuthority", "SmartFactory.MES.Security",
            };

            var offenders = new List<string>();
            var probed = 0;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(skipped.Contains))
                    continue;

                probed++;
                var text = File.ReadAllText(file);

                // 只看物件初始設定式內部：以 new AuditLog 為起點，到該區塊收尾之前。
                // 型別要寫全名：部署專案的測試專案有 `global using Moq;`，那裡的 Moq.Match
                // 與 System.Text.RegularExpressions.Match 撞名（CS0104）。Core 的 GlobalUsings
                // 只有 Xunit，所以這個衝突在 Core 這側看不見——兩邊要能共用同一份檔案。
                // （也不能改用 var：MatchCollection 走的是非泛型列舉器，會推成 object。）
                foreach (System.Text.RegularExpressions.Match start in Regex.Matches(text, @"new\s+AuditLog\s*\{"))
                {
                    var block = text.Substring(start.Index, Math.Min(1200, text.Length - start.Index));
                    var literal = Regex.Match(block, @"Action\s*=\s*""(?<v>[^""]*)""");
                    if (literal.Success)
                    {
                        var line = text.Take(start.Index).Count(c => c == '\n') + 1;
                        offenders.Add($"{relative}:{line} → \"{literal.Groups["v"].Value}\"");
                    }
                }
            }

            // 掃描量自我檢查：若因目錄結構改變而幾乎沒讀到檔案，這條規則會永遠是綠的。
            // 門檻取 100：Core 本身的產品程式碼是 147 個檔，部署專案上千個——
            // 這個數字只用來分辨「掃到了」與「幾乎沒掃到」，不是拿來追蹤檔案成長的。
            Assert.True(probed >= 100, $"只掃到 {probed} 個 .cs 檔，掃描範圍顯然不對。");
            Assert.True(offenders.Count == 0,
                "下列位置手打了 Action 字面值，請改用 AuditAction 常數：" + string.Join("；", offenders));
        }

        /// <summary>
        /// 資料異動三種常數的值必須與 EF 的 <see cref="EntityState"/> 同名。
        /// <c>AuditInterceptor</c> 是由 EntityState 映射過來的，而資料表裡的歷史紀錄存的
        /// 也正是這三個字串——改掉任何一個，舊資料就查不到、也對不上新寫入的值。
        /// </summary>
        [Fact]
        public void DataChangeActions_MatchTheEntityStateNames()
        {
            Assert.Equal(AuditAction.Added, EntityState.Added.ToString());
            Assert.Equal(AuditAction.Modified, EntityState.Modified.ToString());
            Assert.Equal(AuditAction.Deleted, EntityState.Deleted.ToString());
        }

        /// <summary>未知值不得讓 badge 變成空白——看得見的陌生字串才有人會去追。</summary>
        [Theory]
        [InlineData("SomethingNobodyRegistered")]
        [InlineData("")]
        [InlineData(null)]
        public void UnknownAction_FallsBackToAVisibleBadge(string? unknown)
        {
            Assert.Null(AuditAction.ResourceKey(unknown));
            Assert.False(string.IsNullOrWhiteSpace(AuditAction.BadgeCss(unknown)));
        }
    }
}
