using System.Text.RegularExpressions;
using SmartFactory.MES.Modules.BAS.Models;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 每一個 <see cref="WarehouseType"/> 列舉值都要在倉庫主檔畫面上**選得到、看得懂**。
    ///
    /// <para><b>這支測試是被一個真實缺陷逼出來的</b>：2026-09-22 新增
    /// <c>CustomerSupplied</c> 時，列舉、過帳守門、計價排除、稽核、16 支單元測試全部到位，
    /// 唯獨 <c>_WarehouseForm.cshtml</c> 的 <c>&lt;option&gt;</c> 是**手寫死的七個**，
    /// 沒有人動到它。結果是業主打開畫面說「倉庫類別沒有客供倉可以選」——
    /// 後端做好了一整條路，前端沒有入口，等於整批工作都還沒上線。</para>
    ///
    /// <para><b>為什麼靜態掃 cshtml 而不是改成列舉驅動</b>：
    /// <c>Html.GetEnumSelectList</c> 會直接產出列舉名稱（<c>CustomerSupplied</c>），
    /// 中文站台看到的是英文，而本專案的慣例是每個選項配一個 resx 鍵。
    /// 兩害相權，保留手寫 option＋用測試釘住覆蓋率：新增列舉值時這支會紅，
    /// 紅的訊息直接說要補哪一個檔、哪一個鍵。</para>
    ///
    /// <para><b>為什麼連 resx 都要驗</b>：鍵不存在時 <c>IStringLocalizer</c>
    /// **不會丟例外**，它回傳鍵名本身。所以少一個鍵的症狀是下拉選單裡出現一個
    /// 叫「WH_CustomerSupplied」的選項——看起來像有做，實際是壞的，而且測不出來。
    /// 這與 CSP 樣式被靜默丟棄是同一種形狀。</para>
    /// </summary>
    public class WarehouseTypeUiCoverageTests
    {
        private const string FormView  = "Modules/BAS/Views/Warehouse/_WarehouseForm.cshtml";
        private const string IndexView = "Modules/BAS/Views/Warehouse/Index.cshtml";
        private const string ManualPage = "wwwroot/docs/user/bas/warehouse.html";

        /// <summary>手冊頁裡「倉庫類型與可用量」那一節的錨點，本測試只掃這一節。</summary>
        private const string ManualSectionAnchor = "warehouse-type-and-available-quantity";

        private static readonly string[] ResxFiles =
        {
            "Resources/Modules/BAS/Views/Warehouse/_WarehouseForm.zh-TW.resx",
            "Resources/Modules/BAS/Views/Warehouse/_WarehouseForm.en-US.resx",
            "Resources/Modules/BAS/Views/Warehouse/Index.zh-TW.resx",
            "Resources/Modules/BAS/Views/Warehouse/Index.en-US.resx",
        };

        private static string Read(string rel) =>
            File.ReadAllText(Path.Combine(ConventionScan.RepoRoot(), rel));

        private static IEnumerable<WarehouseType> AllTypes =>
            Enum.GetValues<WarehouseType>();

        /// <summary>新增類別卻沒補 option＝後端做完但畫面建不出那種倉庫。</summary>
        [Fact]
        public void 每個倉庫類別都要在新增畫面選得到()
        {
            var html = Read(FormView);

            var missing = AllTypes
                .Where(t => !html.Contains($"<option value=\"{t}\"", StringComparison.Ordinal))
                .Select(t => t.ToString())
                .ToList();

            Assert.True(missing.Count == 0,
                $"{FormView} 缺少這些倉庫類別的 <option>：{string.Join("、", missing)}。"
                + "後端支援但畫面選不到，等於那條路沒上線。");
        }

        /// <summary>
        /// 清單頁的 switch 有 <c>_ =&gt; ToString()</c> 這條 fallback，
        /// 所以漏掉一個類別**不會出錯**，只會在中文站台顯示英文列舉名。
        /// 沒有這支測試，那種漏法要靠肉眼逐頁看才發現。
        /// </summary>
        [Fact]
        public void 每個倉庫類別在清單頁都有自己的顯示名稱()
        {
            var html = Read(IndexView);

            var missing = AllTypes
                .Where(t => !html.Contains($"WarehouseType.{t} ", StringComparison.Ordinal)
                         && !html.Contains($"WarehouseType.{t}=>", StringComparison.Ordinal)
                         && !html.Contains($"WarehouseType.{t} =>", StringComparison.Ordinal))
                .Select(t => t.ToString())
                .ToList();

            Assert.True(missing.Count == 0,
                $"{IndexView} 的 switch 漏了：{string.Join("、", missing)}。"
                + "漏掉不會報錯，只會落到 _ => ToString() 顯示英文列舉名。");
        }

        /// <summary>
        /// 四個 resx 都要有 <c>WH_&lt;列舉名&gt;</c>。
        /// 少了不會丟例外——Localizer 回傳鍵名本身，畫面上就出現「WH_Xxx」。
        /// </summary>
        [Fact]
        public void 每個倉庫類別的中英名稱都齊全()
        {
            var problems = new List<string>();

            foreach (var rel in ResxFiles)
            {
                var xml = Read(rel);
                foreach (var t in AllTypes)
                {
                    var key = $"WH_{t}";
                    var m = Regex.Match(xml,
                        $"<data name=\"{Regex.Escape(key)}\"[^>]*>\\s*<value>(.*?)</value>",
                        RegexOptions.Singleline);

                    if (!m.Success)
                        problems.Add($"{rel} 缺 {key}");
                    else if (string.IsNullOrWhiteSpace(m.Groups[1].Value))
                        problems.Add($"{rel} 的 {key} 是空字串");
                    else if (m.Groups[1].Value.Trim() == key)
                        problems.Add($"{rel} 的 {key} 只是把鍵名抄一次，不是譯文");
                }
            }

            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        /// <summary>
        /// 每個倉庫類別在**使用手冊**的「倉庫類型與可用量」表裡都要有一列。
        ///
        /// <para><b>為什麼手冊也要釘</b>：<c>CustomerSupplied</c> 那次補完畫面之後，手冊的
        /// 倉庫類型表仍然只列七種，而那張表正是使用者判斷「這批料該收去哪個倉」的依據——
        /// 少一列的症狀不是報錯，是使用者根本不知道有這個選項，於是把客供料收進一般倉，
        /// 再由移動平均把成本汙染擴散出去。畫面有、手冊沒有，等於這條路只有知道的人走得到。</para>
        ///
        /// <para><b>比對方式</b>：把那張表的文字抽掉所有非英文字母後，找列舉名。
        /// 手冊寫「客供（Customer Supplied）」、「在製品（WIP）」——中間的空白與大小寫
        /// 都與列舉名不同，但去掉空白後就對得起來。</para>
        ///
        /// <para><b>範圍必須收到表格本身</b>，不能只收到「該小節」：小節裡除了表格還有說明框，
        /// 而說明框自然會反覆提到類別名稱。實測過——把表格裡的客供那一列整列刪掉，
        /// 用整節比對仍然全綠，因為底下說明框的 customer-supplied 把它餵飽了。
        /// 恆綠的守門比沒有守門更糟，它會讓人以為這件事有人看著。</para>
        /// </summary>
        [Fact]
        public void 每個倉庫類別在使用手冊都有說明()
        {
            var html = Read(ManualPage);

            var start = html.IndexOf(ManualSectionAnchor, StringComparison.Ordinal);
            Assert.True(start >= 0,
                $"{ManualPage} 找不到錨點 {ManualSectionAnchor}——手冊頁的段落結構改過，"
                + "請把本測試的錨點一起改，不要改成掃整頁（整頁掃會恆綠）。");

            // 只取錨點之後的第一張表，說明框不算數（見上方 remarks）。
            var tableStart = html.IndexOf("<table", start, StringComparison.Ordinal);
            var tableEnd   = tableStart < 0 ? -1 : html.IndexOf("</table>", tableStart, StringComparison.Ordinal);
            Assert.True(tableStart > 0 && tableEnd > tableStart,
                $"{ManualPage} 的 {ManualSectionAnchor} 小節底下找不到表格——"
                + "段落結構改過，請一起修這支測試。");

            var table = html[tableStart..tableEnd];
            var letters = new string(table.Where(char.IsAsciiLetter).ToArray());

            var missing = AllTypes
                .Where(t => letters.IndexOf(t.ToString(), StringComparison.OrdinalIgnoreCase) < 0)
                .Select(t => t.ToString())
                .ToList();

            Assert.True(missing.Count == 0,
                $"{ManualPage} 的「倉庫類型與可用量」表缺少這些類別：{string.Join("、", missing)}。"
                + "畫面選得到但手冊沒寫，使用者不會知道該用它。");
        }

        /// <summary>
        /// ⚠️ <c>WarehouseType</c> 以 **int 序數**持久化，新值只能加在最後。
        /// 插在中間會讓既有倉庫的類型整批位移，而位移後每一筆**都還是合法值**——
        /// 沒有例外、沒有紅字，只有一整個資料庫的倉別悄悄變成別的意思。
        /// 這支釘住現行序數，改動順序時強迫你看到這段說明。
        /// </summary>
        [Fact]
        public void 倉庫類別的序數不得變動()
        {
            Assert.Equal(0, (int)WarehouseType.Raw);
            Assert.Equal(1, (int)WarehouseType.Wip);
            Assert.Equal(2, (int)WarehouseType.Finished);
            Assert.Equal(3, (int)WarehouseType.Quarantine);
            Assert.Equal(4, (int)WarehouseType.Outsource);
            Assert.Equal(5, (int)WarehouseType.Scrap);
            Assert.Equal(6, (int)WarehouseType.Staging);
            Assert.Equal(7, (int)WarehouseType.CustomerSupplied);
        }
    }
}
