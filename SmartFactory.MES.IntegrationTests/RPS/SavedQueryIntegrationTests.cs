using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPS
{
    /// <summary>
    /// 常用查詢（§7.9 L2）的真實 HTTP 端到端守門。
    ///
    /// 為什麼非整合測試不可：這個功能有兩處**建置與單元測試都看不到、只有真的打開畫面才會炸**的接縫——
    ///   ① `_SavedQueries` 是跨模組共用 partial，解析時 <c>{1}</c> 是**呼叫端 controller**，
    ///      RPSModule 若只註冊 <c>/Modules/RPS/Views/{1}/{0}</c>，六張報表全部找不到這個 partial。
    ///   ② `/SavedQuery/Run/{id}` 的轉導網址是字串組出來的，組錯只會 404。
    ///
    /// 沿用 <see cref="AuthedScheduleFactory"/> 與既有的 "ScheduleIntegration" collection：
    /// 本專案所有整合測試共用同一個 PostgreSQL 測試庫，**不另開 collection**
    /// （見 <c>CollectionLayoutGuardTests</c>：多一個 collection 就多一組平行種子寫入）。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class SavedQueryIntegrationTests
    {
        private const string RoleId = "1";

        // 測試庫是重跑庫，名稱帶前綴以便每輪自清，避免撞唯一鍵 (OwnerUserId, Name)。
        private const string NamePrefix = "ITEST-SQ-";

        private readonly AuthedScheduleFactory _factory;

        public SavedQueryIntegrationTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            Prepare();
        }

        /// <summary>清掉前幾輪殘留，並確保 admin 角色對兩張受測報表有權限。</summary>
        private void Prepare()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var stale = db.ReportDefinitions.Where(d => d.Name.StartsWith(NamePrefix)).ToList();
            if (stale.Count > 0) db.ReportDefinitions.RemoveRange(stale);

            // 兩支畫面的閘門都是 CanRead（StockShortage 於 2026-08-14 由 CanCreate 改來）；
            // 三個動詞都給，測的不是授權矩陣而是常用查詢本身。
            foreach (var key in new[] { "StockShortage", "SalesRanking" })
            {
                var perm = db.MenuPermissions.FirstOrDefault(p => p.RoleId == RoleId && p.MenuKey == key);
                if (perm == null)
                {
                    perm = new MenuPermission { RoleId = RoleId, MenuKey = key };
                    db.MenuPermissions.Add(perm);
                }
                perm.CanRead = perm.CanCreate = perm.CanPrint = true;
            }

            db.SaveChanges();
        }

        private HttpClient Client() => _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        private static string ExtractToken(string html)
        {
            var m = Regex.Match(html,
                @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(m.Success, "報表畫面上找不到 AntiForgery token——partial 沒渲染出來");
            return m.Groups[1].Value;
        }

        // ── ① partial 真的在報表畫面上渲染出來 ──────────────────────────

        [Theory]
        [InlineData("/StockShortage/Index",       "StockShortage")]
        [InlineData("/SalesRanking/Index",        "SalesRanking")]
        [InlineData("/InventoryValuation/Index",  "InventoryValuation")]
        [InlineData("/SlowMovingInventory/Index", "SlowMovingInventory")]
        public async Task 報表畫面渲染常用查詢入口(string url, string toolKey)
        {
            using var client = Client();

            var resp = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var html = await resp.Content.ReadAsStringAsync();

            // 這三個斷言合起來就是「Shared 檢視路徑有註冊」的證明：
            // 少了它 Html.PartialAsync 會直接擲例外，連 200 都拿不到。
            Assert.Contains("rps-saved-queries", html);
            Assert.Contains($"data-tool-key=\"{toolKey}\"", html);
            Assert.Contains("/js/saved-queries.js", html);
        }

        [Fact]
        public async Task 有日期區間的報表才出現期間下拉()
        {
            using var client = Client();

            var withRange    = await (await client.GetAsync("/SalesRanking/Index")).Content.ReadAsStringAsync();
            var withoutRange = await (await client.GetAsync("/StockShortage/Index")).Content.ReadAsStringAsync();

            // §7.9-1：只有吃 fromDate/toDate 的畫面需要（也必須）選相對期間。
            Assert.Contains("rps-sq-period", withRange);
            Assert.DoesNotContain("rps-sq-period", withoutRange);
        }

        // ── ② 存 → 列 → 執行 的完整往返 ─────────────────────────────────

        [Fact]
        public async Task 存檔後可列出並轉導回帶參數的報表畫面()
        {
            using var client = Client();
            var name = NamePrefix + "shortage";

            var page  = await (await client.GetAsync("/StockShortage/Index")).Content.ReadAsStringAsync();
            var token = ExtractToken(page);

            var save = await client.PostAsync("/SavedQuery/Save", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("toolKey", "StockShortage"),
                new KeyValuePair<string, string>("name", name),
                new KeyValuePair<string, string>("filters[searchProduct]", "ITEST-P1"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }));
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Contains("\"success\":true", await save.Content.ReadAsStringAsync());

            var list = await (await client.GetAsync("/SavedQuery/List?toolKey=StockShortage"))
                .Content.ReadAsStringAsync();
            Assert.Contains(name, list);
            Assert.Contains("\"isAuthorized\":true", list);

            var id = ResolveId(name);

            var run = await client.GetAsync($"/SavedQuery/Run/{id}");
            Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);

            var target = run.Headers.Location!.ToString();
            Assert.StartsWith("/StockShortage/Index?searched=true", target);
            Assert.Contains("searchProduct=ITEST-P1", target);

            // 轉導的網址必須真的打得開——組錯只會在這一步變成 404。
            var followed = await client.GetAsync(target);
            Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        }

        [Fact]
        public async Task 相對期間在執行時才換算成絕對日期()
        {
            using var client = Client();
            var name = NamePrefix + "ranking";

            var page  = await (await client.GetAsync("/SalesRanking/Index")).Content.ReadAsStringAsync();
            var token = ExtractToken(page);

            var save = await client.PostAsync("/SavedQuery/Save", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("toolKey", "SalesRanking"),
                new KeyValuePair<string, string>("name", name),
                new KeyValuePair<string, string>("period", "LastMonth"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }));
            Assert.Contains("\"success\":true", await save.Content.ReadAsStringAsync());

            var run = await client.GetAsync($"/SavedQuery/Run/{ResolveId(name)}");
            Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);

            var target = run.Headers.Location!.ToString();

            // 存的是 LastMonth，轉導時才變成當下的上個月——存絕對日期的話
            // 下個月點開還是同一段期間，那正是常用查詢的反面（§7.9-1）。
            var expected = DateOnly.FromDateTime(DateTime.Today).AddMonths(-1);
            Assert.Matches(@"fromDate=\d{4}-\d{2}-01", target);
            Assert.Contains($"fromDate={expected.Year:0000}-{expected.Month:00}-01", target);
        }

        [Fact]
        public async Task 有日期區間的報表未給期間_存檔被擋()
        {
            using var client = Client();

            var page  = await (await client.GetAsync("/SalesRanking/Index")).Content.ReadAsStringAsync();
            var token = ExtractToken(page);

            var save = await client.PostAsync("/SavedQuery/Save", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("toolKey", "SalesRanking"),
                new KeyValuePair<string, string>("name", NamePrefix + "noperiod"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            }));

            var body = await save.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":false", body);
            Assert.Contains("SavedQuery_PeriodRequired", body);
        }

        [Fact]
        public async Task 缺AntiForgery權杖的存檔被擋()
        {
            using var client = Client();

            var save = await client.PostAsync("/SavedQuery/Save", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("toolKey", "StockShortage"),
                new KeyValuePair<string, string>("name", NamePrefix + "notoken"),
            }));

            Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        }

        private int ResolveId(string name)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = db.ReportDefinitions.AsNoTracking().FirstOrDefault(d => d.Name == name);
            Assert.True(row is not null, $"常用查詢「{name}」沒有存進資料庫");
            return row!.Id;
        }
    }
}
