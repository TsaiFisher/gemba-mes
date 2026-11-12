using System.Reflection;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 手冊頁的**可檢索性**守門（規格 §10.10）。
    ///
    /// <para><b>要擋的失敗長這樣：</b>頁面寫好了、索引也建進去了、
    /// <c>HelpDocResolverTests</c> 一片綠——但權限過濾器的三個分支沒有一條接得住它，
    /// 於是不論用哪個角色提問都檢索不到。沒有例外、沒有 log、
    /// 使用者只會覺得「助理不知道有這個功能」。
    /// <c>mobile/index.html</c> 從 2026-07 建立起就是這個狀態。</para>
    ///
    /// <para><b>為什麼在單元測試做：</b>這裡驗的是「路徑 → MenuKey／Module → 過濾分支」
    /// 這條純推導鏈，不需要資料庫。真的跑一次 SQL 的版本在
    /// <c>ManualIndexIntegrationTests</c>，兩者是互補不是重複——
    /// 整合測試只挑代表性的一兩頁，這裡逐頁掃。</para>
    /// </summary>
    public class ManualReachabilityTests
    {
        /// <summary>
        /// <c>HelpDocResolver</c> 登錄的模組目錄集合。
        ///
        /// 這正是 <c>ManualSearchService.ResolveReadableAsync</c> 能推導出的
        /// <c>allowedModules</c> 的值域——使用者的 MenuKey 經 <c>ModuleOf</c> 對應而來，
        /// 所以不在這個集合裡的目錄，<b>沒有任何角色</b>的 <c>allowedModules</c> 會含有它。
        /// </summary>
        private static HashSet<string> ControllerBackedModules()
        {
            var field = typeof(HelpDocResolver).GetField(
                "ControllerModule", BindingFlags.NonPublic | BindingFlags.Static)!;
            var map = (Dictionary<string, string>)field.GetValue(null)!;

            Assert.NotEmpty(map);
            return map.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>DocIndexBuilder 的算法：相對路徑的目錄部分即 <c>DocChunk.Module</c>。</summary>
        private static string ModuleOfPage(string relativePosixPath)
        {
            var idx = relativePosixPath.LastIndexOf('/');
            return idx < 0 ? string.Empty : relativePosixPath[..idx];
        }

        [Fact]
        public void EveryManualPage_IsReachableByAtLeastOneRole()
        {
            var modules = ControllerBackedModules();
            var unreachable = new List<string>();

            foreach (var file in ManualPaths.AllPages())
            {
                var relative = Path.GetRelativePath(ManualPaths.UserDocsRoot(), file)
                    .Replace('\\', '/');

                // 與 DocIndexBuilder.BuildRows 同一組推導，不可各寫一份。
                var menuKey = HelpDocResolver.ResolveController(relative) ?? string.Empty;
                var module = ModuleOfPage(relative);

                // 對應 DocChunkFilter.LangAndPermission 的四個 OR 分支。
                var reachable =
                    menuKey.Length > 0                                                    // 1 有畫面權
                    || module.Length == 0                                                 // 2 手冊首頁公開
                    || modules.Contains(module)                                           // 3 模組目錄頁
                    || ManualCorpusPolicy.PublicModules                                    // 4 白名單目錄
                        .Contains(module, StringComparer.OrdinalIgnoreCase);

                if (!reachable) unreachable.Add(relative);
            }

            Assert.True(unreachable.Count == 0,
                "以下手冊頁不論用哪個角色提問都檢索不到（索引建得進去，但權限過濾器沒有一條分支接得住）：\n"
                + string.Join("\n", unreachable)
                + "\n\n修法二擇一：(a) 若該頁對應某個 MVC 畫面，去 HelpDocResolver 登錄 controller；"
                + "(b) 若該頁天生沒有 controller（如手機 PWA 的靜態頁），"
                + "把它的目錄加進 ManualCorpusPolicy.PublicModules 並在該處寫明公開的理由。");
        }

        /// <summary>
        /// 白名單不得與「有 controller 撐著的模組」重疊。
        ///
        /// 重疊的後果是把整個模組的目錄頁變成公開內容——那是系統功能結構的洩漏，
        /// 而且從測試結果上看不出來（原本就有人檢索得到，只是變成所有人）。
        /// </summary>
        [Fact]
        public void PublicModules_DoNotOverlapControllerBackedModules()
        {
            var backed = ControllerBackedModules();
            foreach (var m in ManualCorpusPolicy.PublicModules)
                Assert.DoesNotContain(m, backed);
        }

        /// <summary>
        /// 白名單指到的目錄必須真的存在。打錯字的話這條白名單只是一句沒有作用的宣告，
        /// 而它要救的頁面依然檢索不到——上面那條測試會紅，但訊息會指向錯的方向。
        /// </summary>
        [Fact]
        public void PublicModules_DirectoriesExist()
        {
            foreach (var m in ManualCorpusPolicy.PublicModules)
            {
                var dir = Path.Combine(ManualPaths.UserDocsRoot(), m);
                Assert.True(Directory.Exists(dir), $"ManualCorpusPolicy.PublicModules 的「{m}」指向不存在的目錄：{dir}");
            }
        }

        /// <summary>
        /// 白名單成員的**字面**約束。集合化之後這兩件事才有出錯的餘地：
        ///
        /// <list type="bullet">
        ///   <item><c>DocChunk.Module</c> 是 <c>varchar(10)</c>。超長的目錄名會在建索引時被
        ///     PostgreSQL 截斷或直接拋錯，而白名單比對的是完整字串——結果是白名單失效，
        ///     且失敗點離原因很遠。</item>
        ///   <item>成員會被**字面**拼進 <c>DocChunkFilter.LangAndPermission</c> 的 SQL。
        ///     這些值全是程式碼裡寫死的識別字，不含使用者輸入；限制成小寫英數字是要讓
        ///     「有人哪天塞進帶引號的字串」變成紅燈，而不是變成一段壞掉的 SQL。</item>
        /// </list>
        ///
        /// 順帶擋掉重複與空字串——兩者都會讓 SQL 的 <c>IN</c> 清單長出無意義的項目。
        /// </summary>
        [Fact]
        public void PublicModules_AreSimpleLowercaseIdentifiers()
        {
            var list = ManualCorpusPolicy.PublicModules;

            Assert.NotEmpty(list);
            Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());

            foreach (var m in list)
            {
                Assert.Matches("^[a-z0-9]+$", m);
                Assert.True(m.Length <= 10, $"「{m}」共 {m.Length} 字元，超過 DocChunk.Module 的 varchar(10)。");
            }
        }
    }
}
