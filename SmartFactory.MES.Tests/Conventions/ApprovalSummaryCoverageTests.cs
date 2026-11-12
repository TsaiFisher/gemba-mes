using System.Reflection;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 每一種會進簽核的單別，要嘛有手機簽核摘要，要嘛**明寫還沒做並附理由**。
    ///
    /// <para><b>為什麼是這個形狀而不是「全部都要有」</b>：現在只實作了少數常用單別，
    /// 直接要求全覆蓋會讓這支測試從第一天就是紅的，而長期紅的測試等於沒有測試。
    /// 所以改成「已實作 ∪ 待辦清單 ⊇ 全部單別」——新增送簽單別時測試會紅，
    /// 逼你二選一：實作它，或把它寫進待辦清單並說明為什麼先不做。
    /// 這與 <c>scripts/core-drift-baseline.txt</c> 是同一個手法。</para>
    ///
    /// <para><b>失敗的症狀是什麼</b>：沒有 provider 的單別在手機上顯示
    /// 「此單別請用電腦簽核」＋桌機連結。那是可接受的降級，**不是壞掉**——
    /// 真正不可接受的是給一顆看不到內容卻按得下去的核准鈕。
    /// 所以這支測試守的是「有沒有人做過決定」，不是「功能有沒有做完」。</para>
    ///
    /// <para>⚠️ 待辦清單只能變短，不該變長。清單裡每一項都是「手機簽不了的單別」。</para>
    /// </summary>
    public class ApprovalSummaryCoverageTests
    {
        /// <summary>
        /// 刻意還沒實作手機摘要的單別。**每一項都要有理由**，而且理由要是
        /// 「為什麼這張單先不做」，不是「還沒排到」——後者對讀的人沒有任何資訊。
        /// </summary>
        private static readonly Dictionary<string, string> NotYetImplemented = new(StringComparer.Ordinal)
        {
            // ⚠️ 母體是 IApprovalSyncContributor 的 DocumentType（27 個），**不是**
            //    ApprovalDocumentLinks.Templates（30 個）。後者多出 CodeEntry／MaterialSlip／
            //    OutsourcePurchaseOrder／OutsourceRework 四個沒有 sync contributor 的鍵，
            //    照著它抄會讓清單長出永遠比對不到的化石——「待辦清單不含不存在的單別」
            //    這支測試就是這樣抓到我抄錯的。

            // ── 主檔類：不是交易單據，沒有金額與明細，摘要能提供的資訊極少 ──────────
            ["Company"]    = "主檔異動。摘要只會是代碼與名稱幾欄，與桌機清單看到的一樣多，手機另做一份的價值低。",
            ["Product"]    = "同上，主檔異動（品號）。",
            ["Tax"]        = "同上，主檔異動（稅別）。",

            // ── 倉庫作業類：判斷依據是實體庫存與儲位，摘要給不出關鍵資訊 ─────────────
            ["ScrapDoc"]             = "報廢判斷要看實物與儲位，摘要上的品號數量不足以支撐決定。",
            ["LoanDoc"]              = "同上，借出。",
            ["SampleDoc"]            = "同上，樣品。",
            ["StockAdjustment"]      = "調整判斷要對照盤點差異明細，那是整張表不是摘要。",
            ["StockTransfer"]        = "同上，調撥。",
            ["CrossPlantTransfer"]   = "同上，跨廠調撥；另有廠別情境要看。",
            ["OutsourceMaterial"]    = "同上，委外領退料。",
            ["ReworkMaterial"]       = "同上，重工領料。",
            ["MaterialStagingOrder"] = "同上，備料。",
            ["LandedCostAllocation"] = "分攤基礎是多張進貨單的分攤比例，摘要放不下。",

            // ── 其餘交易單據：**這些是真正的待辦**，優先序次於 PUR／ORD ────────────
        };

        [Fact]
        public void 每個送簽單別都有手機摘要或明寫還沒做()
        {
            var docTypes = ContributorDocumentTypes();

            // 下限：反射掃不到時這支會變成「零項全過」的裝飾品。
            // 這個數字沿用 ApprovalDocumentLinkCoverageTests，兩支掃的是同一個介面。
            Assert.True(docTypes.Count >= 25,
                $"只找到 {docTypes.Count} 個 IApprovalSyncContributor，遠少於預期——" +
                "多半是反射掃不到了，而不是單別真的變少。");

            var implemented = SummaryProviderDocumentTypes();

            var undecided = docTypes
                .Where(t => !implemented.Contains(t) && !NotYetImplemented.ContainsKey(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            Assert.True(undecided.Count == 0,
                "以下單別會進簽核流程，但既沒有手機簽核摘要、也沒有寫進 NotYetImplemented：\n  "
                + string.Join("\n  ", undecided)
                + "\n\n請二選一：實作 IApprovalSummaryProvider，"
                + "或加進本檔的 NotYetImplemented 並寫下**為什麼這張單先不做**。"
                + "\n沒有摘要的單別在手機上會顯示「請用電腦簽核」——那是可接受的降級，"
                + "但必須是有人做過的決定，不是沒人注意到。");
        }

        [Fact]
        public void 待辦清單不留已經做完的項目()
        {
            // 反面對照。少了這一支，實作完成後忘了從清單移除也不會有人發現，
            // 而清單的意義正是「還沒做的有哪些」——混進做完的就開始說謊。
            var implemented = SummaryProviderDocumentTypes();

            var stale = NotYetImplemented.Keys
                .Where(implemented.Contains)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            Assert.True(stale.Count == 0,
                "以下單別已經有 IApprovalSummaryProvider，卻還留在 NotYetImplemented：\n  "
                + string.Join("\n  ", stale)
                + "\n請從清單移除。");
        }

        [Fact]
        public void 待辦清單不含不存在的單別()
        {
            // 另一面：單別改名或移除後，清單裡會留下永遠不會被比對到的死項目，
            // 而死項目會讓清單長度失去意義（看起來還有很多待辦，其實是化石）。
            var docTypes = ContributorDocumentTypes().ToHashSet(StringComparer.Ordinal);

            var ghosts = NotYetImplemented.Keys
                .Where(t => !docTypes.Contains(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            Assert.True(ghosts.Count == 0,
                "以下單別在 NotYetImplemented 裡，但已經不是會送簽的單別了：\n  "
                + string.Join("\n  ", ghosts)
                + "\n多半是改名或移除，請一併清掉。");
        }

        [Fact]
        public void 每個摘要實作的單別都對得上真的會送簽的單別()
        {
            // 防「實作了一個 DocumentType 打錯字的 provider」——那種 provider 永遠不會被叫到，
            // 而症狀與「沒有實作」一模一樣（手機顯示請用電腦簽核），查起來卻完全不同方向。
            var docTypes = ContributorDocumentTypes().ToHashSet(StringComparer.Ordinal);

            var orphans = SummaryProviderDocumentTypes()
                .Where(t => !docTypes.Contains(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            Assert.True(orphans.Count == 0,
                "以下 IApprovalSummaryProvider 的 DocumentType 對不上任何會送簽的單別"
                + "（多半是打錯字，那支 provider 永遠不會被呼叫到）：\n  "
                + string.Join("\n  ", orphans));
        }

        // ── 反射工具 ─────────────────────────────────────────────────────────

        private static HashSet<string> SummaryProviderDocumentTypes() =>
            DocumentTypesOf(typeof(IApprovalSummaryProvider)).ToHashSet(StringComparer.Ordinal);

        private static List<string> ContributorDocumentTypes() =>
            DocumentTypesOf(typeof(IApprovalSyncContributor));

        private static List<string> DocumentTypesOf(Type iface) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("SmartFactory.MES", StringComparison.Ordinal) == true)
                .SelectMany(SafeTypes)
                .Where(t => t is { IsAbstract: false, IsInterface: false } && iface.IsAssignableFrom(t))
                .Select(t => t.GetProperty("DocumentType")?.GetValue(
                    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t)) as string)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }
    }
}
