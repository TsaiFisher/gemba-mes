using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機料件作業四頁（工單發退料／備料作業／委外收料／領料退料）按下去之後送出了什麼。
    ///
    /// <para><see cref="MobilePageScriptSmokeTests"/> 只驗「載入不報錯」。這四頁真正要守的是：
    /// 送出的內容對不對（選的儲位、批號、數量）、該排隊的離線時有排、<b>不該排隊的（委外收料、
    /// 備料確認鎖庫存）離線時絕對不排</b>、前端該擋的有擋（退料超量、委外缺儲位、負數）。
    /// 這些在伺服器整合測試看不到——那邊收到的已經是組好的請求。</para>
    ///
    /// <para>做法同 <see cref="MobileReportPageTests"/>：<c>/api/**</c> 全換假資料、封鎖 Service Worker，
    /// 不登入、不碰資料庫，只要有東西在供應 wwwroot 就能跑。</para>
    /// </summary>
    public class MobileMaterialPageTests : IAsyncLifetime
    {
        private static readonly string FakeUserJson = JsonSerializer.Serialize(new
        {
            id = "smoke-user", name = "smoke", displayName = "Smoke", activeRoleId = "r1",
            plantId = "P1", roles = new[] { "Admin" }
        });

        private static string Ok(object data) => JsonSerializer.Serialize(new { success = true, data });

        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;
        private string _baseUrl = null!;

        public async Task InitializeAsync()
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.test.json", optional: true)
                .Build();
            _baseUrl = (Environment.GetEnvironmentVariable("BaseUrl") ?? config["BaseUrl"] ?? "http://localhost:5110")
                .TrimEnd('/');
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }

        public async Task DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }

        // ═══════════════ 工單發退料 inv/issue.html ═══════════════

        private static readonly Dictionary<string, string> IssueApi = new()
        {
            ["/api/mobile/material/slips?kind=issue"] = Ok(new[]
            {
                new { slipId = "MI1", category = "InternalIssue", status = "Draft", sourceDocId = "WO1", lineCount = 1 },
            }),
            ["/api/mobile/material/slips?kind=return"] = Ok(new[]
            {
                new { slipId = "MX1", category = "InternalReturn", status = "Draft", sourceDocId = "WO1", lineCount = 1 },
            }),
            ["/api/mobile/material/slips/MI1"] = Ok(new
            {
                slipId = "MI1", category = "InternalIssue", status = "Draft", sourceDocId = "WO1", isReturn = false,
                lines = new[]
                {
                    new
                    {
                        lineNo = 1, productCode = "MAT<b>1</b>", uomCode = "PC", qty = 30m, stagedLocation = "STG-01",
                        stock = new[]
                        {
                            new { whCode = "STG", locationCode = "STG-01", lotNo = "L9", qtyOnHand = 30m, qtyAvailable = 30m },
                            new { whCode = "W1",  locationCode = "A-01",   lotNo = "",   qtyOnHand = 80m, qtyAvailable = 50m },
                        },
                    },
                },
            }),
            ["/api/mobile/material/slips/MX1"] = Ok(new
            {
                slipId = "MX1", category = "InternalReturn", status = "Draft", sourceDocId = "WO1", isReturn = true,
                lines = new[] { new { lineNo = 1, productCode = "MAT1", uomCode = "PC", qty = 10m, stock = Array.Empty<object>() } },
            }),
        };

        [Fact]
        public async Task 工單發料_掃箱號整箱領_送出箱子的儲位批號與箱內量()
        {
            var api = new Dictionary<string, string>(IssueApi)
            {
                // 箱子 API 不是 { success, data } 包裝，直接回物件（WarehouseApiController.ContainerUnit）
                ["/api/warehouse/container-unit/BX-00001"] = JsonSerializer.Serialize(new
                {
                    containerUnitId = "BX-00001", productCode = "MAT<b>1</b>", lotNo = "LB1",
                    locations = new[] { new { whCode = "W1", locationCode = "A-01", qty = 12m } },
                }),
            };
            await using var h = await OpenAsync("/mobile/inv/issue.html", api, "[data-action='openSlip']");
            await h.Page.ClickAsync("[data-action='openSlip']");
            await h.Page.Locator(".stock-chip").First.WaitForAsync();

            await FillAndBlurAsync(h, "input[data-action-change='onBoxInput']", "BX-00001");
            await Assertions.Expect(h.Page.Locator("[data-box-info]")).ToContainTextAsync("LB1");
            await Assertions.Expect(h.Page.Locator("input[data-action-change='onQtyInput']")).ToHaveValueAsync("12");  // 整箱＝箱內 12（這行要 30）

            await h.Page.ClickAsync("#btnPost");
            await WaitForPostAsync(h);
            var line = Assert.Single(h.Posts).Json.RootElement.GetProperty("lines")[0];
            Assert.Equal("A-01", line.GetProperty("locationCode").GetString());
            Assert.Equal("LB1", line.GetProperty("lotNo").GetString());
            Assert.Equal(12m, line.GetProperty("qty").GetDecimal());
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 工單發料_預選備料儲位_改選其他儲位後送出該儲位與數量()
        {
            await using var h = await OpenAsync("/mobile/inv/issue.html", IssueApi, "[data-action='openSlip']");
            await h.Page.ClickAsync("[data-action='openSlip']");
            await h.Page.Locator(".stock-chip").First.WaitForAsync();

            // 已備料的儲位排第一、預先選好、標「備料」
            Assert.Equal("STG-01", await h.Page.Locator(".stock-chip.active").GetAttributeAsync("data-loc"));
            Assert.Equal(1, await h.Page.Locator(".stock-chip.active .badge").CountAsync());
            // 品號是使用者可維護的主檔欄位：要以文字顯示，不能被當成 HTML
            Assert.Equal("MAT<b>1</b>", await h.Page.Locator(".mat-product").TextContentAsync());

            await h.Page.ClickAsync(".stock-chip[data-loc='A-01']");
            await FillAndBlurAsync(h, "input[data-action-change='onQtyInput']", "25");
            await h.Page.ClickAsync("#btnPost");
            await WaitForPostAsync(h);

            var post = Assert.Single(h.Posts);
            Assert.Equal("/api/mobile/material/slips/MI1/post", post.Path);
            var line = post.Json.RootElement.GetProperty("lines")[0];
            Assert.Equal("W1", line.GetProperty("whCode").GetString());
            Assert.Equal("A-01", line.GetProperty("locationCode").GetString());
            Assert.Equal(25m, line.GetProperty("qty").GetDecimal());
            Assert.False(string.IsNullOrEmpty(post.Json.RootElement.GetProperty("clientId").GetString()));
            Assert.Empty(h.Errors);
        }

        // 備料區只有 10、這一行要發 30：自動拆成「備料區 10＋其他儲位 20」（伺服器規定先扣備料區）
        private static readonly Dictionary<string, string> IssueApiShortStaged = new(IssueApi)
        {
            ["/api/mobile/material/slips/MI1"] = Ok(new
            {
                slipId = "MI1", category = "InternalIssue", status = "Draft", sourceDocId = "WO1", isReturn = false,
                lines = new[]
                {
                    new
                    {
                        lineNo = 1, productCode = "MAT1", uomCode = "PC", qty = 30m, stagedLocation = "STG-01",
                        stock = new[]
                        {
                            new { whCode = "STG", locationCode = "STG-01", lotNo = "", qtyOnHand = 10m, qtyAvailable = 10m },
                            new { whCode = "W1",  locationCode = "A-01",   lotNo = "", qtyOnHand = 80m, qtyAvailable = 50m },
                        },
                    },
                },
            }),
        };

        [Fact]
        public async Task 工單發料_備料區不夠_自動拆成備料區加其他儲位_送出同一行兩筆()
        {
            await using var h = await OpenAsync("/mobile/inv/issue.html", IssueApiShortStaged, "[data-action='openSlip']");
            await h.Page.ClickAsync("[data-action='openSlip']");
            await h.Page.Locator(".pick-entry").Nth(1).WaitForAsync();

            Assert.Equal(2, await h.Page.Locator(".pick-entry").CountAsync());
            Assert.True(await h.Page.Locator("[data-staged-hint]").IsVisibleAsync(), "要提示先從備料儲位領用");
            Assert.Equal("STG-01", await h.Page.Locator(".pick-entry").Nth(0).Locator(".stock-chip.active").GetAttributeAsync("data-loc"));
            Assert.Equal("A-01",   await h.Page.Locator(".pick-entry").Nth(1).Locator(".stock-chip.active").GetAttributeAsync("data-loc"));

            await h.Page.ClickAsync("#btnPost");
            await WaitForPostAsync(h);
            var lines = Assert.Single(h.Posts).Json.RootElement.GetProperty("lines").EnumerateArray()
                .Select(l => (l.GetProperty("lineNo").GetInt32(), l.GetProperty("locationCode").GetString(), l.GetProperty("qty").GetDecimal()))
                .ToList();
            Assert.Equal(new[] { (1, (string?)"STG-01", 10m), (1, (string?)"A-01", 20m) }, lines);
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 工單發料_可手動從其他儲位補一筆_也可移除()
        {
            await using var h = await OpenAsync("/mobile/inv/issue.html", IssueApi, "[data-action='openSlip']");
            await h.Page.ClickAsync("[data-action='openSlip']");
            await h.Page.Locator(".stock-chip").First.WaitForAsync();
            Assert.Equal(1, await h.Page.Locator(".pick-entry").CountAsync());

            await FillAndBlurAsync(h, "input[data-action-change='onQtyInput']", "25");
            await h.Page.ClickAsync("[data-action='addPickEntry']");
            Assert.Equal(2, await h.Page.Locator(".pick-entry").CountAsync());
            Assert.Equal("5", await h.Page.Locator(".pick-entry").Nth(1).Locator("input.qty-input").InputValueAsync());   // 補的量＝還沒分配的量
            Assert.Equal("A-01", await h.Page.Locator(".pick-entry").Nth(1).Locator(".stock-chip.active").GetAttributeAsync("data-loc"));

            await h.Page.ClickAsync("[data-action='removePickEntry']");
            Assert.Equal(1, await h.Page.Locator(".pick-entry").CountAsync());
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 工單發料_離線時排進佇列不直接送出()
        {
            await using var h = await OpenAsync("/mobile/inv/issue.html", IssueApi, "[data-action='openSlip']");
            await h.Page.ClickAsync("[data-action='openSlip']");
            await h.Page.Locator(".stock-chip").First.WaitForAsync();
            await h.Context.SetOfflineAsync(true);

            await h.Page.ClickAsync("#btnPost");
            await WaitForQueuedAsync(h, 1);

            Assert.Equal(new[] { "/api/mobile/material/slips/MI1/post" }, await QueuedUrlsAsync(h));
            Assert.Empty(h.Posts);
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 退料收料_沒填退回儲位擋下_填了才送出且只送儲位由伺服器補倉庫()
        {
            await using var h = await OpenAsync("/mobile/inv/issue.html", IssueApi, "[data-action='openSlip']");
            await h.Page.ClickAsync("label[for='kindReturn']");
            await h.Page.Locator("[data-action='openSlip'][data-id='MX1']").WaitForAsync();
            await h.Page.ClickAsync("[data-action='openSlip'][data-id='MX1']");
            await h.Page.Locator("input[data-action-change='onLocInput']").WaitForAsync();

            await h.Page.ClickAsync("#btnPost");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();
            Assert.Empty(h.Posts);

            await FillAndBlurAsync(h, "input[data-action-change='onLocInput']", "RTN-01");
            await h.Page.ClickAsync("#btnPost");
            await WaitForPostAsync(h);

            var line = Assert.Single(h.Posts).Json.RootElement.GetProperty("lines")[0];
            Assert.Equal("RTN-01", line.GetProperty("locationCode").GetString());
            Assert.Equal(JsonValueKind.Null, line.GetProperty("whCode").ValueKind);
            Assert.Empty(h.Errors);
        }

        // ═══════════════ 備料作業 inv/staging.html ═══════════════

        private static readonly Dictionary<string, string> StagingApi = new()
        {
            ["/api/mobile/material/staging"] = Ok(new[]
            {
                new { stagingId = "IM-D", status = "Draft", sourceId = "WO1", stagingWhCode = "STG", stagingLocationCode = "STG-01", lineCount = 1 },
                new { stagingId = "IM-C", status = "Confirmed", sourceId = "WO1", stagingWhCode = "STG", stagingLocationCode = "STG-01", lineCount = 1 },
            }),
            ["/api/mobile/material/staging/IM-D"] = Ok(new
            {
                stagingId = "IM-D", status = "Draft", sourceId = "WO1", stagingWhCode = "STG", stagingLocationCode = "STG-01",
                lines = new[] { new { lineNo = 1, productCode = "MAT1", uomCode = "PC", requiredQty = 30m, pickedQty = 0m, stock = Array.Empty<object>() } },
            }),
            ["/api/mobile/material/staging/IM-C"] = Ok(new
            {
                stagingId = "IM-C", status = "Confirmed", sourceId = "WO1", stagingWhCode = "STG", stagingLocationCode = "STG-01",
                lines = new[]
                {
                    new
                    {
                        lineNo = 1, productCode = "MAT1", uomCode = "PC", requiredQty = 30m, pickedQty = 0m,
                        stock = new[] { new { whCode = "W1", locationCode = "A-01", lotNo = "", qtyOnHand = 80m, qtyAvailable = 50m } },
                    },
                },
            }),
        };

        [Fact]
        public async Task 備料確認鎖庫存_離線時不送也不排隊()
        {
            await using var h = await OpenAsync("/mobile/inv/staging.html", StagingApi, "[data-action='openOrder']");
            await h.Page.ClickAsync("[data-action='openOrder'][data-id='IM-D']");
            await h.Page.Locator("#btnConfirm:not(.u-display-none)").WaitForAsync();
            await h.Context.SetOfflineAsync(true);

            await h.Page.ClickAsync("#btnConfirm");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();

            Assert.Empty(h.Posts);
            Assert.Empty(await QueuedUrlsAsync(h));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 備料移入_送出所選來源儲位_離線則排隊()
        {
            await using var h = await OpenAsync("/mobile/inv/staging.html", StagingApi, "[data-action='openOrder']");
            await h.Page.ClickAsync("[data-action='openOrder'][data-id='IM-C']");
            await h.Page.Locator(".stock-chip.active").WaitForAsync();

            await h.Page.ClickAsync("#btnStage");
            await WaitForPostAsync(h);
            var post = Assert.Single(h.Posts);
            Assert.Equal("/api/mobile/material/staging/IM-C/stage", post.Path);
            Assert.Equal("A-01", post.Json.RootElement.GetProperty("lines")[0].GetProperty("locationCode").GetString());

            await h.Page.Locator("[data-action='openOrder'][data-id='IM-C']").WaitForAsync();
            await h.Page.ClickAsync("[data-action='openOrder'][data-id='IM-C']");
            await h.Page.Locator(".stock-chip.active").WaitForAsync();
            await h.Context.SetOfflineAsync(true);
            await h.Page.ClickAsync("#btnStage");
            await WaitForQueuedAsync(h, 1);
            Assert.Single(h.Posts);
            Assert.Empty(h.Errors);
        }

        // ═══════════════ 委外收料 inv/outsource-receive.html ═══════════════

        private static Dictionary<string, string> OutsourceApi(bool requiresLocation, bool inspectAtReceipt = false, bool isOperation = false) => new()
        {
            ["/api/mobile/material/outsource-receipts/approved"] = Ok(Array.Empty<object>()),
            ["/api/mobile/material/outsource-orders"] = Ok(new[]
            {
                new { outsourcePurchaseOrderId = "OPO1", supplierCode = "S1", supplierName = "Sup", remainingQty = 50m, lineCount = 1 },
            }),
            ["/api/mobile/material/outsource-orders/OPO1"] = Ok(new
            {
                outsourcePurchaseOrderId = "OPO1", supplierCode = "S1", requiresLocation, inspectAtReceipt, isOperation,
                lines = new[] { new { lineNo = 1, productCode = "FG1", uomCode = "PC", orderedQty = 50m, receivedQty = 0m, draftQty = 0m, remainingQty = 50m } },
            }),
        };

        private static async Task OpenOpoAsync(Harness h)
        {
            await h.Page.ClickAsync("[data-action='openOpo']");
            await h.Page.Locator("input[data-field='qty']").WaitForAsync();
        }

        [Fact]
        public async Task 委外收料_送出採購單行號實收量與批號_不帶任何金額()
        {
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html", OutsourceApi(false), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            await FillAndBlurAsync(h, "input[data-field='qty']", "20");
            await FillAndBlurAsync(h, "input[data-field='lot']", "LOT-A");
            await h.Page.CheckAsync("#isFinal");
            await h.Page.ClickAsync("#btnReceive");
            await WaitForPostAsync(h);

            var post = Assert.Single(h.Posts);
            Assert.Equal("/api/mobile/material/outsource-receipts", post.Path);
            var root = post.Json.RootElement;
            Assert.Equal("OPO1", root.GetProperty("opoId").GetString());
            Assert.True(root.GetProperty("isFinalReceipt").GetBoolean());
            var line = root.GetProperty("lines")[0];
            Assert.Equal(1, line.GetProperty("opoLineNo").GetInt32());
            Assert.Equal(20m, line.GetProperty("qty").GetDecimal());
            Assert.Equal("LOT-A", line.GetProperty("lotNo").GetString());
            // 單價／幣別／匯率由伺服器取自委外採購單，手機不可能送金額過去
            Assert.False(line.TryGetProperty("unitCost", out _));
            Assert.False(line.TryGetProperty("remaining", out _));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 委外收料_離線時不送也不排隊()
        {
            // 收料同時產生應付發票：排隊幾小時後才失敗的帳不能讓人以為已經收好
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html", OutsourceApi(false), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            await h.Context.SetOfflineAsync(true);

            await h.Page.ClickAsync("#btnReceive");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();

            Assert.Empty(h.Posts);
            Assert.Empty(await QueuedUrlsAsync(h));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 委外收料_簡化模式沒填儲位擋下()
        {
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html", OutsourceApi(true), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            Assert.True(await h.Page.Locator("#locHint").IsVisibleAsync());

            await h.Page.ClickAsync("#btnReceive");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();
            Assert.Empty(h.Posts);

            await FillAndBlurAsync(h, "input[data-field='loc']", "FG-01");
            await h.Page.ClickAsync("#btnReceive");
            await WaitForPostAsync(h);
            Assert.Equal("FG-01", Assert.Single(h.Posts).Json.RootElement.GetProperty("lines")[0].GetProperty("locationCode").GetString());
        }

        // ── 委外簡化 B2 當場驗收／C 工序委外（mfg-domain.md §19.7）──────────────────

        [Fact]
        public async Task 委外收料_未開當場驗收_沒有不良數欄位_送出不帶不良數()
        {
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html", OutsourceApi(false), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            Assert.Equal(0, await h.Page.Locator("input[data-field='fail']").CountAsync());
            Assert.False(await h.Page.Locator("#inspectHint").IsVisibleAsync());
            Assert.False(await h.Page.Locator("#opHint").IsVisibleAsync());

            await h.Page.ClickAsync("#btnReceive");
            await WaitForPostAsync(h);
            var line = Assert.Single(h.Posts).Json.RootElement.GetProperty("lines")[0];
            Assert.Equal(JsonValueKind.Null, line.GetProperty("failQty").ValueKind);
        }

        [Fact]
        public async Task 委外收料_當場驗收_不良數超過收量擋下_合理才送出並帶不良數()
        {
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html", OutsourceApi(false, inspectAtReceipt: true), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            Assert.True(await h.Page.Locator("#inspectHint").IsVisibleAsync());
            Assert.Equal(1, await h.Page.Locator("input[data-field='loc']").CountAsync());   // 合格入一般倉：可指定儲位（不填用預設）

            await FillAndBlurAsync(h, "input[data-field='qty']", "10");
            await FillAndBlurAsync(h, "input[data-field='fail']", "11");
            await h.Page.ClickAsync("#btnReceive");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();
            Assert.Empty(h.Posts);

            await FillAndBlurAsync(h, "input[data-field='fail']", "2");
            await h.Page.ClickAsync("#btnReceive");
            await WaitForPostAsync(h);
            var line = Assert.Single(h.Posts).Json.RootElement.GetProperty("lines")[0];
            Assert.Equal((10m, 2m), (line.GetProperty("qty").GetDecimal(), line.GetProperty("failQty").GetDecimal()));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 委外收料_工序委外_顯示工序委外提示_不顯示儲位也不顯示一般驗收提示()
        {
            // 收回的是在製品、不入庫：就算開了當場驗收，也不該叫人填儲位
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html",
                OutsourceApi(false, inspectAtReceipt: true, isOperation: true), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            Assert.True(await h.Page.Locator("#opHint").IsVisibleAsync());
            Assert.False(await h.Page.Locator("#inspectHint").IsVisibleAsync());
            Assert.Equal(0, await h.Page.Locator("input[data-field='loc']").CountAsync());
            Assert.Equal(1, await h.Page.Locator("input[data-field='fail']").CountAsync());   // 不良數仍可填：完工量＝收回－不良

            await FillAndBlurAsync(h, "input[data-field='fail']", "1");
            await h.Page.ClickAsync("#btnReceive");
            await WaitForPostAsync(h);
            var line = Assert.Single(h.Posts).Json.RootElement.GetProperty("lines")[0];
            Assert.Equal(JsonValueKind.Null, line.GetProperty("locationCode").ValueKind);
            Assert.Equal(1m, line.GetProperty("failQty").GetDecimal());
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 委外收料_超過未收量要確認_取消就不送()
        {
            await using var h = await OpenAsync("/mobile/inv/outsource-receive.html", OutsourceApi(false), "[data-action='openOpo']");
            await OpenOpoAsync(h);
            await FillAndBlurAsync(h, "input[data-field='qty']", "60");

            var asked = false;
            h.Page.Dialog += async (_, d) => { asked = true; await d.DismissAsync(); };
            await h.Page.ClickAsync("#btnReceive");
            await h.Page.WaitForTimeoutAsync(300);

            Assert.True(asked, "超收沒有要求確認");
            Assert.Empty(h.Posts);
        }

        // ═══════════════ 領料／退料 mfg/requisition.html ═══════════════

        // 額度由伺服器算好帶下來（10/10 起）：MAT1 領料用料 需求 30、已發 20 → 可申請 10、可退 20；
        // MAT2 發料用料 → 不能申請、也沒有可退。
        private static readonly Dictionary<string, string> RequisitionApi = new()
        {
            ["/api/mobile/material/dispatches/D1/materials"] = Ok(new
            {
                workOrderId = "WO1",
                isOutsource = false,
                materials = new[]
                {
                    new { productCode = "MAT1", uomCode = "PC", issueMethod = "Pull", requiredQty = 30m, issuedQty = 20m, returnedQty = 0m, netIssuedQty = 20m,
                          isPull = true,  requestableQty = 10m, returnableQty = 20m },
                    new { productCode = "MAT2", uomCode = "PC", issueMethod = "Push", requiredQty = 10m, issuedQty = 0m,  returnedQty = 0m, netIssuedQty = 0m,
                          isPull = false, requestableQty = 0m,  returnableQty = 0m },
                },
            }),
        };

        [Fact]
        public async Task 領料申請_只列可申請的領料用料_超量擋下_未超量送出品號與數量()
        {
            await using var h = await OpenAsync("/mobile/mfg/requisition.html?dispatchId=D1", RequisitionApi, "#matRows .mat-line");
            Assert.Equal(0, await h.Page.Locator("input[data-product='MAT2']").CountAsync());   // 發料用料不列

            await FillAndBlurAsync(h, "input[data-product='MAT1']", "11");
            await h.Page.ClickAsync("#btnReq");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();
            Assert.Empty(h.Posts);

            await FillAndBlurAsync(h, "input[data-product='MAT1']", "4");
            await h.Page.ClickAsync("#btnReq");
            await WaitForPostAsync(h);

            var post = Assert.Single(h.Posts);
            Assert.Equal("/api/mobile/material/dispatches/D1/requisition", post.Path);
            var line = post.Json.RootElement.GetProperty("lines")[0];
            Assert.Equal("MAT1", line.GetProperty("productCode").GetString());
            Assert.Equal(4m, line.GetProperty("qty").GetDecimal());
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 退料_只列可退的料_超量擋下_未超量送到退料端點()
        {
            await using var h = await OpenAsync("/mobile/mfg/requisition.html?dispatchId=D1", RequisitionApi, "#matRows .mat-line");
            await h.Page.ClickAsync("label[for='kindRet']");
            await h.Page.Locator("input[data-product='MAT1']").WaitForAsync();
            Assert.Equal(0, await h.Page.Locator("input[data-product='MAT2']").CountAsync());

            await FillAndBlurAsync(h, "input[data-product='MAT1']", "21");
            await h.Page.ClickAsync("#btnReq");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();
            Assert.Empty(h.Posts);

            await FillAndBlurAsync(h, "input[data-product='MAT1']", "10");
            await h.Page.ClickAsync("#btnReq");
            await WaitForPostAsync(h);
            Assert.Equal("/api/mobile/material/dispatches/D1/return", Assert.Single(h.Posts).Path);
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 委外派工_整頁停用_送出鈕不能按()
        {
            var api = new Dictionary<string, string>
            {
                ["/api/mobile/material/dispatches/D1/materials"] = Ok(new
                {
                    workOrderId = "WO1", isOutsource = true, materials = Array.Empty<object>(),
                }),
            };
            await using var h = await OpenAsync("/mobile/mfg/requisition.html?dispatchId=D1", api, "#matRows .alert");
            Assert.True(await h.Page.Locator("#btnReq").IsDisabledAsync());
            Assert.Empty(h.Errors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private sealed record Post(string Path, JsonDocument Json);

        private sealed class Harness : IAsyncDisposable
        {
            public required IBrowserContext Context { get; init; }
            public required IPage Page { get; init; }
            public List<Post> Posts { get; } = new();
            public List<string> Errors { get; } = new();
            public ValueTask DisposeAsync() => new(Context.CloseAsync());
        }

        private static async Task FillAndBlurAsync(Harness h, string selector, string value)
        {
            // 這幾頁的欄位走 data-action-change（change 事件），要失焦才會寫回頁面狀態
            await h.Page.FillAsync(selector, value);
            await h.Page.Locator(selector).DispatchEventAsync("change");
        }

        private static async Task WaitForPostAsync(Harness h, int count = 1)
        {
            for (var i = 0; i < 50 && h.Posts.Count < count; i++) await Task.Delay(100);
            Assert.True(h.Posts.Count >= count,
                $"沒有送出請求（預期 {count} 筆）。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        private static async Task<string[]> QueuedUrlsAsync(Harness h) =>
            await h.Page.EvaluateAsync<string[]>(
                "async () => window.offlineQueue ? (await offlineQueue.getPending()).map(p => p.url) : []");

        /// <summary>非同步條件在 C# 端輪詢（WaitForFunctionAsync 收到 Promise 會當成真值立刻通過）。</summary>
        private static async Task WaitForQueuedAsync(Harness h, int count)
        {
            for (var i = 0; i < 50; i++)
            {
                if ((await QueuedUrlsAsync(h)).Length >= count) return;
                await Task.Delay(100);
            }
            Assert.Fail($"離線佇列沒有排進 {count} 筆。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        private async Task<Harness> OpenAsync(string pagePath, Dictionary<string, string> api, string readySelector)
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });
            var page = await context.NewPageAsync();
            var h = new Harness { Context = context, Page = page };

            await context.RouteAsync("**/api/**", route =>
            {
                var req = route.Request;
                var uri = new Uri(req.Url);
                if (req.Method != "GET")
                {
                    h.Posts.Add(new Post(uri.AbsolutePath, JsonDocument.Parse(string.IsNullOrEmpty(req.PostData) ? "{}" : req.PostData)));
                    return route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 200, ContentType = "application/json",
                        Body = JsonSerializer.Serialize(new { success = true, data = new { }, message = "ok" }),
                    });
                }
                var body = uri.AbsolutePath == "/api/mobile/auth/me" ? FakeUserJson
                    : api.TryGetValue(uri.PathAndQuery, out var b) ? b
                    : api.TryGetValue(uri.AbsolutePath, out var b2) ? b2
                    : "{\"success\":true,\"data\":[]}";
                return route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = body });
            });
            await MobileFakeApi.IgnoreTelemetryAsync(context);   // 遙測不算寫入（見 MobileFakeApi）

            page.PageError += (_, message) => h.Errors.Add("pageerror: " + message);
            page.Console += (_, msg) =>
            {
                if (msg.Type == "error" && !msg.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                    h.Errors.Add("console.error: " + msg.Text);
            };

            await page.GotoAsync(_baseUrl + pagePath,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            await page.Locator(readySelector).First.WaitForAsync();
            return h;
        }
    }
}
