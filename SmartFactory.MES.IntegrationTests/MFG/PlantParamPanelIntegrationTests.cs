using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 廠別參數設定區塊的**渲染**守門（`plant-scoped-system-params.md` §3.5 方案丙）。
    ///
    /// 單元測試釘住 `PlantFormEnhancementFilter` 的投影與持久化，但那一層完全看不到
    /// 「使用者到底有沒有看到這個區塊」——這條鏈有四個環節，每一環壞掉都是**安靜的**：
    ///   ① Core 的 `_PlantForm.cshtml` 是否真的有那一行 `PartialAsync`（要靠 upstream 同步進來）
    ///   ② ProjectZ 的 `_PlantFormModuleExtensions.cshtml` 是否覆寫成功（檢視搜尋路徑）
    ///   ③ `_ProjectZPlantParams` → `_PlantParamField` 兩層 partial 是否解析得到
    ///   ④ Filter 有沒有被 `BASModule` 註冊進 MVC 管線（沒註冊時區塊整段不出現，且不報錯）
    ///
    /// 少了這支，上面任何一環回歸都只會表現成「面板上那一塊不見了」，沒有任何測試會紅。
    ///
    /// 寫入端（`SavePanel_*`）補的是另一個單元層看不到的環節：Filter 有沒有被掛在
    /// **POST** 那條路上。單元測試直接建構 Filter 再餵假的 IFormCollection，
    /// 就算 `BASModule` 忘了註冊、或 AntiForgery 先擋掉，單元層一樣全綠。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class PlantParamPanelIntegrationTests
    {
        private const string PlantId = "P1";

        private readonly AuthedScheduleFactory _factory;

        public PlantParamPanelIntegrationTests(AuthedScheduleFactory factory)
        {
            _factory = factory;
            EnsurePlant();
        }

        /// <summary>
        /// 面板實際的取法：帶 X-Requested-With 才回**裸 partial**（`PlantController.Edit` 的
        /// `IsAjax` 分支）。不帶標頭會回含 layout 的整頁——測試若照那樣比對，
        /// 會把 layout 的 script 與樣式一起算進區塊裡。
        /// </summary>
        private async Task<string> GetPanelAsync()
        {
            var client = _factory.CreateClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"/Plant/Edit/{PlantId}");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return await resp.Content.ReadAsStringAsync();
        }

        private void EnsurePlant()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!db.Plants.Any(p => p.Id == PlantId))
            {
                db.Plants.Add(new Plant { Id = PlantId, Name = "整合測試廠" });
                db.SaveChanges();
            }
        }

        [Fact]
        public async Task PlantEditPanel_RendersTheParamSection()
        {
            var html = await GetPanelAsync();

            // 目錄裡每一個參數都要在畫面上長出「覆寫」核取方塊與值控制項。
            // 逐一比對而不是只看區塊在不在——漏渲染一格是這裡最可能發生、也最難察覺的回歸。
            foreach (var def in PlantParamCatalog.All)
            {
                Assert.Contains($"name=\"{def.OverrideField}\"", html);
                Assert.Contains($"name=\"{def.ValueField}\"", html);
            }
        }

        /// <summary>
        /// 廠別面板必須長成兩個頁籤：第一頁是廠別本身的欄位，第二頁才是本部署專案的參數覆寫。
        ///
        /// 這層排版**刻意留在 ProjectZ**（`Views/Plant/{Index,_PlantForm}.cshtml` 就地漂移，
        /// 已登錄 `scripts/core-drift-baseline.txt`）：頁籤是為了「廠別參數覆寫」才長出來的，
        /// 而那是本專案獨有的概念，Core 的廠別表單維持單頁、只管廠別本身的欄位。
        ///
        /// 兩個頁籤的名字都由本 partial 經 ViewData 供給宿主表單
        /// （`Html.PartialAsync(name)` 沿用呼叫端同一個 `ViewDataDictionary` 實例，所以傳得到），
        /// 宿主少拿到任一個名字就會退回單頁排版——那是純視覺缺陷，
        /// 除了這支測試沒有任何東西會察覺（編譯得過、欄位也存得進去）。
        /// </summary>
        [Fact]
        public async Task PlantParamsPartial_RendersInsideHostFormTab()
        {
            // HtmlEncoder.Default 會把非 ASCII 逐字轉成 &#x5EE0; 這種數值實體，
            // 直接比對中文字串永遠不會命中——先解碼再比。
            var html = System.Net.WebUtility.HtmlDecode(await GetPanelAsync());

            // 結構：兩個頁籤與兩個對應的內容區都要在。
            Assert.Contains("nav-tabs", html, StringComparison.Ordinal);
            Assert.Contains("id=\"plantFormPaneBasic\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"plantFormPaneExtension\"", html, StringComparison.Ordinal);

            // 頁籤按鈕在 <form> 裡一定要是 type="button"，否則按頁籤等於送出表單。
            Assert.DoesNotContain("<button class=\"nav-link active\" type=\"submit\"", html, StringComparison.Ordinal);

            // 名字：直接讀 resx，不經 IStringLocalizer——測試跑在哪個文化不該影響這條斷言，
            // 兩個語系任一命中就代表「名字有出現」。
            foreach (var key in new[] { "BasicInfoTab", "PlantParamSettings" })
            {
                var titles = new[] { "zh-TW", "en-US" }
                    .Select(c => Path.Combine(RepoRoot(), "Resources", "Views", "Plant",
                                              $"_ProjectZPlantParams.{c}.resx"))
                    .Select(path => ResxValue(path, key))
                    .ToList();

                Assert.All(titles, t => Assert.False(string.IsNullOrWhiteSpace(t), $"resx 缺少 {key}"));
                Assert.Contains(titles, t => html.Contains(t!, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// 參數區塊在整張表單裡**只能出現一次**。
        ///
        /// <para>2026-09-08 使用者回報：「設定參數移到第 2 頁後，第一頁的應該要清除才是」。
        /// 成因是 `_PlantForm.cshtml` 把區塊搬進頁籤之後，**搬移前的原始渲染點沒有刪**
        /// （`.tab-content` 收掉之後還有一行 `PartialAsync("_PlantFormModuleExtensions")`），
        /// 於是同一份欄位被輸出兩次：一次在第 2 頁的 pane 裡，一次接在基本資料下面。</para>
        ///
        /// <para>⚠️ **這不只是外觀重複**。兩份的 <c>name</c> 完全相同，而
        /// <c>PlantFormEnhancementFilter</c> 讀值走 <c>LastOrDefault()</c>、讀勾選走
        /// <c>Any(v == "true")</c>：
        /// <list type="bullet">
        /// <item>文字／數值／選項欄位取的是**最後一份**，也就是頁籤外那份殘留——
        /// 使用者在第 2 頁改的值會被安靜丟掉，畫面還會顯示存檔成功。</item>
        /// <item>布林欄位（含「覆寫」核取方塊）只要任一份是 true 就算 true——
        /// 在第 2 頁取消勾選**永遠關不掉**，因為殘留那份仍送出 true。</item>
        /// </list>
        /// 兩種都沒有錯誤訊息，正是本專案反覆踩到的「存檔成功但沒改到」。</para>
        ///
        /// <para>釘的是**次數**不是位置：位置會隨排版調整，而「只准一份」是這個缺陷的本質。</para>
        /// </summary>
        [Fact]
        public async Task ParamSection_RendersExactlyOnce()
        {
            var html = await GetPanelAsync();

            // 存在標記：Filter 用它區分「勾選全部取消」與「這張表單沒有參數區塊」，
            // 一張表單只該有一個
            Assert.Equal(1, CountOf(html, "name=\"" + PlantFormEnhancementFilter.PresentField + "\""));

            // 逐個參數：name 與 id 都不得重複（id 重複另有 label for= 指錯的問題）
            foreach (var def in PlantParamCatalog.All)
            {
                Assert.Equal(1, CountOf(html, "name=\"" + def.OverrideField + "\" value=\"true\""));
                Assert.Equal(1, CountOf(html, "id=\"ovr_" + def.Key + "\""));
                Assert.Equal(1, CountOf(html, "id=\"val_" + def.Key + "\""));
            }
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        /// <summary>取出 resx 裡指定鍵的值。</summary>
        private static string? ResxValue(string resxPath, string key) =>
            System.Xml.Linq.XDocument.Load(resxPath).Root!
                .Elements("data")
                .FirstOrDefault(d => (string?)d.Attribute("name") == key)
                ?.Element("value")?.Value;

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;

            throw new DirectoryNotFoundException("找不到 repo 根目錄（.git）");
        }

        /// <summary>
        /// 面板走的是 `body.innerHTML = DOMPurify.sanitize(html)`（`Views/Plant/Index.cshtml`，Core 檔），
        /// **不是** crud-panel.js —— innerHTML 不執行 script、DOMPurify 也會整段移除，
        /// 而 CSP 是 `style-src 'self'`。所以這個區塊一旦混進 script 或行內樣式，
        /// 使用者看到的是「控制項沒反應」或「版面跑掉」，不會有任何錯誤。
        /// </summary>
        [Fact]
        public async Task ParamSection_ContainsNoScriptAndNoInlineStyle()
        {
            var html = await GetPanelAsync();

            Assert.Contains("pp_ovr_", html);
            // 整份 partial 都要乾淨——面板是把這段整個塞進 innerHTML 的
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("style=\"", html, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 三態的讀出端：有覆寫的欄位必須勾起來，沒覆寫的必須維持未勾。
        /// 這一條同時證明 Filter 真的被註冊進管線了——沒註冊時 ViewData 是空的，
        /// 整個區塊會安靜地不渲染（見 `_ProjectZPlantParams.cshtml` 的 null 守門）。
        /// </summary>
        [Fact]
        public async Task PlantEditPanel_ShowsOverriddenFieldsAsChecked()
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<IPlantParamService>();
                await svc.SetAsync(ProjectZSystemParamKeys.MfgLotPolicy, PlantId, "MinMultiple");
                await svc.ClearAsync(ProjectZSystemParamKeys.MfgMaterialGate, PlantId);
            }

            var html = await GetPanelAsync();

            var overridden = Section(html, "pp_ovr_" + ProjectZSystemParamKeys.MfgLotPolicy);
            var inherited  = Section(html, "pp_ovr_" + ProjectZSystemParamKeys.MfgMaterialGate);

            Assert.Contains("checked", overridden);
            Assert.DoesNotContain("checked", inherited);
        }


        // ── 寫入端共用：AntiForgery token 與表單 POST ────────────────────────────
        //   token 直接從面板本身取（`_PlantForm.cshtml` 有 @Html.AntiForgeryToken()），
        //   且必須與 POST 用同一個 client——cookie 與表單 token 是成對驗證的。

        private static async Task<string> HarvestTokenAsync(HttpClient client)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/Plant/Edit/{PlantId}");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(m.Success, "未能從廠別面板取得 AntiForgery token");
            return m.Groups[1].Value;
        }

        /// <summary>照面板實際送出的樣子 POST：Plant 本體欄位 + 參數欄位，AJAX 分支回 Json。</summary>
        private static async Task<HttpResponseMessage> SavePanelAsync(
            HttpClient client, string token, Dictionary<string, string> paramFields)
        {
            var form = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Id"] = PlantId,
                ["Name"] = "整合測試廠",
                // 面板存在標記：沒帶就代表「這張表單沒有參數區塊」，Filter 會整段跳過。
                [PlantFormEnhancementFilter.PresentField] = "1",
            };
            foreach (var kv in paramFields) form[kv.Key] = kv.Value;

            var req = new HttpRequestMessage(HttpMethod.Post, $"/Plant/Edit/{PlantId}")
            {
                Content = new FormUrlEncodedContent(form),
            };
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.IsSuccessStatusCode,
                $"/Plant/Edit 回應非 2xx（{(int)resp.StatusCode}）：{body[..Math.Min(body.Length, 400)]}");
            // Filter 只在「Core 真的存成功」時才寫參數；這裡順便釘住那個前提
            Assert.Contains("\"success\":true", body);
            return resp;
        }

        private async Task<(bool HasOverride, string? Effective)> ReadAsync(string key)
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlantParamService>();
            return (await svc.HasOverrideAsync(key, PlantId), await svc.GetAsync(key, PlantId));
        }

        /// <summary>
        /// 端到端寫入：真的 POST 一次面板，值要落到 PlantParams，且重新開面板讀得回來。
        ///
        /// 單元測試蓋的是 Filter 內部的分支；這一支蓋的是「Filter 有沒有接在這條路上」——
        /// 註冊漏掉、AntiForgery 擋掉、或 Core 改成別的成功回應形狀，都會**安靜地**
        /// 變成「按了儲存但設定沒存進去」，使用者只會看到「廠別更新成功」。
        /// </summary>
        [Fact]
        public async Task SavePanel_PersistsOverrides_EndToEnd()
        {
            var client = _factory.CreateClient();
            var token = await HarvestTokenAsync(client);

            await SavePanelAsync(client, token, new Dictionary<string, string>
            {
                // Bool / Choice / Number / Text 四種型別各壓一個，正規化在單元層已蓋
                ["pp_ovr_" + ProjectZSystemParamKeys.MfgSimplifiedMode]      = "true",
                ["pp_val_" + ProjectZSystemParamKeys.MfgSimplifiedMode]      = "true",
                ["pp_ovr_" + ProjectZSystemParamKeys.MfgMaterialGate]        = "true",
                ["pp_val_" + ProjectZSystemParamKeys.MfgMaterialGate]        = "Block",
                ["pp_ovr_" + ProjectZSystemParamKeys.MfgWoApprovalThreshold] = "true",
                ["pp_val_" + ProjectZSystemParamKeys.MfgWoApprovalThreshold] = "1500",
                ["pp_ovr_" + ProjectZSystemParamKeys.MfgFgWarehouse]         = "true",
                ["pp_val_" + ProjectZSystemParamKeys.MfgFgWarehouse]         = "WH-FG",
            });

            Assert.Equal((true, "true"),   await ReadAsync(ProjectZSystemParamKeys.MfgSimplifiedMode));
            Assert.Equal((true, "Block"),  await ReadAsync(ProjectZSystemParamKeys.MfgMaterialGate));
            Assert.Equal((true, "1500"),   await ReadAsync(ProjectZSystemParamKeys.MfgWoApprovalThreshold));
            Assert.Equal((true, "WH-FG"),  await ReadAsync(ProjectZSystemParamKeys.MfgFgWarehouse));

            // 讀寫要對得起來：重新開面板時勾選與值都得回填
            var html = await GetPanelAsync();
            Assert.Contains("checked", Section(html, "pp_ovr_" + ProjectZSystemParamKeys.MfgMaterialGate));
            Assert.Contains("WH-FG", html);
        }

        /// <summary>
        /// 取消覆寫要真的回到繼承全域——不是寫入空字串。
        /// 這兩者在 `IPlantParamService` 是不同狀態（有列＝覆寫成空 / 無列＝繼承），
        /// 弄混的話全域改值以後該廠會固定卡在空值。
        /// </summary>
        [Fact]
        public async Task SavePanel_Unchecked_ClearsOverride_AndFallsBackToGlobal()
        {
            const string Key = ProjectZSystemParamKeys.MfgLotPolicy;

            using (var scope = _factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ISystemParamService>()
                    .SetAsync(Key, "LotForLot");
                await scope.ServiceProvider.GetRequiredService<IPlantParamService>()
                    .SetAsync(Key, PlantId, "MinMultiple");
            }
            Assert.Equal((true, "MinMultiple"), await ReadAsync(Key));

            var client = _factory.CreateClient();
            var token = await HarvestTokenAsync(client);

            // 面板送的是 checkbox 未勾的樣子：只剩 hidden 的 "false"，值欄位照樣帶著
            await SavePanelAsync(client, token, new Dictionary<string, string>
            {
                ["pp_ovr_" + Key] = "false",
                ["pp_val_" + Key] = "MinMultiple",
            });

            var (hasOverride, effective) = await ReadAsync(Key);
            Assert.False(hasOverride);
            Assert.Equal("LotForLot", effective);   // 覆寫消失後看到的是全域值，不是空值
        }

        /// <summary>
        /// 不帶參數區塊的 POST 不得清掉既有覆寫。
        ///
        /// <para>2026-08-18 用 curl 補手冊截圖資料時實際踩到：剛設好的覆寫，被下一個只改廠名的
        /// 請求全部清光，回應仍是「廠別更新成功」，畫面上毫無徵兆。根因是「沒有勾選」與
        /// 「沒有這個區塊」在 Request.Form 裡長得一模一樣。單元測試釘住了守門邏輯，
        /// 這一支釘的是另一半：面板真的有把存在標記渲染出來——少了它，守門會反過來
        /// 讓正常的取消覆寫也失效。</para>
        /// </summary>
        [Fact]
        public async Task SavePanel_FormWithoutParamSection_DoesNotWipeOverrides()
        {
            // 刻意挑一個**仍在 PlantParamCatalog 裡**的鍵：這支測的是「面板有渲染存在標記」，
            // 用目錄外的鍵會讓斷言失去意義（守門根本不會碰它）。也刻意避開本類別其他測試用的
            // MfgLotPolicy／MfgMaterialGate，免得同一個 collection 內互相干擾。
            const string Key = ProjectZSystemParamKeys.MfgDefaultStagingWh;

            using (var scope = _factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IPlantParamService>()
                    .SetAsync(Key, PlantId, "WH-STG1");
            }

            var client = _factory.CreateClient();
            var token = await HarvestTokenAsync(client);

            // 刻意不帶 pp_present／pp_ovr_*：模擬腳本或精簡表單只改廠名
            var req = new HttpRequestMessage(HttpMethod.Post, $"/Plant/Edit/{PlantId}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["Id"]   = PlantId,
                    ["Name"] = "整合測試廠",
                }),
            };
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var resp = await client.SendAsync(req);
            Assert.True(resp.IsSuccessStatusCode);

            var (hasOverride, effective) = await ReadAsync(Key);
            Assert.True(hasOverride, "只改廠名的 POST 把該廠的覆寫清掉了");
            Assert.Equal("WH-STG1", effective);
        }

        /// <summary>取該 name 的 input 標籤本體（到下一個 '&gt;' 為止），避免整頁比對誤判。</summary>
        private static string Section(string html, string fieldName)
        {
            var i = html.IndexOf($"name=\"{fieldName}\" value=\"true\"", StringComparison.Ordinal);
            Assert.True(i > 0, $"找不到欄位 {fieldName}");
            var open = html.LastIndexOf('<', i);
            var close = html.IndexOf('>', i);
            return html[open..close];
        }

        /// <summary>
        /// 第 3 節 #5（惡意使用者：管理員把廠切到簡化模式、免簽投產）：廠別參數的設定與清除
        /// 都要留下稽核紀錄（誰、何時、前後值），事後追得到。用專屬廠別代號，不碰 P1 的固定裝置。
        /// </summary>
        [Fact]
        public async Task PlantParamChange_IsAudited_ForSetAndClear()
        {
            const string AuditPlant = "ZZAUDIT";
            var start = DateTime.UtcNow.AddSeconds(-1);
            using (var scope = _factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<IPlantParamService>();
                await svc.SetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, AuditPlant, "true");
                await svc.ClearAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, AuditPlant);
            }

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var logs = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.Timestamp >= start && a.EntityType.Contains("PlantParam")
                             && a.Changes != null && a.Changes.Contains(ProjectZSystemParamKeys.MfgSimplifiedMode)
                             && a.Changes.Contains(AuditPlant))
                    .Select(a => a.Action).ToListAsync();
                Assert.Contains(AuditAction.Added, logs);
                Assert.Contains(AuditAction.Deleted, logs);
            }
        }
    }
}
