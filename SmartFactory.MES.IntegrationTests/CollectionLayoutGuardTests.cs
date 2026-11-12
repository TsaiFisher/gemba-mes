using System.Reflection;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 整合測試的 xUnit collection 佈局守門。
    ///
    /// 這個專案的所有測試共用同一個 PostgreSQL 測試庫。不同 collection 預設**平行**執行，
    /// 而每個 collection 的 fixture 都會跑 migration 與種子——平行時兩邊同時插入相同主檔
    /// （Plants / MenuPermissions）或同時建立型別，撞成 <c>23505</c> 與 <c>pg_type</c> 競態。
    ///
    /// 症狀特別難認：失敗數每輪都不一樣，而且會擴散到與改動完全無關的類別上，
    /// 看起來像「動到了什麼共用的東西」，實際上只是新增了一個 collection。
    /// 現行解法是兩道：<c>AssemblyInfo.cs</c> 的
    /// <c>CollectionBehavior(DisableTestParallelization = true)</c>，
    /// 以及「不要再多開 collection」這個口頭約定。
    ///
    /// 口頭約定守不住——所以把它變成測試。新增 collection 不是禁止的事，
    /// 但必須連同測試庫隔離一起想清楚，而這支測試會強迫那個決定被看見。
    /// </summary>
    public class CollectionLayoutGuardTests
    {
        /// <summary>目前允許的 collection：兩者都會各自跑 migration＋種子。</summary>
        private static readonly string[] Allowed = { "Integration", "ScheduleIntegration" };

        [Fact]
        public void IntegrationTests_UseOnlyKnownCollections()
        {
            // xUnit v2 的 CollectionAttribute 不公開名稱屬性，名稱只存在建構子引數裡，
            // 故走 CustomAttributeData 取值（不綁 xUnit 版本）。
            var found = Assembly.GetExecutingAssembly().GetTypes()
                .SelectMany(t => t.GetCustomAttributesData())
                .Where(d => d.AttributeType == typeof(CollectionAttribute))
                .Select(d => d.ConstructorArguments.Count > 0
                             ? d.ConstructorArguments[0].Value as string
                             : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            // 掃描器自我檢查：抓不到任何 collection 表示反射條件失效，規則等於關閉
            Assert.NotEmpty(found);

            var unexpected = found.Except(Allowed, StringComparer.Ordinal).ToArray();
            Assert.True(unexpected.Length == 0,
                "整合測試出現未預期的 xUnit collection：" + string.Join("、", unexpected) + "。\n"
                + "所有整合測試共用同一個 PostgreSQL 測試庫，每個 collection 的 fixture 各自跑 "
                + "migration 與種子；多一個 collection 就多一組平行的種子寫入，會撞 23505 / pg_type 競態，"
                + "而失敗會擴散到無關的類別上、每輪數字還不一樣。\n"
                + "要新增 collection 必須連同測試庫隔離一起處理（例如各自獨立的資料庫），"
                + "處理完再把名稱加進本測試的允許清單。");
        }

        /// <summary>
        /// 平行化必須是關閉的。這條與上面互補：上面管「有幾個 collection」，
        /// 這條管「collection 之間會不會同時跑」——只要有一邊失守就會踩到同一個競態。
        /// </summary>
        [Fact]
        public void CrossCollectionParallelization_IsDisabled()
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<CollectionBehaviorAttribute>();

            Assert.NotNull(attr);
            Assert.True(attr!.DisableTestParallelization,
                "AssemblyInfo.cs 的 CollectionBehavior(DisableTestParallelization = true) 被移除了。"
                + "兩個 collection 會恢復平行執行並共用同一個測試庫，種子互撞。");
        }
    }
}
