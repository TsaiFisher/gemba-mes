using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機「我在哪個廠」端點的守門。
    ///
    /// <para><b>為什麼需要它</b>：<c>ActivePlantId</c> 是使用者資料列上的欄位，桌機與手機
    /// 共用同一個值，而手機端在此之前**畫面上一個字都沒有**——現場人員的廠別取決於他
    /// 上次在電腦上切到哪，而他可能根本沒有電腦。這支端點是那個缺口的第一步（顯示），
    /// 切換是後續階段。</para>
    ///
    /// <para>走真 HTTP 而不是直接建構 controller：這支的重點有一半在
    /// <c>[Authorize]</c> 與 <c>IUserContext</c> 的解析上，而 <c>IUserContext</c> 解不出
    /// 使用者時 <c>PlantId</c> 是 <c>null</c>——那與「使用者真的沒有設定廠別」在回應上
    /// 長得一模一樣。直接建構就得自己塞一個假的 <c>IUserContext</c>，等於把最容易錯的
    /// 那一段換成手工假資料。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class MobilePlantContextTests
    {
        private readonly AuthedScheduleFactory _factory;

        public MobilePlantContextTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task 回傳的是登入者資料列上的ActivePlantId()
        {
            // ⚠️ **這支測試區分不出 `Plants.First()` 這種實作。** 測試庫裡 admin 的廠別是
            // P1，而 P1 剛好也是 Plants 排序後的第一筆——兩者同值，斷言分不開。
            // 要真的分開，得把 admin 的 ActivePlantId 改成別的廠，但這是**共用測試庫**，
            // 改使用者資料列會波及其他人正在跑的測試（見 CollectionLayoutGuardTests）。
            // 所以這裡釘的是比較弱但仍有意義的一件事：回傳值跟著**使用者資料列**走。
            string? expected;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                expected = (await db.Users.AsNoTracking()
                    .SingleAsync(u => u.UserName == "admin")).ActivePlantId;
            }

            // 兩邊都是 null 也會相等——那時這支測試什麼都沒驗到，而且看起來是綠的。
            Assert.False(string.IsNullOrEmpty(expected),
                "測試帳號沒有 ActivePlantId，本測試將無條件通過；請先確認測試庫的種子資料。");

            var root = await GetCurrentAsync();

            Assert.Equal(expected, root.GetProperty("plantId").GetString());
        }

        [Fact]
        public async Task 廠別代碼查得到主檔時會一併給名稱()
        {
            // 畫面顯示的是「名稱 (代碼)」。名稱漏掉不會出錯，只會退化成光有代碼——
            // 而那正是這支端點想解決的問題（現場看不懂自己在哪），退化了也沒有訊號。
            var root = await GetCurrentAsync();
            var plantId = root.GetProperty("plantId").GetString();

            string? expectedName;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                expectedName = await db.Plants.AsNoTracking()
                    .Where(p => p.Id == plantId).Select(p => p.Name).FirstOrDefaultAsync();
            }

            if (expectedName == null) return;   // 測試庫沒有這筆主檔時不強求

            Assert.Equal(expectedName, root.GetProperty("name").GetString());
        }

        [Fact]
        public async Task 名稱欄位一定存在_查不到主檔時是null而不是省略()
        {
            // 前端是 `data.name || data.nameEn` 這樣取的，欄位不見時讀到 undefined
            // 也會走同一條路——但那是巧合不是契約，序列化設定一改（例如哪天加上
            // IgnoreNullValues）就會靜靜換成另一種行為。
            var root = await GetCurrentAsync();

            Assert.True(root.TryGetProperty("name", out _), "name 欄位不見了。");
            Assert.True(root.TryGetProperty("nameEn", out _), "nameEn 欄位不見了。");
        }

        // ── 切換（第三階段）────────────────────────────────────────────────

        [Fact]
        public async Task 可切換清單包含目前廠別而且旗標與角色設定一致()
        {
            using var client = Client();
            var root = await JsonAsync(await client.GetAsync("/api/mobile/plants"));

            var current = root.GetProperty("currentPlantId").GetString();
            var ids = root.GetProperty("plants").EnumerateArray()
                .Select(p => p.GetProperty("id").GetString()).ToList();

            Assert.Contains(current, ids);

            // 旗標必須反映實際的角色指派，不是清單長度。
            bool restrictedInDb;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var roleId = (await db.Users.AsNoTracking()
                    .SingleAsync(u => u.UserName == "admin")).ActiveRoleId;
                restrictedInDb = await db.RolePlantAssignments.AsNoTracking()
                    .AnyAsync(a => a.RoleId == roleId);
            }

            Assert.Equal(restrictedInDb, root.GetProperty("roleIsRestricted").GetBoolean());
        }

        [Fact]
        public async Task 切到不存在的廠別回403_而且沒有動到使用者的廠別()
        {
            // 這條是**不限廠**角色的存在性檢查（測試庫裡 RolePlantAssignments 是空的，
            // 所以 admin 走的正是那條分支）。少了它，偽造的代碼會被寫進 ActivePlantId，
            // 使用者接著看到一個空系統——所有清單都空的，而且沒有任何錯誤訊息。
            var before = await ActivePlantIdAsync();

            using var client = Client();
            var res = await client.PostAsync("/api/mobile/plants/switch",
                JsonContent("{\"plantId\":\"ZZNOSUCHPLANT\"}"));

            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

            // 只驗狀態碼不夠：一個「先寫了再回 403」的實作也會通過。
            Assert.Equal(before, await ActivePlantIdAsync());
        }

        [Fact]
        public async Task 切到自己目前所在的廠會成功()
        {
            // ⚠️ 刻意切到**同一個廠**：這是共用測試庫，改掉 admin 的 ActivePlantId
            // 會波及正在平行執行的其他 collection（見 CollectionLayoutGuardTests）。
            // 切回自己所在的廠一樣走完整條授權路徑，但淨變化是零。
            //
            // 成功路徑的語意（真的有寫進去、擋下時不寫）由 Core 的
            // PlantSwitchServiceTests 15 支負責；這裡驗的是 HTTP 這一層有沒有接對。
            var current = await ActivePlantIdAsync();
            Assert.False(string.IsNullOrEmpty(current), "測試帳號沒有 ActivePlantId。");

            using var client = Client();
            var res = await client.PostAsync("/api/mobile/plants/switch",
                JsonContent($"{{\"plantId\":\"{current}\"}}"));

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var root = await JsonAsync(res);
            Assert.Equal(current, root.GetProperty("plantId").GetString());
        }

        /// <summary>
        /// 廠別預設語言（2026-10-27）：個人語言留空的帳號，/me 與切換廠別都回該廠的預設語言；
        /// 有個人語言的以個人為準，切換也不改。走真 HTTP（含登入身分解析）。
        /// ⚠️ 共用測試庫：只改 admin 的語言與所在廠的預設語言，finally 一律還原；切換同上只切回目前所在的廠。
        /// </summary>
        [Fact]
        public async Task 個人語言留空時跟著廠別預設語言_有個人語言時以個人為準()
        {
            var current = await ActivePlantIdAsync();
            Assert.False(string.IsNullOrEmpty(current), "測試帳號沒有 ActivePlantId。");
            var (origLang, origPlantLang) = await LanguagesAsync(current!);
            try
            {
                await SetLanguagesAsync(current!, personal: null, plantDefault: "en-US");

                using (var client = Client())
                {
                    var me = await JsonAsync(await client.GetAsync("/api/mobile/auth/me"));
                    Assert.Equal("en-US", me.GetProperty("language").GetString());

                    var res = await client.PostAsync("/api/mobile/plants/switch", JsonContent($"{{\"plantId\":\"{current}\"}}"));
                    Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                    var sw = await JsonAsync(res);
                    Assert.True(sw.GetProperty("followsPlant").GetBoolean());
                    Assert.Equal("en-US", sw.GetProperty("language").GetString());
                    Assert.Contains("c=en-US", Uri.UnescapeDataString(string.Join(";", res.Headers.GetValues("Set-Cookie"))));
                }

                await SetLanguagesAsync(current!, personal: "zh-TW", plantDefault: "en-US");

                using (var client = Client())
                {
                    var me = await JsonAsync(await client.GetAsync("/api/mobile/auth/me"));
                    Assert.Equal("zh-TW", me.GetProperty("language").GetString());

                    var sw = await JsonAsync(await client.PostAsync("/api/mobile/plants/switch", JsonContent($"{{\"plantId\":\"{current}\"}}")));
                    Assert.False(sw.GetProperty("followsPlant").GetBoolean());
                    Assert.Equal("zh-TW", sw.GetProperty("language").GetString());
                }
            }
            finally
            {
                await SetLanguagesAsync(current!, origLang, origPlantLang);
            }
        }

        private async Task<(string? Personal, string? PlantDefault)> LanguagesAsync(string plantId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var personal = (await db.Users.AsNoTracking().SingleAsync(u => u.UserName == "admin")).Language;
            var plant = (await db.Plants.AsNoTracking().SingleAsync(p => p.Id == plantId)).DefaultLanguage;
            return (personal, plant);
        }

        private async Task SetLanguagesAsync(string plantId, string? personal, string? plantDefault)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Users.Where(u => u.UserName == "admin").ExecuteUpdateAsync(u => u.SetProperty(x => x.Language, personal));
            await db.Plants.Where(p => p.Id == plantId).ExecuteUpdateAsync(p => p.SetProperty(x => x.DefaultLanguage, plantDefault));
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private static StringContent JsonContent(string json) =>
            new(json, System.Text.Encoding.UTF8, "application/json");

        private static async Task<JsonElement> JsonAsync(HttpResponseMessage res) =>
            JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();

        private async Task<string?> ActivePlantIdAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return (await db.Users.AsNoTracking().SingleAsync(u => u.UserName == "admin")).ActivePlantId;
        }

        private async Task<JsonElement> GetCurrentAsync()
        {
            using var client = Client();

            var res = await client.GetAsync("/api/mobile/plants/current");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
        }
    }
}
