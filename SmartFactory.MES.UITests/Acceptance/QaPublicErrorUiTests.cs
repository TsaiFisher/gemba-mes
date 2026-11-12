using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 人工驗收 N-11 的自動版：沒有修改權限的使用者，在推論服務連不上時只看到通用訊息——
    /// 首頁健康狀態、手冊問答、資料查詢三處都不得帶出推論端點、埠號或模型名稱（安全評估 2026-10-07 #5）。
    ///
    /// <para>要穩定重現「連不上」，受測 app 必須以一個不通的位址啟動，例如
    /// <c>Features__QaAssistant__Enabled=true Features__QaAssistant__OllamaUrl=http://127.0.0.1:9</c>，
    /// 並設 <c>QA_UI_INFERENCE_DOWN=1</c>。接上真正的推論服務時這支會因為「回答成功」而驗不到錯誤路徑，所以預設略過。</para>
    ///
    /// <para>帳號自備（e2e.qareader／E2E-QARO，密碼同 e2e.readonly）：只有 QaAssistant 的讀取權限。
    /// 不借用 E2E-RO——ReadOnlyRoleUiTests 依賴那個角色「只有兩個畫面」。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class QaPublicErrorUiTests
    {
        private const string UserName = "e2e.qareader";
        private const string Password = "E2eReadOnly!2026";

        /// <summary>任何一個出現在畫面上都算洩漏內部架構。</summary>
        private static readonly string[] Internals = ["127.0.0.1", "localhost", "11434", "11435", "ollama", "gemma", "snowflake", "/api/"];

        private readonly UiTestFixture _fixture;
        public QaPublicErrorUiTests(UiTestFixture fixture) => _fixture = fixture;

        [QaInferenceDownFact]
        public async Task N11_一般使用者_推論服務連不上時_首頁與兩種提問都只看到通用訊息()
        {
            await SeedUserAsync();
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            await using (context)
            {
                var login = new LoginPage(page, _fixture.BaseUrl);
                await login.GotoAsync();
                await login.LoginAsync(UserName, Password);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.DoesNotContain("/Identity/Account/Login", page.Url, StringComparison.OrdinalIgnoreCase);

                var qa = new QaAssistantPage(page, _fixture.BaseUrl);
                await qa.GotoAsync();
                var home = await page.InnerTextAsync("body");
                Assert.Contains("請聯絡系統管理員", home);
                AssertNoInternals(home, "首頁健康狀態");

                await qa.AskAsync("本月不良率最高的工單");
                var manual = await LastAnswerAsync(page, qa);
                Assert.Contains("系統管理員", manual);
                AssertNoInternals(manual, "手冊問答的錯誤訊息");

                await qa.SelectDataModeAsync();
                await qa.AskAsync("查 P2 的不良率");
                var data = await LastAnswerAsync(page, qa);
                Assert.Contains("系統管理員", data);
                AssertNoInternals(data, "資料查詢的錯誤訊息");
            }
        }

        [QaInferenceDownFact]
        public async Task N11_有修改權限的管理者_首頁看得到診斷細節()
        {
            // 對照組：同一個環境下管理者看得到細節，證明上一支的通用訊息是權限分流的結果，
            // 不是健康檢查本身就只吐通用字串。
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await new QaAssistantPage(page, _fixture.BaseUrl).GotoAsync();
            var home = await page.InnerTextAsync("body");
            Assert.DoesNotContain("請聯絡系統管理員。", home);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private static void AssertNoInternals(string text, string where)
        {
            foreach (var token in Internals)
                Assert.False(text.Contains(token, StringComparison.OrdinalIgnoreCase), $"{where}帶出了內部資訊「{token}」：{text}");
        }

        private static async Task<string> LastAnswerAsync(IPage page, QaAssistantPage qa)
        {
            await page.WaitForFunctionAsync("() => !document.getElementById('qaQuestion').disabled",
                null, new PageWaitForFunctionOptions { Timeout = 60_000 });
            return await qa.LastBubble.InnerTextAsync();
        }

        private static Task SeedUserAsync() => SeedFixture.ExecAsync("""
            INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","NameZh","NameEn","ConcurrencyStamp","OrganizationUnitId")
            VALUES ('E2E-QARO','E2E 助理唯讀','E2E 助理唯讀','E2E 助理唯讀','E2E QA Read-only','e2e-qaro','ORG-ROOT')
            ON CONFLICT ("Id") DO UPDATE SET "OrganizationUnitId"='ORG-ROOT';
            INSERT INTO "RolePlantAssignments" ("RoleId","PlantId","IsDefault")
            VALUES ('E2E-QARO','P1',true)
            ON CONFLICT ("RoleId","PlantId") DO NOTHING;
            DELETE FROM "MenuPermissions" WHERE "RoleId" = 'E2E-QARO';
            INSERT INTO "MenuPermissions" ("Id","RoleId","MenuKey","CanRead","CanCreate","CanUpdate","CanDelete","CanPrint")
            VALUES ('e2e-qaro-qa','E2E-QARO','QaAssistant',true,false,false,false,false);
            INSERT INTO "AspNetUsers"
              ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PasswordHash",
               "SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled",
               "AccessFailedCount","MustChangePassword","ActivePlantId","ActiveRoleId","Language","TimeZoneId")
            VALUES ('e2e00000-0000-4000-8000-0000000000a1','e2e.qareader','E2E.QAREADER',
                    'e2e.qareader@example.invalid','E2E.QAREADER@EXAMPLE.INVALID',true,
                    'AQAAAAIAAYagAAAAEAABAgMEBQYHCAkKCwwNDg9L5Dqic0V6FQ+q9JwwjmE50sylkRwobPDlqdJX0VNCyA==',
                    'E2E-QARO-STAMP','e2e-qaro',false,false,true,0,false,'P1','E2E-QARO','zh-TW','Asia/Taipei')
            ON CONFLICT ("Id") DO UPDATE
              SET "PasswordHash"=EXCLUDED."PasswordHash", "LockoutEnd"=NULL, "AccessFailedCount"=0,
                  "TwoFactorEnabled"=false, "MustChangePassword"=false,
                  "ActivePlantId"='P1', "ActiveRoleId"='E2E-QARO';
            INSERT INTO "AspNetUserRoles" ("UserId","RoleId")
            VALUES ('e2e00000-0000-4000-8000-0000000000a1','E2E-QARO')
            ON CONFLICT ("UserId","RoleId") DO NOTHING;
            """);
    }

    /// <summary>受測 app 以不通的推論位址啟動時才跑（見 <see cref="QaPublicErrorUiTests"/>）。</summary>
    public sealed class QaInferenceDownFactAttribute : FactAttribute
    {
        public QaInferenceDownFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("QA_UI_INFERENCE_DOWN") != "1")
                Skip = "需要受測 app 以不通的 Features__QaAssistant__OllamaUrl 啟動，並設 QA_UI_INFERENCE_DOWN=1。";
        }
    }
}
