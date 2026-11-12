using System.Reflection;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 每一種會進簽核的單據，都必須登錄得出 Details 頁網址。
    ///
    /// <para><b>為什麼需要守門</b>：對應表原本只是一份手寫的 JS 字面，沒有任何東西
    /// 檢查它與實際會送簽的單別是否一致。2026-09-08 逐一比對時發現 <b>5 個單別漏登錄</b>
    /// （<c>APInvoice</c>／<c>ARInvoice</c>／<c>LandedCostAllocation</c>／
    /// <c>MaterialStagingOrder</c>／<c>NonConformanceReport</c>），而漏掉的症狀是
    /// <b>點單號完全沒反應、也沒有任何訊息</b>——<c>PendingIndex.cshtml</c> 是
    /// <c>if (!urlFn) return;</c>。簽核人看到的是「畫面壞了」，不會知道要回報什麼。</para>
    ///
    /// <para>⚠️ 漏掉的那 5 個裡有兩個的原因是<b>名字對不上</b>：
    /// <c>NonConformanceReport</c> 的控制器叫 <c>Ncr</c>、<c>MaterialStagingOrder</c> 的叫
    /// <c>MaterialStaging</c>。所以這支測試比對的是「有沒有登錄」，
    /// <b>不是</b>用名字推網址——用名字推的話，正是這兩個會被推錯而測試照樣綠。</para>
    /// </summary>
    public class ApprovalDocumentLinkCoverageTests
    {
        [Fact]
        public void 每個會送簽的單別都登錄了單據連結()
        {
            var docTypes = ContributorDocumentTypes();

            // 下限：反射找不到任何 contributor 時（改了介面、改了組件載入方式）
            // 這支測試會變成「零項全過」的裝飾品。
            Assert.True(docTypes.Count >= 25,
                $"只找到 {docTypes.Count} 個 IApprovalSyncContributor，遠少於預期——" +
                "多半是反射掃不到了，而不是單別真的變少。");

            var missing = docTypes
                .Where(t => !ApprovalDocumentLinks.Templates.ContainsKey(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            Assert.True(missing.Count == 0,
                "以下單別會進簽核流程，但沒有登錄 Details 連結——簽核人點單號會完全沒反應" +
                "（PendingIndex.cshtml 是 if (!urlFn) return;，不會有錯誤訊息）：\n  "
                + string.Join("\n  ", missing)
                + "\n請在 Modules/_Shared/ApprovalDocumentLinks.cs 補上。"
                + "⚠️ DocumentType 不一定等於 Controller 名稱，請實際確認路由。");
        }

        [Fact]
        public void 樣板都帶得出單號()
        {
            // 少寫 {id} 不會編譯失敗，但會產生一個永遠指向列表頁的連結——
            // 點下去有反應、開得起來、就是打不開那一張單，比沒有連結更難發現。
            var bad = ApprovalDocumentLinks.Templates
                .Where(kv => !kv.Value.Contains("{id}", StringComparison.Ordinal))
                .Select(kv => $"{kv.Key} → {kv.Value}")
                .ToList();

            Assert.True(bad.Count == 0,
                "以下樣板沒有 {id} 佔位，組出來的連結會指到別的地方：\n  " + string.Join("\n  ", bad));
        }

        [Fact]
        public void Resolve對含斜線的單號不需特例()
        {
            // CodeEntry 的 DocumentId 是「類別/代碼」的自然鍵，原本的 JS 特地 split('/') 再組。
            // 這支釘住「直接代入結果相同」這個前提——它一旦不成立，特例就得補回來。
            Assert.Equal("/CodeEntry/Details/unit/KG",
                ApprovalDocumentLinks.Resolve("CodeEntry", "unit/KG"));
        }

        [Fact]
        public void 沒登錄的單別回null而不是丟例外()
        {
            // 簽核清單不該因為一個沒登錄的單別而整張開不起來。
            Assert.Null(ApprovalDocumentLinks.Resolve("ZZNoSuchDocType", "X-1"));
            Assert.Null(ApprovalDocumentLinks.Resolve("PurchaseOrder", null));
        }

        private static List<string> ContributorDocumentTypes()
        {
            var iface = typeof(IApprovalSyncContributor);
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("SmartFactory.MES", StringComparison.Ordinal) == true)
                .SelectMany(SafeTypes)
                .Where(t => t is { IsAbstract: false, IsInterface: false } && iface.IsAssignableFrom(t))
                .Select(t => t.GetProperty("DocumentType")?.GetValue(
                    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t)) as string)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            // 反射載入失敗時只丟掉載不到的那幾個型別，不要讓整支測試炸掉。
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
        }
    }
}
