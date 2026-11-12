using System.Globalization;
using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.Acceptance
{
    /// <summary>
    /// 人工驗收桌機段的自動版：N-1（發退料單）、N-10（資產負債表／損益表列印與匯出）、C-6（貿易條件）、
    /// N-12（報表排程對象廠別）、C-4（合併財報＝各廠相加）。N-11 在 <see cref="QaPublicErrorUiTests"/>。
    ///
    /// <para>後端邏輯各自已有單元／整合測試；這裡只驗「畫面上看得到、按得動」——這幾項當初回報的
    /// 都是畫面層的缺陷（連結沒有 href、狀態印出資源鍵、檢視關不掉），後端測試抓不到。</para>
    ///
    /// <para>自備的資料一律 E2E-N1／E2E-C4／E2E-C6／E2E-NY 前綴，測完刪掉會影響別支測試的那幾筆
    /// （沒有異動的庫存列會讓月結的 INV_ROLLFORWARD 稽核擋下關帳）。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class DesktopAcceptanceUiTests
    {
        private readonly UiTestFixture _fixture;
        public DesktopAcceptanceUiTests(UiTestFixture fixture) => _fixture = fixture;

        // ── N-1 發退料單 ─────────────────────────────────────────────────────

        [Fact]
        public async Task N1_發退料單_發料只顯示來源倉_退料只顯示退回倉_選擇庫存帶入倉庫儲位批號()
        {
            await SeedFixture.ExecAsync("""
                INSERT INTO "Products"
                  ("ProductCode","ProductName","Spec","RoutingId","SalesUom","PurchaseUom","StockUom",
                   "StockMode","SafetyStock","StandardCost","MrpMode","LeadTimeDays","UrgentLeadTimeDays",
                   "OutsourceQtyPerDay","IsLotControlled","IqcExempt","IsActive","ApprovalStatus","CategoryCode","CreatedAt","UpdatedAt")
                VALUES ('E2E-N1LOT','E2E 批號管制料','DesktopAcceptanceUiTests',NULL,'E2E-EA','E2E-EA','E2E-EA',
                   'M',0,10,'P',1,1,0,true,true,true,0,'E2E-CAT',now(),now())
                ON CONFLICT ("ProductCode") DO UPDATE SET "IsActive"=true, "IsLotControlled"=true;
                INSERT INTO "Inventories" ("PlantId","ProductCode","LocationCode","LotNo","QtyOnHand","QtyReserved","UnitCost","LastUpdatedAt")
                VALUES ('P1','E2E-N1LOT','E2E-LRAW','E2E-N1-LOT',5,0,10,now())
                ON CONFLICT ("PlantId","ProductCode","LocationCode","LotNo") DO UPDATE SET "QtyOnHand"=5, "QtyReserved"=0;
                """);
            try
            {
                var page = await SignedInAsync();
                var errors = new List<string>();
                page.PageError += (_, e) => errors.Add(e);
                var crud = new DocFormPage(page, _fixture.BaseUrl);
                await crud.GotoIndexAsync("/MaterialSlip");
                await crud.OpenNewPanelAsync("button[data-url*='/MaterialSlip/Form?category']", "#materialSlipForm");

                const string F = "#materialSlipForm";
                Assert.True(await page.Locator($"{F} th.ms-col-from").IsVisibleAsync(), "發料單要顯示來源倉儲位");
                Assert.False(await page.Locator($"{F} th.ms-col-to").IsVisibleAsync(), "發料單不該顯示退回倉儲位");

                var before = await page.Locator($"{F} select[name$='.ProductCode']").CountAsync();
                await page.Locator($"{F} [data-action='addMsLine']").First.ClickAsync();
                await page.WaitForFunctionAsync(
                    $"n => document.querySelectorAll(\"{F} select[name$='.ProductCode']\").length > n", before);
                await page.EvaluateAsync(@"form => {
                    const sels = document.querySelectorAll(form + "" select[name$='.ProductCode']"");
                    const sel = sels[sels.length - 1];
                    if (![...sel.options].some(o => o.value === 'E2E-N1LOT')) throw new Error('品號清單沒有 E2E-N1LOT');
                    sel.value = 'E2E-N1LOT';
                    if (window.jQuery) window.jQuery(sel).trigger('change');
                    sel.dispatchEvent(new Event('change', { bubbles: true }));
                }", F);

                var row = page.Locator($"{F} #msLinesTable tbody tr").Last;
                await row.Locator("[data-action='lookupStock']").ClickAsync();
                var pick = page.Locator("#stockPickerTbody [data-action='pickStockRow'][data-lot='E2E-N1-LOT']");
                await Assertions.Expect(pick).ToBeVisibleAsync();
                await pick.ClickAsync();
                await Assertions.Expect(page.Locator("#stockPickerModal")).ToBeHiddenAsync();

                Assert.Equal("E2E-WRAW", await row.Locator("select[name$='.FromWarehouseCode']").InputValueAsync());
                Assert.Equal("E2E-LRAW", await row.Locator("select[name$='.FromLocationCode']").InputValueAsync());
                Assert.Equal("E2E-N1-LOT", await row.Locator("[name$='.LotNo']").InputValueAsync());

                // 改成退料類別：欄位對調
                await page.Locator($"{F} select[name='Category']").SelectOptionAsync("InternalReturn");
                Assert.False(await page.Locator($"{F} th.ms-col-from").IsVisibleAsync(), "退料單不該顯示來源倉儲位");
                Assert.True(await page.Locator($"{F} th.ms-col-to").IsVisibleAsync(), "退料單要顯示退回倉儲位");
                Assert.True(await row.Locator("select[name$='.ToWarehouseCode']").IsVisibleAsync());
                Assert.False(await row.Locator("select[name$='.FromWarehouseCode']").IsVisibleAsync());
                Assert.Empty(errors);
            }
            finally
            {
                await SeedFixture.ExecAsync("""DELETE FROM "Inventories" WHERE "ProductCode" = 'E2E-N1LOT';""");
            }
        }

        [Fact]
        public async Task N1_發退料單_清單狀態顯示中文_非草稿可檢視且關得掉()
        {
            // 每個狀態一張，清單上每一種徽章都看得到
            await SeedFixture.ExecAsync("""
                DELETE FROM "MaterialSlips" WHERE "SlipId" LIKE 'E2E-N1-S%';
                INSERT INTO "MaterialSlips" ("SlipId","SlipNo","Category","Status","SlipDate","SourceDocType","SourceDocId","PlantId","Remarks","CreatedAt","UpdatedAt")
                SELECT 'E2E-N1-S' || s, 'E2E-N1-S' || s, 0, s, current_date, 'WorkOrder', 'E2E-N1', 'P1', 'DesktopAcceptanceUiTests', now(), now()
                FROM generate_series(0, 7) AS s;
                """);
            try
            {
                var page = await SignedInAsync();
                var errors = new List<string>();
                page.PageError += (_, e) => errors.Add(e);
                await page.GotoAsync($"{_fixture.BaseUrl}/MaterialSlip?searched=true&categoryFilter=InternalIssue&searchText=E2E-N1-S");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var texts = new List<string>();
                foreach (var tr in await page.Locator("table tbody tr").AllAsync())
                {
                    if (!(await tr.InnerTextAsync()).Contains("E2E-N1-S")) continue;
                    texts.AddRange(await tr.Locator("span.badge").AllInnerTextsAsync());
                }
                Assert.True(texts.Count >= 16, $"清單上應有 8 張單（類別＋狀態兩個徽章），只找到 {texts.Count} 個徽章");
                foreach (var t in texts)
                {
                    Assert.DoesNotMatch(@"^(St|MaterialSlipStatus|Cat)[A-Z]", t.Trim());
                    Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", t);
                }
                Assert.Contains("待審核", texts);
                Assert.Contains("揀料中", texts);

                // 已過帳那張：檢視開在側面板，按關閉面板收起（不是另開整頁）
                var view = page.Locator("[data-action='openPanel'][data-url$='/MaterialSlip/Form/E2E-N1-S5']");
                Assert.Equal(1, await view.CountAsync());
                await view.ClickAsync();
                await page.WaitForSelectorAsync("#sidePanel.show #sidePanelBody [data-bs-dismiss='offcanvas']");
                Assert.Contains("/MaterialSlip", page.Url);
                Assert.DoesNotContain("/Form/", page.Url);
                await page.Locator("#sidePanelBody [data-bs-dismiss='offcanvas']").Last.ClickAsync();
                await Assertions.Expect(page.Locator("#sidePanel")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bshow\b"));
                Assert.Empty(errors);
            }
            finally
            {
                await SeedFixture.ExecAsync("""DELETE FROM "MaterialSlips" WHERE "SlipId" LIKE 'E2E-N1-S%';""");
            }
        }

        // ── N-10 資產負債表／損益表列印與匯出 ────────────────────────────────

        public static TheoryData<string, string, string> FinancialStatements => new()
        {
            // 畫面、查詢條件、列印頁上必定出現的科目（種子 E2E-TB01：資產 E2E-1101、費用 E2E-6101）
            { "/BalanceSheet", "asOfPeriod=209001", "E2E-1101" },
            { "/ProfitLoss",   "fromPeriod=209001&toPeriod=209001", "E2E-6101" },
        };

        [Theory]
        [MemberData(nameof(FinancialStatements))]
        public async Task N10_財報_列印開得出報表_匯出下載得到Excel(string path, string query, string account)
        {
            var page = await SignedInAsync();
            await page.GotoAsync($"{_fixture.BaseUrl}{path}?searched=true&{query}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var print = page.Locator("a:has(i.bi-printer)");
            var export = page.Locator("a:has(i.bi-file-earmark-excel)");
            Assert.Equal(1, await print.CountAsync());
            Assert.Equal(1, await export.CountAsync());
            Assert.Contains("/Print", await print.GetAttributeAsync("href") ?? "");   // 原缺陷：asp-action 沒被處理，沒有 href
            Assert.Contains("/Export", await export.GetAttributeAsync("href") ?? "");

            var popupTask = page.Context.WaitForPageAsync();
            await print.ClickAsync();
            var popup = await popupTask;
            await popup.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.Contains("/Print", popup.Url);
            Assert.Contains(account, await popup.InnerTextAsync("body"));
            await popup.CloseAsync();

            var download = await page.RunAndWaitForDownloadAsync(() => export.ClickAsync());
            Assert.EndsWith(".xlsx", download.SuggestedFilename);
            var file = await download.PathAsync();
            Assert.True(new FileInfo(file!).Length > 0, "匯出的檔案是空的");
        }

        // ── C-6 貿易條件 ──────────────────────────────────────────────────────

        public static TheoryData<string, string, string, string> TradeTermScreens => new()
        {
            // 畫面、表單、往來對象欄位、資料表
            { "/PurchaseOrder", "#poForm", "SupplierCode", "PurchaseOrders" },
            { "/SalesOrder",    "#soForm", "CompanyCode",  "SalesOrders" },
        };

        [Theory]
        [MemberData(nameof(TradeTermScreens))]
        public async Task C6_貿易條件_存檔後重開仍在(string index, string form, string partyField, string table)
        {
            // 乾淨部署沒有貿易條件代碼（要跑 scripts/seed-trade-terms.sql），自備一個
            await SeedFixture.ExecAsync("""
                INSERT INTO "CodeEntries" ("Id","Category","Code","NameZh","NameEn","Remark","IsActive","SortOrder","ApprovalStatus")
                VALUES ('e2e-c6-fob','trade_term','FOB','船上交貨','Free On Board','DesktopAcceptanceUiTests',true,4,2)
                ON CONFLICT ("Category","Code") DO UPDATE SET "IsActive"=true, "ApprovalStatus"=2;
                """);
            var remark = "E2E-C6-" + Guid.NewGuid().ToString("N")[..8];
            var idCol = table == "PurchaseOrders" ? "PurchaseOrderId" : "SalesOrderId";

            var page = await SignedInAsync();
            var crud = new DocFormPage(page, _fixture.BaseUrl);
            await crud.GotoIndexAsync(index);
            await crud.OpenNewPanelAsync($"button[data-url$='{index}/Create']", form);
            const string P = "#sidePanelBody";

            await page.Locator($"{P} select[name='{partyField}']").SelectOptionAsync("E2E-CO01");
            await page.WaitForTimeoutAsync(400);
            await page.Locator($"{P} select[name='TradeTermCode']").SelectOptionAsync("FOB");
            await page.Locator($"{P} [name='Remarks']").FillAsync(remark);
            await page.ClickAsync($"{P} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);
            var row = page.Locator($"{P} #linesTbody tr").Last;
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(600);
            await row.Locator("input[name$='.OrderQty']").First.FillAsync("1");
            var uom = row.Locator("select[name$='.UomCode']");
            if (await uom.CountAsync() > 0 && string.IsNullOrEmpty(await uom.First.InputValueAsync()))
                await uom.First.SelectOptionAsync("E2E-EA");
            await page.ClickAsync($"{P} button[type='submit']");

            string? id = null;
            for (var i = 0; i < 50 && id == null; i++)
            {
                id = await SeedFixture.ScalarAsync<string>($"""SELECT "{idCol}" FROM "{table}" WHERE "Remarks" = '{remark}'""");
                if (id == null) await Task.Delay(200);
            }
            Assert.True(id != null, $"{index} 存檔沒有成功（找不到備註 {remark} 的單）");
            Assert.Equal("FOB", await SeedFixture.ScalarAsync<string>($"""SELECT "TradeTermCode" FROM "{table}" WHERE "{idCol}" = '{id}'"""));

            // 回列表重開那張單，下拉仍選在 FOB
            await page.GotoAsync($"{_fixture.BaseUrl}{index}?searched=true&searchTerm={id}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var open = page.Locator($"[data-url*='{index}/Edit/{id}'], [data-url*='{index}/Details/{id}']").First;
            Assert.True(await open.CountAsync() == 1, $"列表上找不到 {id} 的開啟鈕");
            await open.ClickAsync();
            await page.WaitForSelectorAsync($"{P} {form}");
            var reopened = page.Locator($"{P} select[name='TradeTermCode']");
            Assert.Equal("FOB", await reopened.InputValueAsync());
        }

        // ── N-12 報表排程對象廠別 ─────────────────────────────────────────────

        [Fact]
        public async Task N12_報表排程_預設只列本廠_全部廠別才看得到別廠_下次執行依該廠時區_表單顯示對象廠別()
        {
            // 紐約廠每日 08:00：下次執行換成 UTC 存，畫面上應該仍是 08:00 並標出時區
            var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var nyNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ny);
            var nextLocal = nyNow.Date.AddDays(1).AddHours(8);
            var nextUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), ny);
            var name = "E2E-N12-" + Guid.NewGuid().ToString("N")[..8];

            await SeedFixture.ExecAsync("""
                INSERT INTO "Plants" ("Id","Name","NameEn","DefaultLanguage","DefaultTimeZone")
                VALUES ('E2E-NY','E2E 紐約廠','E2E New York','zh-TW','America/New_York')
                ON CONFLICT ("Id") DO UPDATE SET "DefaultTimeZone"='America/New_York';
                """);
            var id = await SeedFixture.ScalarAsync<int>($"""
                INSERT INTO "ReportDefinitions"
                  ("Name","OwnerUserId","OwnerRoleId","PlantId","ToolKey","ParamsJson","ScheduleKind","ScheduleHour","ScheduleMinute",
                   "NextRunAt","IsActive","ManualRunRequested","OutputFormat","CreatedByRoleId","CreatedAt")
                SELECT '{name}', u."Id", '1', 'E2E-NY', 'ReturnRate', '{"{"}"period":"Last30Days"{"}"}', 0, 8, 0,
                       '{nextUtc:yyyy-MM-dd HH:mm:ss}+00'::timestamptz, true, false, 'Csv', '1', now()
                FROM "AspNetUsers" u WHERE u."UserName" = 'admin'
                RETURNING "Id";
                """);
            try
            {
                var page = await SignedInAsync();
                var errors = new List<string>();
                page.PageError += (_, e) => errors.Add(e);

                // 預設：只列目前廠別（P1）
                await page.GotoAsync($"{_fixture.BaseUrl}/ReportSchedule?searched=true&searchTerm={name}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.Equal("P1", await page.Locator("select[name='plantFilter']").InputValueAsync());
                Assert.Equal(0, await page.Locator($"tr:has-text('{name}')").CountAsync());

                // 全部廠別：看得到，廠別欄寫 E2E-NY，下次執行 08:00＋America/New_York
                await page.GotoAsync($"{_fixture.BaseUrl}/ReportSchedule?searched=true&plantFilter=*&searchTerm={name}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var row = page.Locator($"tr:has-text('{name}')");
                Assert.Equal(1, await row.CountAsync());
                var cells = await row.Locator("td").AllInnerTextsAsync();
                Assert.StartsWith("E2E-NY", cells[1].Trim());
                Assert.Contains($"{nextLocal:yyyy-MM-dd} 08:00", cells[4]);
                Assert.Contains("America/New_York", cells[4]);

                // 編輯：顯示排程原本的廠別，不是目前所在的 P1
                await page.Locator($"[data-url$='/ReportSchedule/Edit/{id}']").ClickAsync();
                await page.WaitForSelectorAsync("#sidePanelBody #targetPlant");
                var target = await page.InnerTextAsync("#sidePanelBody #targetPlant");
                Assert.Contains("E2E-NY", target);
                Assert.Contains("America/New_York", target);

                // 新增：對象廠別＝目前廠別
                await page.GotoAsync($"{_fixture.BaseUrl}/ReportSchedule?searched=true");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator("[data-url$='/ReportSchedule/Create']").First.ClickAsync();
                await page.WaitForSelectorAsync("#sidePanelBody #targetPlant");
                Assert.Contains("P1", await page.InnerTextAsync("#sidePanelBody #targetPlant"));
                Assert.DoesNotContain("E2E-NY", await page.InnerTextAsync("#sidePanelBody #targetPlant"));

                // 收件匣與執行紀錄有廠別欄
                foreach (var screen in new[] { "/ReportInbox", "/ReportRunHistory" })
                {
                    await page.GotoAsync($"{_fixture.BaseUrl}{screen}?searched=true");
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    var headers = await page.Locator("table thead th").AllInnerTextsAsync();
                    Assert.True(headers.Count > 0, $"{screen} 沒有清單表格");
                    Assert.Contains(headers, h => h.Contains("廠別"));
                }
                Assert.Empty(errors);
            }
            finally
            {
                await SeedFixture.ExecAsync($"""DELETE FROM "ReportDefinitions" WHERE "Id" = {id};""");
            }
        }

        // ── C-4 合併財報 ──────────────────────────────────────────────────────

        [Fact]
        public async Task C4_合併資產負債表_某科目等於各廠相加_只勾本廠時等於本廠報表()
        {
            // 第二廠 E2E-P2 在 2090-01 放一筆 E2E-1101 借 700；P1 已有種子 E2E-TB01（貸 1000）與其他測試的分錄
            await SeedFixture.ExecAsync("""
                INSERT INTO "AccountingPeriods"
                  ("PeriodId","PlantId","Year","Month","StartDate","EndDate","Status","CreatedAt","UpdatedAt","Remarks")
                VALUES ('209001','E2E-P2',2090,1,'2090-01-01','2090-01-31',0,now(),now(),'DesktopAcceptanceUiTests')
                ON CONFLICT ("PeriodId","PlantId") DO NOTHING;
                DELETE FROM "JournalLines" WHERE "JournalId" = 'E2E-C4P2';
                DELETE FROM "JournalEntries" WHERE "JournalId" = 'E2E-C4P2';
                INSERT INTO "JournalEntries" ("JournalId","PeriodId","EntryDate","SourceDocType","SourceDocId","Type","Description","CreatedAt","PlantId")
                VALUES ('E2E-C4P2','209001','2090-01-10','Manual',NULL,1,'E2E C-4 第二廠分錄',now(),'E2E-P2');
                INSERT INTO "JournalLines" ("JournalId","LineNo","AccountCode","AccountName","Debit","Credit","CurrencyCode","Amount","ExchangeRate","AmountLocal","Description")
                VALUES ('E2E-C4P2',1,'E2E-1101','E2E 資產科目',700,0,NULL,700,1,700,'E2E C-4'),
                       ('E2E-C4P2',2,'E2E-6101','E2E 費用科目',0,700,NULL,700,1,700,'E2E C-4');
                """);
            try
            {
                var page = await SignedInAsync();
                var errors = new List<string>();
                page.PageError += (_, e) => errors.Add(e);

                await page.GotoAsync($"{_fixture.BaseUrl}/BalanceSheet?searched=true&asOfPeriod=209001");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var p1 = await AmountOfAsync(page, "tr:has(td code:text-is('E2E-1101')) td.text-end");

                await page.GotoAsync($"{_fixture.BaseUrl}/ConsolidatedStatement?searched=true&asOfPeriod=209001&plantIds=P1");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var onlyP1 = await AmountOfAsync(page, "tr:has(td:text-is('E2E-1101')) td.text-end");

                await page.GotoAsync($"{_fixture.BaseUrl}/ConsolidatedStatement?searched=true&asOfPeriod=209001&plantIds=P1&plantIds=E2E-P2");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var both = await AmountOfAsync(page, "tr:has(td:text-is('E2E-1101')) td.text-end");

                Assert.Equal(p1, onlyP1);
                Assert.Equal(p1 + 700m, both);
                Assert.Empty(errors);
            }
            finally
            {
                await SeedFixture.ExecAsync("""
                    DELETE FROM "JournalLines" WHERE "JournalId" = 'E2E-C4P2';
                    DELETE FROM "JournalEntries" WHERE "JournalId" = 'E2E-C4P2';
                    DELETE FROM "AccountingPeriods" WHERE "PlantId" = 'E2E-P2' AND "Remarks" = 'DesktopAcceptanceUiTests';
                    """);
            }
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private async Task<IPage> SignedInAsync()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            return page;
        }

        private static async Task<decimal> AmountOfAsync(IPage page, string cellSelector)
        {
            var cell = page.Locator(cellSelector);
            Assert.True(await cell.CountAsync() == 1, $"畫面上找不到唯一的金額格：{cellSelector}");
            var text = (await cell.InnerTextAsync()).Trim();
            return decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
    }
}
