using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartFactory.MES.IntegrationTests.MFG;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPS
{
    /// <summary>
    /// R-1 的後半段：報表工具開關開啟後，三支畫面**真的長在權限矩陣上**
    /// （人工驗收計畫 Wave R 的 R-1，manual-test-plan.md §10.2）。
    ///
    /// <para><b>單元測試驗不到的那一段</b>：<see cref="SmartFactory.MES.Tests.RPS"/> 的
    /// <c>ReportToolsFeatureUpgradeTests</c> 釘住的是「選單樹裡有那三項、動詞照實宣告、
    /// 升級時補出 16 列」——全部是 <c>ProjectZMenuContributor</c> 與
    /// <c>RolePermissionSeeder</c> 這兩個純函式之間的約定。矩陣**畫面**有沒有照著那份宣告
    /// 渲染，是另一回事：<c>_PermissionPanel.cshtml</c> 自己決定哪些勾選框要印出來
    /// （<c>item.SupportedActions.Contains("Create")</c>），宣告與渲染之間隔著一層檢視。</para>
    ///
    /// <para><b>為什麼「唯讀畫面不長寫入勾選框」值得端到端驗一次</b>：多印出來的勾選框
    /// **勾得下去也存得進去**，只是永遠沒有作用——收件匣沒有 Create 語意，那個欄位被設成 true
    /// 之後不會有任何畫面行為改變。症狀因此不是壞掉，而是「某角色全站唯讀」這類跨畫面不變式
    /// 被一筆假的寫入權悄悄打破，而稽核那一側看到的是一筆貨真價實的授權紀錄。</para>
    ///
    /// <para>⚠️ <b>權限矩陣有兩份渲染，而且各寫各的。</b><c>PermissionController.Edit</c> 依
    /// <c>X-Requested-With</c> 分流：帶了走側面板的 <c>_PermissionPanel.cshtml</c>，
    /// 沒帶走整頁的 <c>Edit.cshtml</c>——兩份都自己判一次
    /// <c>SupportedActions.Contains("Create")</c>。**使用者實際點得到的是側面板那一份**
    /// （<c>Views/Permission/Index.cshtml</c> 的 <c>openPermissionPanel</c> 明確帶了那個標頭），
    /// 整頁那份只有直接輸入網址才會走到。本檔因此兩份都驗：只驗整頁會驗到沒有人在看的畫面，
    /// 只驗側面板則漏掉另一份——而兩份之間的漂移**沒有任何訊號**，改了一邊漏了一邊，
    /// 畫面上看起來完全正常。這一點是突變驗證撞出來的：把整頁那份的動詞判斷改成恆真，
    /// 三支測試照樣全綠，因為當時它們打的正是使用者看不到的那一份。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection，理由見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection。
    /// 本檔**只讀不寫**——不動任何 <c>MenuPermission</c>，因此沒有還原問題。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReportToolsMenuMatrixIntegrationTests
    {
        /// <summary>Admin。矩陣畫面以 roleId 定位，這是 dev seed 沿用的既有慣例。</summary>
        private const string RoleId = "1";

        private readonly AuthedScheduleFactory _factory;

        public ReportToolsMenuMatrixIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        [Theory]
        [InlineData(true)]   // 側面板：使用者實際點得到的那一份
        [InlineData(false)]  // 整頁：只有直接輸入網址才會走到
        public async Task R1_權限矩陣畫面長出報表工具三列(bool sidePanel)
        {
            var html = await MatrixHtmlAsync(sidePanel);

            foreach (var menuKey in new[] { "ReportSchedule", "ReportInbox", "ReportRunHistory" })
                Assert.True(IndexOf(html, menuKey) is not null,
                    $"權限矩陣畫面上找不到 {menuKey} 這一列。開關開著卻沒有列，" +
                    "代表選單樹到矩陣畫面之間斷了——而畫面上看起來只是「少一頁」。");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task R1_排程維護那一列有四個動詞的勾選框但沒有列印(bool sidePanel)
        {
            var html = await MatrixHtmlAsync(sidePanel);
            var idx = RequireIndex(html, "ReportSchedule");

            Assert.True(HasCheckbox(html, idx, "CanRead"));
            Assert.True(HasCheckbox(html, idx, "CanCreate"));
            Assert.True(HasCheckbox(html, idx, "CanUpdate"));
            Assert.True(HasCheckbox(html, idx, "CanDelete"));

            // 排程沒有列印語意。多一個勾選框不會報錯，只會永遠沒有作用。
            Assert.False(HasCheckbox(html, idx, "CanPrint"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task R1_兩支唯讀畫面只長出讀取勾選框(bool sidePanel)
        {
            var html = await MatrixHtmlAsync(sidePanel);

            foreach (var menuKey in new[] { "ReportInbox", "ReportRunHistory" })
            {
                var idx = RequireIndex(html, menuKey);

                Assert.True(HasCheckbox(html, idx, "CanRead"),
                    $"{menuKey} 連讀取勾選框都沒有，那一列等於不能設定。");

                foreach (var action in new[] { "CanCreate", "CanUpdate", "CanDelete", "CanPrint" })
                    Assert.False(HasCheckbox(html, idx, action),
                        $"{menuKey} 是唯讀畫面，卻在矩陣上長出 {action} 勾選框。" +
                        "它勾得下去也存得進去，只是永遠沒有作用。");
            }
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        /// <param name="sidePanel">
        /// true＝帶 <c>X-Requested-With</c>，拿到側面板的 partial（使用者實際看到的那一份）；
        /// false＝整頁 <c>Edit.cshtml</c>。兩份各寫各的，所以兩份都要問一次。
        /// </param>
        private async Task<string> MatrixHtmlAsync(bool sidePanel)
        {
            using var client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var req = new HttpRequestMessage(HttpMethod.Get, $"/Permission/Edit/{RoleId}");
            if (sidePanel) req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var res = await client.SendAsync(req);

            // 302 代表驗證掉了或被強制改密／2FA 中介攔下——那不是「矩陣沒有那三列」，
            // 而是根本沒走到矩陣。先把它跟 200 分開，否則下面的斷言會以錯的理由紅。
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// 找出某個 MenuKey 在表單裡的索引。檢視用的是扁平索引
        /// （<c>permissions[i].MenuKey</c>），動詞勾選框掛在同一個 i 上，
        /// 所以要先拿到 i 才問得出「這一列有哪些勾選框」。
        /// </summary>
        private static int? IndexOf(string html, string menuKey)
        {
            var m = Regex.Match(html,
                @"name=""permissions\[(\d+)\]\.MenuKey""[^>]*value=""" + Regex.Escape(menuKey) + @"""");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

        private static int RequireIndex(string html, string menuKey) =>
            IndexOf(html, menuKey) ?? throw new Xunit.Sdk.XunitException(
                $"權限矩陣畫面上找不到 {menuKey} 這一列。");

        private static bool HasCheckbox(string html, int index, string action) =>
            html.Contains($@"name=""permissions[{index}].{action}""");
    }
}
