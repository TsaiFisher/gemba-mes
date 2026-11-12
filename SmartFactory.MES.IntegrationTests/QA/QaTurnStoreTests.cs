using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// <c>QaTurnStore</c> 與 <c>ConfirmData</c> 的真實資料庫守門（§11.3）。
    ///
    /// <para><b>為什麼是整合測試而不是單元測試</b>：這一支要驗的三件事
    /// （UserId 一定進 where、已定局的列不得被改寫、修剪真的刪得掉）
    /// 全部靠 <c>ExecuteUpdateAsync</c>／<c>ExecuteDeleteAsync</c> 與真實查詢翻譯，
    /// InMemory provider 對它們的行為與 PostgreSQL 不同——在那上面測等於測了一個不存在的系統。</para>
    ///
    /// <para>沿用 <c>ScheduleIntegration</c> collection，理由見 <c>CollectionLayoutGuardTests</c>：
    /// 全專案共用同一個測試庫，不另開 collection。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class QaTurnStoreTests
    {
        private readonly AuthedScheduleFactory _factory;

        public QaTurnStoreTests(AuthedScheduleFactory factory) => _factory = factory;

        [Fact]
        public async Task 別人的turnId拿不到()
        {
            // 🚨 §11.3 的核心：鍵必須含 user id。只用 Id 查的話，任何人都能拿別人的
            //    turnId 接著執行，而那一次執行會用**他自己的**權限跑——
            //    症狀是「有人查到了他本來問不出來的東西」，且沒有任何錯誤訊息。
            var id = await SeedAsync("someone-else", resolved: false);

            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IQaTurnStore>();

            Assert.Null(await store.FindPendingAsync(id, "another-user"));
            Assert.NotNull(await store.FindPendingAsync(id, "someone-else"));
        }

        [Fact]
        public async Task 已定局的列不再是待確認_也不得被改寫()
        {
            var id = await SeedAsync("u-resolved", resolved: true);

            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IQaTurnStore>();

            // 已執行過的那一輪不能再被接續，否則同一個 turnId 可以重放無限次。
            Assert.Null(await store.FindPendingAsync(id, "u-resolved"));

            await store.ResolveAsync(id, "u-resolved", nameof(QaToolQueryOutcome.NoTool));
            Assert.Equal(nameof(QaToolQueryOutcome.Answered), await OutcomeOfAsync(id));
        }

        [Fact]
        public async Task 空的userId一律不寫也查不到()
        {
            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IQaTurnStore>();

            var written = await store.CreateAsync(new QaTurnDraft(
                Guid.NewGuid(), 1, string.Empty, "問題",
                nameof(QaToolQueryOutcome.NoTool), null, null, null, 0, true));

            // 寫一列 UserId 為空的資料，等於製造一列誰都對得上、也誰都對不上的孤兒。
            Assert.Null(written);
            Assert.Null(await store.FindPendingAsync(1, string.Empty));
        }

        [Fact]
        public async Task 修剪只刪掉截止時刻之前的列()
        {
            var old = await SeedAsync("u-trim", resolved: true, createdAt: DateTime.UtcNow.AddDays(-120));
            var recent = await SeedAsync("u-trim", resolved: true, createdAt: DateTime.UtcNow.AddDays(-1));

            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IQaTurnStore>();

            await store.TrimAsync(DateTime.UtcNow.AddDays(-90));

            Assert.Null(await FindAsync(old));
            Assert.NotNull(await FindAsync(recent));
        }

        [Fact]
        public async Task 確認端點_別人的turnId回ConfirmExpired而不是執行()
        {
            // 走完整 HTTP 路徑：登入身分是種子 admin，而這一列屬於別人。
            var id = await SeedAsync("someone-else", resolved: false, toolKey: "StockShortage");

            using var client = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });

            var token = await TokenAsync(client);

            var res = await client.PostAsync("/QaAssistant/ConfirmData", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("turnId", id.ToString()),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            ]));

            var body = await res.Content.ReadAsStringAsync();

            // **這一支不需要 Ollama**：ConfirmExpired 在任何推論之前就回來了，
            // 所以它在沒有推論服務的機器上照樣守得住這條界線。
            Assert.Contains("ConfirmExpired", body, StringComparison.Ordinal);

            // 那一列必須原封不動——被別人碰過的話，真正的擁有者按確認就會變成 Expired。
            Assert.NotNull(await FindAsync(id));
            Assert.Null((await FindAsync(id))!.ResolvedAt);
        }

        [Fact]
        public async Task 沒有讀取權限時也確認不了()
        {
            // ConfirmData 是**第三支** action。Ask／AskData 的閘門幫不了它，
            // 而它走到底就是執行一支報表工具。
            using var client = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });

            // ⚠️ **token 一定要在關掉權限之前拿。** [ValidateAntiForgeryToken] 是 filter，
            // 排在 action 本體之前——沒帶 token 收到的是 400，而 400 與 403 在斷言上
            // 都不是 200，看起來像「有擋」，實際上那一輪根本沒走到權限檢查。
            var token = await TokenAsync(client);

            var perm = await SetPermissionAsync(false);
            try
            {
                var res = await client.PostAsync("/QaAssistant/ConfirmData", new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("turnId", "1"),
                    new KeyValuePair<string, string>("__RequestVerificationToken", token),
                ]));

                Assert.Equal(System.Net.HttpStatusCode.Forbidden, res.StatusCode);
            }
            finally
            {
                await SetPermissionAsync(perm);
            }
        }

        // ── 另存為常用查詢（Phase 3，§十三）────────────────────────────────

        [Fact]
        public async Task 另存_把那一輪的工具與參數寫成常用查詢並回報丟掉的條件()
        {
            var id = await SeedAsync("__admin__", resolved: true, toolKey: "StockShortage",
                paramsJson: """{"keyword":"ABC","topN":5}""");

            var name = "IT-另存-" + Guid.NewGuid().ToString("N")[..8];
            var (ok, message) = await SaveQueryAsync(id, name);

            Assert.True(ok, message);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Data.ApplicationDbContext>();
            var def = await db.ReportDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Name == name);

            Assert.NotNull(def);
            Assert.Equal("StockShortage", def!.ToolKey);

            // keyword 有對應欄位 → 存進去；topN 沒有 → 丟掉，而且**必須在訊息裡講出來**。
            Assert.Contains("searchProduct", def.ParamsJson, StringComparison.Ordinal);
            Assert.Contains("ABC", def.ParamsJson, StringComparison.Ordinal);
            Assert.Contains("topN", message, StringComparison.Ordinal);

            // 存成的是常用查詢不是排程（§7.9：先存常用查詢，用順了再到排程畫面加排程）。
            Assert.Null(def.ScheduleKind);

            db.ReportDefinitions.Remove(await db.ReportDefinitions.FirstAsync(d => d.Name == name));
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task 另存_還沒執行過的那一輪存不起來()
        {
            // 🚨 只有真的跑出過結果的那一輪能存。待確認的那一輪連查都還沒查——
            //    存得起來的話，「另存」就變成一個憑空寫入查詢定義的入口，
            //    而那張定義日後可能被掛上排程、定期寄出（§9.2-3）。
            var id = await SeedAsync("__admin__", resolved: false, toolKey: "StockShortage");

            var (ok, _) = await SaveQueryAsync(id, "IT-不該存得起來");

            Assert.False(ok);
        }

        [Fact]
        public async Task 另存_別人的turnId存不起來()
        {
            var id = await SeedAsync("someone-else", resolved: true, toolKey: "StockShortage");

            var (ok, _) = await SaveQueryAsync(id, "IT-別人的");

            Assert.False(ok);
        }

        [Fact]
        public async Task 另存_沒有名稱時擋下()
        {
            var id = await SeedAsync("__admin__", resolved: true, toolKey: "StockShortage");

            var (ok, _) = await SaveQueryAsync(id, "   ");

            Assert.False(ok);
        }

        [Fact]
        public async Task 另存_沒有讀取權限時擋下()
        {
            using var client = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });

            var token = await TokenAsync(client);
            var perm = await SetPermissionAsync(false);
            try
            {
                var res = await client.PostAsync("/QaAssistant/SaveQuery", new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("turnId", "1"),
                    new KeyValuePair<string, string>("name", "x"),
                    new KeyValuePair<string, string>("__RequestVerificationToken", token),
                ]));

                Assert.Equal(System.Net.HttpStatusCode.Forbidden, res.StatusCode);
            }
            finally
            {
                await SetPermissionAsync(perm);
            }
        }

        /// <summary>打 /QaAssistant/SaveQuery，回 (success, message)。</summary>
        private async Task<(bool Ok, string Message)> SaveQueryAsync(long turnId, string name)
        {
            using var client = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });

            var token = await TokenAsync(client);

            var res = await client.PostAsync("/QaAssistant/SaveQuery", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("turnId", turnId.ToString()),
                new KeyValuePair<string, string>("name", name),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            ]));

            var body = await res.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return (doc.RootElement.GetProperty("success").GetBoolean(),
                    doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 取 AntiForgery token。**必須與送出用同一個 client**：它是 cookie ＋ 欄位成對驗證，
        /// 每個 <c>CreateClient</c> 有自己的 cookie 容器，換一個 client 送出收到的是 400。
        /// </summary>
        private static async Task<string> TokenAsync(HttpClient client)
        {
            var html = await (await client.GetAsync("/QaAssistant")).Content.ReadAsStringAsync();
            var token = System.Text.RegularExpressions.Regex.Match(
                html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(token), "對話畫面上找不到 AntiForgery token");
            return token;
        }

        /// <summary>
        /// 種一列 <c>QaTurn</c>。<paramref name="userId"/> 傳 <c>__admin__</c> 時代換成
        /// 種子 admin 的實際 Id——測試身分就是它（見 <c>TestAuthHandler</c>），
        /// 而本表的存取邊界完全建立在 <c>UserId</c> 完全相等上。
        /// </summary>
        private async Task<long> SeedAsync(
            string userId, bool resolved, DateTime? createdAt = null,
            string? toolKey = null, string paramsJson = "{}")
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QaDbContext>();

            if (userId == "__admin__")
            {
                var admin = await scope.ServiceProvider
                    .GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync("admin");
                userId = admin?.Id ?? throw new InvalidOperationException("測試庫沒有種子 admin。");
            }

            var row = new QaTurn
            {
                ThreadId = Guid.NewGuid(),
                RoundNo = 1,
                UserId = userId,
                Question = "測試提問",
                Outcome = resolved
                    ? nameof(QaToolQueryOutcome.Answered)
                    : nameof(QaToolQueryOutcome.NeedsConfirm),
                ToolKey = toolKey,
                ParamsJson = paramsJson,
                CandidatesJson = toolKey is null
                    ? null
                    : $"[{{\"toolKey\":\"{toolKey}\",\"paramsJson\":\"{{}}\"}}]",
                CreatedAt = createdAt ?? DateTime.UtcNow,
                ResolvedAt = resolved ? DateTime.UtcNow : null,
            };

            db.QaTurns.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }

        private async Task<QaTurn?> FindAsync(long id)
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<QaDbContext>()
                .QaTurns.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        }

        private async Task<string?> OutcomeOfAsync(long id) => (await FindAsync(id))?.Outcome;

        /// <summary>設定 admin 角色對 QaAssistant 的讀取權限，回傳**原本的值**供還原。</summary>
        private async Task<bool> SetPermissionAsync(bool canRead)
        {
            using var scope = _factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<SmartFactory.MES.Data.ApplicationDbContext>();

            var admin = await sp.GetRequiredService<UserManager<ApplicationUser>>()
                .FindByNameAsync("admin");
            var roleId = admin?.ActiveRoleId ?? "1";

            var perm = await db.MenuPermissions
                .FirstOrDefaultAsync(p => p.RoleId == roleId && p.MenuKey == "QaAssistant");

            // ⚠️ 新建的列 CanRead 預設是 false，若把它當成「原本的值」，還原時就會把權限
            // **永久留在關閉狀態**——而測試庫是跨進程共用的（見 CollectionLayoutGuardTests），
            // 下一輪別人的量測會看到一整頁消失，且症狀不是紅燈而是數字看起來很正常。
            // 沒有這一列時，原本的值就是 seeder 給的 true。
            var existed = perm != null;
            if (perm == null)
            {
                perm = new MenuPermission { RoleId = roleId, MenuKey = "QaAssistant" };
                db.MenuPermissions.Add(perm);
            }

            var previous = existed ? perm.CanRead : true;
            perm.CanRead = canRead;
            await db.SaveChangesAsync();
            return previous;
        }
    }
}
