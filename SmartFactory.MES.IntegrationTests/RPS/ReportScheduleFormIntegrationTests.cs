using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using Microsoft.Extensions.Hosting;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPS
{
    /// <summary>
    /// 報表排程的新增／編輯側面板（人工驗收計畫 Wave R 的 R-3）。
    ///
    /// <para><b>這裡出過三次真缺陷</b>：R-F1（面板全死，09-10）、R-F2（報表下拉印內部識別字）、
    /// R-F3（存檔成功的 toast 文字空白，皆 09-14）。三次都是**畫面層**的事，
    /// 而 <c>ReportScheduleController</c> 的 <c>Create</c>／<c>Edit</c> 在補這一檔之前
    /// 沒有任何測試——三個測試專案 grep <c>ReportScheduleFormVm</c> 只命中慣例守門。</para>
    ///
    /// <para><b>哪一半需要瀏覽器，哪一半不需要</b>：面板是 AJAX GET 回 partial、存檔是
    /// POST 回 JSON，兩端都在 HTTP 層，整合測試打得到。真正需要 DOM 的只有
    /// 「toast 把訊息渲染進 span」那一段——而那一段**已有 jest 覆蓋**
    /// （<c>wwwroot/js/__tests__/crud-panel.test.js</c> 的 <c>toastSuccess</c> 5 支，
    /// 正是 R-F3 修好時補的）。所以本檔補的是伺服器這一端：
    /// 面板回得出表單、驗證擋得住、存檔存得進去、**回應真的帶著那句訊息**。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection，理由見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReportScheduleFormIntegrationTests : IAsyncLifetime
    {
        /// <summary>
        /// 目標工具。刻意挑一支沒有別的測試在動的——測試庫的 <c>MenuPermissions</c>
        /// 跨進程共用，在別人用的鍵上做權限收窄會互相把前提改掉。
        /// </summary>
        private const string TargetTool = "ReturnRate";

        private const string NamePrefix = "ITEST-RPS-FORM-";

        private const string MenuKey = "ReportSchedule";
        private const string RoleId = "1";

        private readonly AuthedScheduleFactory _factory;

        /// <summary>本檔開始前 <c>(RoleId, MenuKey)</c> 那一列的原值；<c>null</c> ＝原本就沒有那一列。</summary>
        private (bool Read, bool Create, bool Update)? _savedPermission;

        public ReportScheduleFormIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();
            await GrantCreateAsync();
        }

        public async Task DisposeAsync()
        {
            await RestorePermissionAsync();
            await CleanupAsync();
        }

        // ── R-F1：面板開得起來 ───────────────────────────────────────────────

        [Fact]
        public async Task R3_新增排程面板回得出一份可以送出的表單()
        {
            // R-F1 的症狀是「面板全死」——點下去什麼都沒有。那在 HTTP 層就是這一支：
            // 端點回不回得出一份**含 antiforgery token 與必填欄位**的表單。
            using var client = Client();

            var html = await GetFormAsync(client);

            Assert.Contains(@"name=""Name""", html);
            Assert.Contains(@"name=""ToolKey""", html);
            Assert.Contains(@"name=""ScheduleKind""", html);

            // 沒有 token 的表單送出去一律 400，而畫面上看起來是「按了存檔沒反應」。
            Assert.Contains("__RequestVerificationToken", html);

            // 每個必填欄位都要有 validation span，否則欄位級錯誤只會閃一下不顯示。
            Assert.Contains(@"data-valmsg-for=""Name""", html);
            Assert.Contains(@"data-valmsg-for=""ToolKey""", html);

            // 面板收到的必須是**片段**，不是整頁。回整頁的話 HTTP 還是 200、欄位也都在，
            // 但那一整份（含 <html>、導覽列、layout 的 script）會被塞進 offcanvas 裡——
            // 畫面上是爆掉的版面，而不是任何一種錯誤。這一條是思想實驗補的：
            // 先問「把 FormView 改成不分流、一律回整頁，會紅哪一支」，答案是沒有。
            Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<body", html, StringComparison.OrdinalIgnoreCase);
        }

        // ── R-F2：下拉印的是在地化名稱，不是內部識別字 ────────────────────────

        [Theory]
        [InlineData("zh-TW", "退貨率分析")]
        [InlineData("en-US", "Return Rate")]
        public async Task R3_報表下拉印在地化名稱而不是內部識別字(string culture, string expected)
        {
            // R-F2：先前印的是 @tool.ToolKey，於是英文介面下看到的是 "ReturnRate" 這種內部識別字。
            // 兩種語言各問一次才驗得到「會跟著切」——只驗中文的話，
            // 把顯示名寫死成中文常數同樣會綠。
            using var client = Client();

            var html = await GetFormAsync(client, culture);
            var option = OptionOf(html, TargetTool);

            Assert.Equal(expected, option);
            Assert.NotEqual(TargetTool, option);
        }

        [Fact]
        public async Task R3_下拉的提示文字帶入的是實際張數不是寫死的數字()
        {
            // R-F2 的另一半：業主看到「只有 6 張」時，畫面上那句提示寫的是
            // 「僅列出您目前有權限執行的報表」——**把「系統只有這些」說成了「你只看得到這些」**。
            // 修法是改講明「目前有 N 張已接入排程」，N 動態帶入。
            // 寫死的數字會過期而且過期時沒有訊號，所以這裡釘的是「N 等於下拉實際的張數」。
            using var client = Client();

            var html = await GetFormAsync(client);
            var optionCount = ToolOptionCount(html);

            Assert.True(optionCount > 0, "下拉一張報表都沒有，提示文字的張數就無從比對。");
            Assert.Contains($" {optionCount} ", HintTextOf(html));
        }

        // ── 驗證擋得住 ───────────────────────────────────────────────────────

        [Fact]
        public async Task R3_沒填名稱時擋下來_而且回的是看得到欄位級錯誤的表單()
        {
            using var client = Client();
            var token = TokenOf(await GetFormAsync(client));

            var res = await PostAsync(client, token, name: "", toolKey: TargetTool);

            // 驗證失敗回的是 partial（200 + HTML），不是 JSON——crud-panel.js 據此判斷
            // 「要把回應塞回面板」而不是「關掉面板彈 toast」。回 400 的話面板會靜靜地不動。
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var html = await res.Content.ReadAsStringAsync();

            Assert.Contains(@"data-valmsg-for=""Name""", html);
            Assert.Contains("field-validation-error", html);

            await AssertNoneSavedAsync();
        }

        [Fact]
        public async Task R3_同一人不得建兩筆同名排程()
        {
            // 唯一鍵是 (OwnerUserId, Name)。不先擋在這裡的話，存檔時吃到的是一個 23505，
            // 畫面上看到的是 500 而不是「這個名字用過了」。
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var first = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool);
            Assert.True(await IsJsonOkAsync(first));

            var second = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool);

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Contains("field-validation-error", await second.Content.ReadAsStringAsync());
            Assert.Equal(1, await CountAsync(name));
        }

        // ── 存檔成功：清單看得到，而且回應帶著訊息 ─────────────────────────────

        [Fact]
        public async Task R3_存檔成功後清單真的看得到新那一列()
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool);
            Assert.True(await IsJsonOkAsync(res));

            // 存進去了不等於列得出來：Index 只列 ScheduleKind != null 的那些，
            // 而表單若沒把週期寫進去，這一筆會變成「常用查詢」而從排程清單上消失。
            var listed = await client.GetStringAsync(
                $"/ReportSchedule?searched=true&searchTerm={Uri.EscapeDataString(name)}");

            // ⚠️ 不可以只 Assert.Contains(name, listed)：searchTerm 會**回顯在搜尋框裡**，
            // 於是那個斷言恆為真。突變驗證撞出來的——把 ScheduleKind 寫成 null
            // （那一筆就成了常用查詢，排程清單再也列不到它），這一支照樣綠。
            // 要問的是「表格裡有沒有那一列」，所以定位到列本身。
            Assert.Contains($@"<td class=""fw-medium"">{name}</td>", listed);

            // 資料那一半分開斷言：列得出來是因為它**真的存成排程**，不是別的巧合。
            Assert.NotNull(await ScheduleKindOfAsync(name));
        }

        [Fact]
        public async Task R3_存檔成功的回應要帶著訊息文字()
        {
            // R-F3 的伺服器端那一半。JS 端（toastSuccess 把字串寫進 span）已有 jest 覆蓋，
            // 但那一側再對，伺服器回一個空訊息的話畫面上照樣是「勾勾在、文字是空的」。
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            var message = doc.RootElement.GetProperty("message").GetString();
            Assert.False(string.IsNullOrWhiteSpace(message),
                "存檔成功但回應沒有訊息文字——畫面上會是一個空白的成功提示。");
        }

        // ── 參數：從畫面建立的排程，真的跑得出資料 ────────────────────────────
        //
        // 2026-09-17 回報：排程執行紀錄顯示「成功、0 筆」。根因有三層，而三層都只在
        // **從畫面建立**的排程上才湊得齊：
        //   ① 表單沒有任何參數欄位，ParamsJson 一律寫死 "{}"（違反規格 §5.2.1
        //      「期間必須存 RelativePeriod」）；
        //   ② ReportToolRunner 只注入 TimeZoneId，不注入 PlantId；
        //   ③ 十六支工具把 request.PlantId ?? "" 拿去字面比對，而契約寫的是
        //      「null ＝ 依執行 scope 的 CurrentPlantId」。
        // 於是吃期間的十三支直接 Failed，不吃期間的四支安靜產出零筆。
        //
        // ⚠️ 補 RunNow 那批測試沒抓到，是因為它們**直接在 DB 種 definition** 並自己
        // 序列化了帶 PlantId 與 Period 的 ParamsJson——種了一筆畫面上建不出來的排程。
        // 整條鏈驗過了，驗的卻是一個不存在的前提。所以這一支刻意**全程走畫面**。

        [Fact]
        public async Task R3_從畫面建立的排程_執行後真的有資料而不是零筆()
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                      period: "Last30Days");
            Assert.True(await IsJsonOkAsync(res));

            // 存進去的參數要帶期間——這是「畫面建得出一筆跑得動的排程」的前半句。
            var definitionId = await IdOfAsync(name);
            Assert.Contains("\"period\"", await ParamsJsonOfAsync(definitionId));

            // ⚠️ 資料要種在**這筆排程自己的廠別**，而那個廠別只能從排程上讀。
            // 本來是先種資料、廠別取自 scope 裡的 IUserContext——那裡沒有 HTTP 上下文，
            // PlantId 是 null，於是種到一個沒有廠別的角落，排程當然查不到。
            // 這正是 ReportDispatcherService 註解在講的同一個陷阱：
            // 背景 scope 注入 IUserContext 不會拋例外，只會靜默給空值。
            var plantId = await PlantIdOfAsync(definitionId);
            Assert.False(string.IsNullOrEmpty(plantId), "排程沒有記下自己的廠別，後面的斷言就沒有意義。");

            await SeedShipmentAsync(plantId!);

            await MakeDueAsync(definitionId);
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);

            Assert.Equal(ReportRunStatus.Success, run.Status);
            Assert.True(run.RowCount > 0,
                $"排程跑完是「成功」但 {run.RowCount} 筆——這正是回報的症狀。" +
                "資料就種在這個排程自己的廠別裡，查不到代表廠別沒有傳到工具手上。");
        }

        [Fact]
        public async Task R3_吃期間的報表沒選期間時_存檔就要擋下來()
        {
            // 不擋在存檔這一刻的話，錯誤要等到下一次派工才出現——而使用者這邊看到的是
            // 「存檔成功」。錯誤必須落在按下按鈕的那個人面前。
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                      period: null);

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("field-validation-error", await res.Content.ReadAsStringAsync());
            Assert.Equal(0, await CountAsync(name));
        }

        [Fact]
        public async Task R3_不吃期間的報表_沒有期間也存得下去()
        {
            // 庫存評估表看的是當下快照。逼它選一個永遠不生效的期間，
            // 使用者會合理地以為那個期間有意義。
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)),
                                      name, "InventoryValuation", period: null);

            Assert.True(await IsJsonOkAsync(res), "不吃期間的報表不該被期間必填擋下來。");
        }

        [Theory]
        [InlineData("ReturnRate", true)]
        [InlineData("InventoryValuation", false)]
        public async Task R3_期間欄位只對吃期間的報表顯示(string toolKey, bool expected)
        {
            // 顯示與否由工具自己宣告的 RequiresPeriod 決定，印在 option 上給 JS 用。
            // 兩處各維護一份名單的結果一定是有一天對不上，而症狀是某張報表的排程
            // 存得下去卻跑不出來。
            using var client = Client();
            var html = await GetFormAsync(client);

            var option = Regex.Match(html,
                @"<option value=""" + Regex.Escape(toolKey) + @"""[^>]*data-requires-period=""(true|false)""");

            Assert.True(option.Success, $"報表下拉上的 {toolKey} 沒有宣告要不要期間。");
            Assert.Equal(expected.ToString().ToLowerInvariant(), option.Groups[1].Value);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 開新增面板要的是 <c>CanCreate</c>，而測試庫裡那一列通常只有 <c>CanRead</c>——
        /// <c>ReportPermissionIntegrationTests</c> 的 <c>GrantAllAsync</c> 只設讀取，
        /// 其餘動詞留 false。**先記下原值再放寬**：這個庫跨進程共用，
        /// 留一個被本檔改過的權限給別人，症狀是那一輪的數字看起來正常但少了一整頁。
        /// </summary>
        private async Task GrantCreateAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var perm = await db.MenuPermissions
                .FirstOrDefaultAsync(p => p.RoleId == RoleId && p.MenuKey == MenuKey);

            _savedPermission = perm is null ? null : (perm.CanRead, perm.CanCreate, perm.CanUpdate);

            if (perm is null)
            {
                perm = new MenuPermission { RoleId = RoleId, MenuKey = MenuKey };
                db.MenuPermissions.Add(perm);
            }

            perm.CanRead = perm.CanCreate = perm.CanUpdate = true;
            await db.SaveChangesAsync();
        }

        private async Task RestorePermissionAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var perm = await db.MenuPermissions
                .FirstOrDefaultAsync(p => p.RoleId == RoleId && p.MenuKey == MenuKey);
            if (perm is null) return;

            if (_savedPermission is not { } saved)
            {
                db.MenuPermissions.Remove(perm);   // 本檔之前沒有這一列，不留下來
            }
            else
            {
                (perm.CanRead, perm.CanCreate, perm.CanUpdate) = saved;
            }

            await db.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════════════════
        // R-5：畫面填的週期規則，真的變成那個規則
        //
        // 🔴 這一組補的是 2026-09-17 盤點出來的缺口：在此之前，本檔的 POST 確實帶了
        //    ScheduleHour／ScheduleDayOfWeek／ScheduleDayOfMonth，但**一支都沒有讀回來
        //    斷言**——它們只是湊齊必填欄位的填充值。另一端的
        //    ReportScheduleCalculatorTests 驗的是「給定規則算得出下一次」。
        //    於是中間這一段「畫面填的值有沒有變成那個規則」，兩邊都沒有碰到。
        //
        // ⚠️ R-F7 正是這個形狀的缺陷（期間欄位根本沒被寫進 ParamsJson），
        //    而它是使用者回報的、不是測試抓到的。同一個形狀不該再發生第二次。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 每一欄都刻意<b>不用預設值</b>：8 點 0 分、每日、Csv、啟用都是 VM 的預設，
        /// 用預設值去斷言的話，「表單根本沒被讀」與「讀了而且對」會長得一模一樣。
        /// </summary>
        [Fact]
        public async Task R5_畫面填的週期規則_一字不差存進資料庫()
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                      period: "Last30Days",
                                      kind: "Monthly", hour: 21, minute: 45, dayOfMonth: 31,
                                      outputFormat: "Excel", isActive: false);
            Assert.True(await IsJsonOkAsync(res));

            var def = await DefinitionOfAsync(await IdOfAsync(name));

            Assert.Equal(ReportScheduleKind.Monthly, def.ScheduleKind);
            Assert.Equal(21, def.ScheduleHour);
            Assert.Equal(45, def.ScheduleMinute);
            Assert.Equal(31, def.ScheduleDayOfMonth);
            Assert.Equal("Excel", def.OutputFormat);
            Assert.False(def.IsActive);
        }

        /// <summary>
        /// 只寫該週期用得到的欄位，另一種設 <c>null</c>。留著舊值的話，從每週改成每月之後
        /// 畫面仍會顯示「星期一」——看起來像設定沒生效，而資料其實是對的。
        /// </summary>
        [Theory]
        [InlineData("Monthly", null, 31)]
        [InlineData("Weekly",  4,    null)]
        public async Task R5_每月排程不留星期幾_每週排程不留幾號(
            string kind, int? expectedDayOfWeek, int? expectedDayOfMonth)
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            // 兩個欄位都送出去——瀏覽器送的就是整份表單，收起來的那一列照樣會送值。
            var res = await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                      kind: kind, dayOfWeek: 4, dayOfMonth: 31);
            Assert.True(await IsJsonOkAsync(res));

            var def = await DefinitionOfAsync(await IdOfAsync(name));

            Assert.Equal(expectedDayOfWeek,  def.ScheduleDayOfWeek);
            Assert.Equal(expectedDayOfMonth, def.ScheduleDayOfMonth);
        }

        /// <summary>
        /// 往返的另一半：存進去之後**重新開啟編輯面板**，填過的值要原樣回到欄位上。
        /// 這一半壞掉的症狀特別安靜——使用者開編輯面板看到的是預設值（8 點、星期一），
        /// 以為設定沒存成功，改一改再按存檔，於是把原本正確的設定覆蓋掉。
        /// </summary>
        [Fact]
        public async Task R5_重新開啟編輯面板_填過的值要原樣回到欄位上()
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            Assert.True(await IsJsonOkAsync(
                await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                kind: "Weekly", hour: 21, minute: 45, dayOfWeek: 4,
                                outputFormat: "Excel")));

            var html = await GetEditFormAsync(client, await IdOfAsync(name));

            Assert.Equal("21", InputValueOf(html, "ScheduleHour"));
            Assert.Equal("45", InputValueOf(html, "ScheduleMinute"));
            Assert.Equal("Weekly", SelectedValueOf(html, "ScheduleKind"));
            Assert.Equal("4",      SelectedValueOf(html, "ScheduleDayOfWeek"));
            Assert.Equal("Excel",  SelectedValueOf(html, "OutputFormat"));

            // 期間也是往返的一部分（R-F7 修好的那一欄）。
            Assert.Equal("Last30Days", SelectedValueOf(html, "Period"));

            // 該顯示的那一列要是展開的，另一種要收起來——不然值對了也看不到。
            Assert.False(RowIsHidden(html, "rowDayOfWeek"));
            Assert.True(RowIsHidden(html, "rowDayOfMonth"));
        }

        /// <summary>
        /// 改變週期之後，畫面不得還顯示著另一種的設定。這一支同時驗資料與畫面兩端：
        /// 資料端 <c>ScheduleDayOfMonth</c> 要被清成 <c>null</c>，畫面端那一列要收起來。
        /// </summary>
        [Fact]
        public async Task R5_把每月改成每週之後_幾號要一起消失()
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            Assert.True(await IsJsonOkAsync(
                await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                kind: "Monthly", dayOfMonth: 31)));

            var id = await IdOfAsync(name);
            Assert.Equal(31, (await DefinitionOfAsync(id)).ScheduleDayOfMonth);

            // token 取自編輯面板自己那一份。
            Assert.True(await IsJsonOkAsync(
                await PostEditAsync(client, TokenOf(await GetEditFormAsync(client, id)), id, name,
                                    TargetTool, kind: "Weekly", dayOfWeek: 4, dayOfMonth: 31)));

            var def = await DefinitionOfAsync(id);
            Assert.Equal(ReportScheduleKind.Weekly, def.ScheduleKind);
            Assert.Equal(4, def.ScheduleDayOfWeek);
            Assert.Null(def.ScheduleDayOfMonth);

            var html = await GetEditFormAsync(client, id);
            Assert.True(RowIsHidden(html, "rowDayOfMonth"));
            Assert.False(RowIsHidden(html, "rowDayOfWeek"));
        }

        /// <summary>
        /// 規則真的走到<b>下一次執行時刻</b>。前面幾支驗的是「值進了資料庫」，
        /// 這一支驗的是那些值**被用上了**——`NextRunAt` 由 `ApplyAsync` 當場重算，
        /// 沒重算的話改了時間仍會照舊時刻跑一次（而且畫面上完全看不出來）。
        ///
        /// <para>斷言挑的是「換算回這筆排程自己的時區之後，時、分要等於畫面填的值」。
        /// ⚠️ 時區來源與正式路徑同樣是 <c>ReportScheduleTimeZoneResolver</c>，
        /// 所以**解析順序本身這一支驗不到**——那是 TZ-2 的範圍（已有 6 支單元測試）。
        /// 這裡驗的是時與分，它們只可能來自表單。</para>
        /// </summary>
        [Fact]
        public async Task R5_下次執行時刻_落在畫面填的那個時刻上()
        {
            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            Assert.True(await IsJsonOkAsync(
                await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                kind: "Monthly", hour: 21, minute: 45, dayOfMonth: 31)));

            var def = await DefinitionOfAsync(await IdOfAsync(name));
            Assert.NotNull(def.NextRunAt);
            Assert.True(def.NextRunAt > DateTime.UtcNow, "下次執行時刻排在過去，等於這筆排程一存好就已經逾期。");

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // 解不出來時退到計算器自己的 fallback——與正式路徑同一個值，
            // 不在這裡另外寫死一個常數（寫死的話 fallback 一改，這支會以錯的理由紅）。
            var tz = await ReportScheduleTimeZoneResolver.ResolveAsync(db, def)
                     ?? ReportScheduleCalculator.FallbackTimeZoneId;
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(def.NextRunAt!.Value, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(tz));

            Assert.Equal(21, local.Hour);
            Assert.Equal(45, local.Minute);

            // 選 31 號的月份沒有 31 日時取當月最後一日（不是跳過該月）——
            // 這條規則由 ReportScheduleCalculatorTests 釘死，這裡確認它**被套用到了**。
            Assert.True(local.Day == 31 || local.Day == DateTime.DaysInMonth(local.Year, local.Month),
                        $"每月 31 日的排程落在 {local:yyyy-MM-dd}，既不是 31 號也不是當月最後一日。");
        }

        /// <summary>
        /// <b>存檔路徑真的把時區解析結果傳下去了。</b>（TZ-2／§11 #10 的回歸守門）
        ///
        /// <para><b>這條規則本來沒有守門，而且是實測確認的</b>：2026-09-24 把
        /// <c>ReportScheduleController.ApplyAsync</c> 裡的
        /// <c>ComputeNextRunUtc(definition, DateTime.UtcNow, timeZoneId)</c>
        /// 改回 <c>timeZoneId: null</c>——也就是 §11 #10 當初修掉的那個缺陷本身——
        /// 本檔 19 支<b>全部照樣通過</b>。`ReportScheduleTimeZoneResolverTests` 那 6 支
        /// 驗的是解析器本身，沒有人驗<b>呼叫端有沒有呼叫它</b>。</para>
        ///
        /// <para>上面那支 `R5_下次執行時刻_落在畫面填的那個時刻上` 看似涵蓋得到，
        /// 其實不行：它用<b>同一個解析器</b>把 `NextRunAt` 換算回去，而這個固定裝置的
        /// 排程沒有 `PlantId`、`admin` 也沒有個人時區，解析器於是回 null、兩邊都退到
        /// 同一個 fallback——**錯的實作與對的實作算出完全相同的值**。</para>
        ///
        /// <para><b>作法</b>：給 `admin` 一個遠時區（UTC+14）再存檔，於是解析器的第二順位
        /// （建立者個人時區）與 fallback（Asia/Taipei，UTC+8）**相差 6 小時**，
        /// 傳不傳時區算出來的 UTC 時刻就不可能相同。斷言刻意比對<b>絕對的 UTC 時刻</b>
        /// 而不是換算回當地——換算回去就又會被「用同一個解析器」這件事抵銷掉。</para>
        ///
        /// <para><b>廠別時區要一併清空</b>：解析器第一順位是 <c>Plant.DefaultTimeZone</c>，
        /// 而存檔時 <c>PlantId</c> 取自 admin 的目前廠別（P1）。乾淨庫的 P1 由 Program.cs
        /// 種成 <c>Asia/Taipei</c>——剛好等於 fallback，於是解析器在第一層就回台北，
        /// 個人時區永遠輪不到，CI 必紅（2026-10-01）。本機殘留庫的 P1 時區是 null 才一直綠。</para>
        ///
        /// <para>⚠️ 改完一定要還原：測試庫跨進程共用，留一個帶時區的 `admin`
        /// 會讓別的量測以為自己在測時區。</para>
        /// </summary>
        [Fact]
        public async Task TZ2_存檔時的下次執行時刻要走時區解析器_不得退回預設時區()
        {
            const string farEast = "Pacific/Kiritimati";   // UTC+14，與 fallback 的 UTC+8 差 6 小時
            const int hour = 9, minute = 0;

            using var client = Client();
            var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];

            var original = await SetAdminTimeZoneAsync(farEast);
            var (plantId, originalPlantTz) = await SetAdminPlantTimeZoneAsync(null);
            try
            {
                Assert.True(await IsJsonOkAsync(
                    await PostAsync(client, TokenOf(await GetFormAsync(client)), name, TargetTool,
                                    kind: "Daily", hour: hour, minute: minute)));

                var def = await DefinitionOfAsync(await IdOfAsync(name));
                Assert.NotNull(def.NextRunAt);

                // 期望值由產品程式自己算，不在測試裡手寫時差——
                // 寫死 6 小時的話，fallback 或時區資料一變，這支會以錯的理由紅。
                var expected = ReportScheduleCalculator.ComputeNextRunUtc(
                    def, DateTime.UtcNow, farEast);
                var wrong = ReportScheduleCalculator.ComputeNextRunUtc(
                    def, DateTime.UtcNow, ReportScheduleCalculator.FallbackTimeZoneId);

                // 前提檢查：兩者必須不同，否則這支測試沒有鑑別力而且不會有人發現。
                Assert.True(expected != wrong,
                    $"固定裝置失去鑑別力：{farEast} 與 fallback 算出同一個時刻 {expected:o}。");

                Assert.Equal(expected, def.NextRunAt);
            }
            finally
            {
                await SetAdminTimeZoneAsync(original);
                await SetPlantTimeZoneAsync(plantId, originalPlantTz);
            }
        }

        /// <summary>設定 admin 目前廠別的預設時區，回傳廠別代碼與原值以供還原。</summary>
        private async Task<(string? PlantId, string? Previous)> SetAdminPlantTimeZoneAsync(string? timeZoneId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plantId = await db.Users.Where(u => u.UserName == "admin")
                .Select(u => u.ActivePlantId).FirstAsync();
            if (string.IsNullOrEmpty(plantId)) return (null, null);
            return (plantId, await SetPlantTimeZoneAsync(plantId, timeZoneId));
        }

        private async Task<string?> SetPlantTimeZoneAsync(string? plantId, string? timeZoneId)
        {
            if (string.IsNullOrEmpty(plantId)) return null;
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plant = await db.Plants.FirstAsync(p => p.Id == plantId);
            var previous = plant.DefaultTimeZone;
            plant.DefaultTimeZone = timeZoneId;
            await db.SaveChangesAsync();
            return previous;
        }

        /// <summary>設定 admin 的個人時區，回傳原值以供還原。</summary>
        private async Task<string?> SetAdminTimeZoneAsync(string? timeZoneId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var admin = await db.Users.FirstAsync(u => u.UserName == "admin");
            var previous = admin.TimeZoneId;
            admin.TimeZoneId = timeZoneId;
            await db.SaveChangesAsync();
            return previous;
        }

        // ── R-5 用的 helper ──────────────────────────────────────────────────

        /// <summary>取<b>編輯</b>面板的 HTML（與新增面板是兩份不同的回應，token 也不同）。</summary>
        private static async Task<string> GetEditFormAsync(HttpClient client, int id)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/ReportSchedule/Edit/{id}");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// 取某個 <c>&lt;input&gt;</c> 的 <c>value</c>。
        /// ⚠️ 不可以只搜 <c>value="21"</c>——那個字串在別的欄位（例如收件人下拉的某個
        /// 使用者 Id、或另一個數字欄位）也可能出現，斷言會以錯的理由通過。
        /// 這裡先定位到 <c>name="X"</c> 所在的那個標籤，再從標籤內取值。
        /// </summary>
        private static string? InputValueOf(string html, string name)
        {
            var m = Regex.Match(html, @"<input\b[^>]*\bname=""" + Regex.Escape(name) + @"""[^>]*>");
            if (!m.Success) return null;
            var v = Regex.Match(m.Value, @"\bvalue=""([^""]*)""");
            return v.Success ? WebUtility.HtmlDecode(v.Groups[1].Value) : null;
        }

        /// <summary>
        /// 取某個 <c>&lt;select&gt;</c> 目前選中的值。找不到選中項回 <c>null</c>——
        /// 「沒有任何選項被選中」與「選中了錯的那個」是兩種不同的壞法，不可混成同一個斷言。
        /// </summary>
        private static string? SelectedValueOf(string html, string name)
        {
            var start = html.IndexOf($@"name=""{name}""", StringComparison.Ordinal);
            if (start < 0) return null;
            var end = html.IndexOf("</select>", start, StringComparison.Ordinal);
            if (end < 0) return null;

            foreach (Match opt in Regex.Matches(html[start..end], @"<option\b[^>]*>"))
            {
                if (!opt.Value.Contains("selected", StringComparison.OrdinalIgnoreCase)) continue;
                return Regex.Match(opt.Value, @"\bvalue=""([^""]*)""").Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// 那一列在畫面上是不是收起來的。R14 規定顯示／隱藏一律切 <c>u-display-none</c>，
        /// 所以判斷的依據就是那個 class 在不在該列的標籤上。
        /// </summary>
        private static bool RowIsHidden(string html, string rowId)
        {
            var m = Regex.Match(html, @"<div\b[^>]*\bid=""" + Regex.Escape(rowId) + @"""[^>]*>");
            Assert.True(m.Success, $"表單裡找不到 {rowId} 這一列。");

            // ⚠️ 比對的是**整個標籤**，不是 id 後面那一段：這一列的 class 寫在 id 前面
            // （而且中間隔著換行），只往 id 之後找會永遠判成「沒隱藏」——恆綠的斷言。
            return m.Value.Contains("u-display-none", StringComparison.Ordinal);
        }

        private async Task<ReportDefinition> DefinitionOfAsync(int id)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportDefinitions.AsNoTracking().FirstAsync(d => d.Id == id);
        }

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        /// <summary>
        /// 取新增面板的 HTML。帶 <c>X-Requested-With</c> 走的才是使用者實際看到的那條
        /// （<c>FormView</c> 依它分流 PartialView／View）。
        /// </summary>
        private static async Task<string> GetFormAsync(HttpClient client, string? culture = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/ReportSchedule/Create");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (culture is not null)
                req.Headers.Add("Cookie",
                    ".AspNetCore.Culture=" + Uri.EscapeDataString($"c={culture}|uic={culture}"));

            var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }

        private static string TokenOf(string html) =>
            Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

        /// <summary>
        /// 取某個工具的下拉顯示文字。⚠️ 要 <c>HtmlDecode</c>：Razor 預設把非 ASCII 編成
        /// <c>&amp;#x9000;</c> 這種數值參照，直接拿字串比對中文會以錯的理由紅。
        /// </summary>
        private static string OptionOf(string html, string toolKey) =>
            WebUtility.HtmlDecode(
                Regex.Match(html, @"<option value=""" + Regex.Escape(toolKey) + @"""[^>]*>([^<]*)</option>")
                     .Groups[1].Value);

        /// <summary>
        /// 數 <b>報表下拉自己</b>的選項。⚠️ 整份表單裡不只這一個 select——收件人下拉有
        /// 一百多個使用者，從頭數 <c>&lt;option&gt;</c> 會數到 174 而不是 17，
        /// 而那個數字看起來同樣「是動態算出來的」。
        /// </summary>
        private static int ToolOptionCount(string html)
        {
            var start = html.IndexOf(@"id=""ToolKey""", StringComparison.Ordinal);
            Assert.True(start >= 0, "表單裡找不到報表下拉。");

            var end = html.IndexOf("</select>", start, StringComparison.Ordinal);
            Assert.True(end > start, "報表下拉沒有收尾。");

            // 第一個 <option value=""> 是 select2 的 placeholder，不算一張報表。
            return Regex.Matches(html[start..end], @"<option value=""[^""]+""[^>]*>").Count;
        }

        /// <summary>
        /// 取報表下拉底下那句提示。**從 ToolKey 的 select 往後找第一個 form-text**——
        /// 表單裡不只一個 <c>form-text</c>（週期那一區也有），從頭抓會抓到別人的。
        /// </summary>
        private static string HintTextOf(string html)
        {
            var afterSelect = html[(html.IndexOf(@"id=""ToolKey""", StringComparison.Ordinal) is var i and >= 0
                ? i : 0)..];
            return WebUtility.HtmlDecode(
                Regex.Match(afterSelect, @"<div class=""form-text text-muted"">([^<]*)</div>")
                     .Groups[1].Value);
        }

        private static Task<HttpResponseMessage> PostAsync(
            HttpClient client, string token, string name, string toolKey,
            string? period = "Last30Days",
            string kind = "Daily", int hour = 8, int minute = 0,
            int dayOfWeek = 1, int dayOfMonth = 1,
            string outputFormat = "Csv", bool isActive = true)
            => client.PostAsync("/ReportSchedule/Create",
                   Form(token, id: 0, name, toolKey, period, kind, hour, minute,
                        dayOfWeek, dayOfMonth, outputFormat, isActive));

        /// <summary>
        /// 送出編輯。⚠️ token 要取自<b>編輯</b>面板而不是新增面板——兩份面板各自帶一個
        /// antiforgery token，拿錯那一份會 400，而 400 與「驗證擋下來」在斷言上長得很像。
        /// </summary>
        private static Task<HttpResponseMessage> PostEditAsync(
            HttpClient client, string token, int id, string name, string toolKey,
            string? period = "Last30Days",
            string kind = "Daily", int hour = 8, int minute = 0,
            int dayOfWeek = 1, int dayOfMonth = 1,
            string outputFormat = "Csv", bool isActive = true)
            => client.PostAsync($"/ReportSchedule/Edit/{id}",
                   Form(token, id, name, toolKey, period, kind, hour, minute,
                        dayOfWeek, dayOfMonth, outputFormat, isActive));

        private static FormUrlEncodedContent Form(
            string token, int id, string name, string toolKey, string? period,
            string kind, int hour, int minute, int dayOfWeek, int dayOfMonth,
            string outputFormat, bool isActive)
        {
            var form = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Id"] = id.ToString(),
                ["Name"] = name,
                ["ToolKey"] = toolKey,
                ["ScheduleKind"] = kind,
                ["ScheduleHour"] = hour.ToString(),
                ["ScheduleMinute"] = minute.ToString(),
                ["ScheduleDayOfWeek"] = dayOfWeek.ToString(),
                ["ScheduleDayOfMonth"] = dayOfMonth.ToString(),
                ["OutputFormat"] = outputFormat,
                ["IsActive"] = isActive ? "true" : "false",
            };

            // null ＝ 不送這個欄位（不吃期間的報表，畫面上那一列是收起來的）。
            if (period is not null) form["Period"] = period;

            return new FormUrlEncodedContent(form);
        }

        private static async Task<bool> IsJsonOkAsync(HttpResponseMessage res)
        {
            if (res.Content.Headers.ContentType?.MediaType != "application/json") return false;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean();
        }

        /// <summary>
        /// 取那一筆的排程週期。<c>null</c> ＝存成了常用查詢（S-2）而不是排程（S-1）——
        /// 兩者是同一張表的兩種狀態，差別只在這一欄。
        /// </summary>
        private async Task<SmartFactory.MES.Modules.RPS.Models.ReportScheduleKind?> ScheduleKindOfAsync(string name)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportDefinitions.Where(d => d.Name == name)
                .Select(d => d.ScheduleKind).FirstOrDefaultAsync();
        }

        /// <summary>
        /// 在**指定廠別**種一筆已過帳的出貨單。刻意不用專屬測試廠：這一支要驗的正是
        /// 「畫面建立的排程會不會把自己的廠別交給工具」，所以資料必須落在那筆排程的廠裡。
        /// </summary>
        private async Task SeedShipmentAsync(string plantId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var adminId = (await db.Users.AsNoTracking().FirstAsync(u => u.UserName == "admin")).Id;

            var shipmentId = NamePrefix + Guid.NewGuid().ToString("N")[..8];
            db.GoodsShipments.Add(new GoodsShipment
            {
                GoodsShipmentId = shipmentId,
                ShipDate        = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                TypeCode        = "GS",
                CompanyCode     = "ITEST-FORM-C1",
                Status          = DocStatus.Posted,
                PlantId         = plantId,
                CreatedBy       = adminId,
                CreatedByRoleId = RoleId,   // 少了它會被 IDataAuthEntity 的全域過濾隱藏
            });
            db.GoodsShipmentLines.Add(new GoodsShipmentLine
            {
                GoodsShipmentId = shipmentId,
                LineNo          = 1,
                ProductCode     = "ITEST-FORM-P1",
                ShipQty         = 10,
            });

            await db.SaveChangesAsync();
        }

        private async Task<int> IdOfAsync(string name)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportDefinitions.Where(d => d.Name == name).Select(d => d.Id).FirstAsync();
        }

        private async Task<string> ParamsJsonOfAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportDefinitions.Where(d => d.Id == definitionId)
                .Select(d => d.ParamsJson).FirstAsync();
        }

        private async Task<string?> PlantIdOfAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportDefinitions.Where(d => d.Id == definitionId)
                .Select(d => d.PlantId).FirstAsync();
        }

        /// <summary>把排程拉到期，好讓派工器這一輪就撈得到它。</summary>
        private async Task MakeDueAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var def = await db.ReportDefinitions.FirstAsync(d => d.Id == definitionId);
            def.NextRunAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        private async Task ScanOnceAsync() =>
            await _factory.Services.GetServices<IHostedService>()
                .OfType<ReportDispatcherService>().Single()
                .ScanOnceAsync(default);

        /// <summary>只斷言自己那一筆：派工器會撈全部到期排程，數全表會飄。</summary>
        private async Task<ReportRun> SingleRunAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportRuns.AsNoTracking()
                .Where(r => r.DefinitionId == definitionId).SingleAsync();
        }

        private async Task<int> CountAsync(string name)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.ReportDefinitions.CountAsync(d => d.Name == name);
        }

        /// <summary>驗證失敗那幾支用：擋下來了就不該留下任何一筆。</summary>
        private async Task AssertNoneSavedAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(0, await db.ReportDefinitions.CountAsync(d => d.Name.StartsWith(NamePrefix)));
        }

        /// <summary>
        /// 清掉本檔前幾輪的殘留（測試庫是重跑庫，跨進程共用）。
        /// 收件人列要一起刪：它掛在 definitionId 上，只刪定義會留下指向不存在排程的列。
        /// </summary>
        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // ⚠️ 出貨單是 IPlantEntity ＋ IDataAuthEntity：清理這一端**也受全域過濾管轄**，
            // 而這個 scope 沒有 HTTP 上下文，CurrentPlantId 設不出正確的值
            // （設成 null 的話過濾會變成 PlantId IS NULL，RemoveRange 收到空集合、
            // SaveChanges 成功、測試全綠——資料卻留在共用測試庫裡）。
            // 清理不是業務查詢，明確跳過過濾才是誠實的做法：要刪的那幾筆由前綴唯一決定。
            var lines = await db.GoodsShipmentLines.IgnoreQueryFilters()
                .Where(l => l.GoodsShipmentId.StartsWith(NamePrefix)).ToListAsync();
            if (lines.Count > 0) db.GoodsShipmentLines.RemoveRange(lines);

            var shipments = await db.GoodsShipments.IgnoreQueryFilters()
                .Where(g => g.GoodsShipmentId.StartsWith(NamePrefix)).ToListAsync();
            if (shipments.Count > 0) db.GoodsShipments.RemoveRange(shipments);

            if (lines.Count > 0 || shipments.Count > 0) await db.SaveChangesAsync();

            var defs = await db.ReportDefinitions
                .Where(d => d.Name.StartsWith(NamePrefix)).ToListAsync();
            if (defs.Count == 0) return;

            var ids = defs.Select(d => d.Id).ToList();
            db.ReportRuns.RemoveRange(
                await db.ReportRuns.Where(r => ids.Contains(r.DefinitionId)).ToListAsync());
            db.ReportRecipients.RemoveRange(
                await db.ReportRecipients.Where(r => ids.Contains(r.DefinitionId)).ToListAsync());
            db.ReportDefinitions.RemoveRange(defs);
            await db.SaveChangesAsync();
        }
    }
}
