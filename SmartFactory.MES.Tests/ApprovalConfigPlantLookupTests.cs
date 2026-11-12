using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// `GetApprovalConfig` 的 PlantId 參數不得傳 `null`。
    ///
    /// 為什麼要用掃原始碼的方式守：這個缺陷的形狀是**放行**，不是卡住。
    /// `DocumentApprovalConfig` 實作 `IPlantEntity`，存檔時 `ApplyGlobalValues` 會把
    /// `PlantId ??= CurrentPlantId` 填上——**沒有任何入口建得出「不指定廠別」的設定**；
    /// 而 `GetApprovalConfig` 沒有 `IgnoreQueryFilters`，全域查詢過濾
    /// `PlantId == CurrentPlantId` 也還壓著。條件要 `null`、過濾要廠別，**雙重不可能**。
    ///
    /// 於是傳 `null` 的呼叫端一定查不到設定，而每個呼叫端查不到都會走
    /// 「無簽核設定 → 直接核准」那條分支：**設定存得下去、畫面上看得到、就是不生效**，
    /// 沒有任何錯誤訊息。行為測試很難抓到它——單元測試多半會 mock `IApprovalService`
    /// 並自己指定回傳值，於是傳什麼參數進去都測得過。
    ///
    /// 這條規則過去確實被違反過兩輪，而且是**照著文件寫的**：
    /// `CLAUDE.md`〈全域主檔規則〉原本寫著「`GetApprovalConfig` 的 PlantId 參數傳 `null`
    /// （全域審核設定）」。`VendorEvaluationController` 與 `CodeEntryController` 都照做了。
    /// 兩處都已修正，文件那條也已更正——但**文件更正擋不住下一個人再寫一次**，
    /// 所以在這裡釘住。
    ///
    /// ⚠️ 混淆點寫在這裡，因為它就是原始成因：「代碼是全域主檔」不代表
    /// 「代碼的**簽核設定**也是全域的」。前者共用一份資料（不該用廠別過濾），
    /// 後者是廠別主檔（必須用廠別查）。兩件事被寫成同一條規則，才會錯。
    /// </summary>
    public class ApprovalConfigPlantLookupTests
    {
        private static readonly string RepoRoot = FindRepoRoot();

        /// <summary>比對 `GetApprovalConfig(<任意>, null)`——最後一個引數是字面 null。</summary>
        private static readonly Regex NullPlantLookup = new(
            @"GetApprovalConfig\s*\([^()]*,\s*null\s*\)", RegexOptions.Compiled);

        [Fact]
        public void GetApprovalConfig_IsNeverCalledWithNullPlantId()
        {
            var violations = new List<string>();

            foreach (var file in EnumerateProductionSources())
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!NullPlantLookup.IsMatch(lines[i])) continue;
                    violations.Add(
                        $"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}  {lines[i].Trim()}");
                }
            }

            Assert.True(violations.Count == 0,
                "以下呼叫端把 GetApprovalConfig 的 PlantId 傳成 null，設定將永遠查不到而「直接核准」放行：\n  "
                + string.Join("\n  ", violations)
                + "\n改傳 _userContext.PlantId（或該類別取得目前廠別的等效寫法）。");
        }

        /// <summary>
        /// 反向驗證：這條規則真的在看東西。
        ///
        /// 只斷言「沒有違規」的掃描規則有一種常見的死法——樣式寫錯而永遠比對不到，
        /// 於是恆綠、形同關閉（`manual-test-plan.md` §12.7 記過同一種假訊號：
        /// 「有讀取點、結果被丟掉」也通過了稽核）。故在此餵一段已知會違規的字串，
        /// 確認樣式抓得到；同時確認掃描真的有讀到檔案。
        /// </summary>
        [Fact]
        public void TheScanItselfIsNotVacuous()
        {
            Assert.Matches(NullPlantLookup, @"await _approvalService.GetApprovalConfig(""CodeEntry"", null);");
            Assert.Matches(NullPlantLookup, @"_approval.GetApprovalConfig(DocType, null)");
            // 正確寫法不得被誤報
            Assert.DoesNotMatch(NullPlantLookup, @"_approval.GetApprovalConfig(DocType, UserContext.PlantId)");

            var scanned = EnumerateProductionSources().Count();
            Assert.True(scanned > 20, $"只掃到 {scanned} 個原始檔，掃描範圍已失效，規則形同關閉");
        }

        // ── 輔助方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 產品程式碼（排除測試專案與建置產物）。測試裡出現字面 null 是合理的——
        /// 例如本檔自己，以及驗證「傳 null 查不到」的那類測試。
        /// </summary>
        private static IEnumerable<string> EnumerateProductionSources()
            => Directory.EnumerateFiles(RepoRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var rel = Path.GetRelativePath(RepoRoot, f);
                    return !rel.Contains($"Tests{Path.DirectorySeparatorChar}")
                        && !rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                });

        private static string FindRepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "SmartFactory.MES.sln")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("找不到 SmartFactory.MES.sln，無法定位 Repo 根目錄。");
        }
    }
}
