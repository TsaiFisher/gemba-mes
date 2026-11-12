using System.Globalization;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.ACT
{
    /// <summary>
    /// 財務報表「真的按查詢、真的讀數字」：AR／AP 帳齡、試算表、ECL、NRV。
    ///
    /// <para><b>補的缺口</b>：這五支之前只有 MenuWalk 開過頁面——而 MenuWalk 不帶
    /// <c>searched=true</c>，報表一律停在「請先查詢」，查詢與計算從沒跑過。
    /// 種子也只有已付款發票，就算按了查詢也是空表，空表不會讓任何斷言失敗。</para>
    ///
    /// <para><b>預期值寫死、來自種子 ㉑ 的設計</b>，不是拿同一套公式在測試裡再算一次——
    /// 那樣程式算錯，測試也跟著算錯。唯一例外是 ECL：它彙總全廠所有未結發票，
    /// 其他測試開的發票會混進來，只能用資料庫當對照（見該測試的說明）。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class FinanceReportUiTests
    {
        private readonly UiTestFixture _fixture;
        public FinanceReportUiTests(UiTestFixture fixture) => _fixture = fixture;

        /// <summary>結帳測試專用期間（種子 ㉑）。</summary>
        private const string E2EPeriod = "209001";

        /// <summary>廠別當地日（app 以此為帳齡基準日；DB 的 current_date 可能是 UTC）。</summary>
        private static string LocalToday =>
            TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Taipei").ToString("yyyy-MM-dd");

        private async Task<IPage> OpenAsync(string pathAndQuery)
        {
            await SeedFixture.ResetAsync();
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}{pathAndQuery}",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            return page;
        }

        private static decimal Money(string text) =>
            decimal.Parse(text.Trim().Replace(",", ""), NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);

        private static async Task<decimal[]> RowMoneyAsync(IPage page, string rowFirstCell, int from, int count)
        {
            var row = page.Locator("table tbody tr").Filter(new LocatorFilterOptions
            {
                Has = page.Locator($"td:first-child:text-is('{rowFirstCell}')"),
            });
            Assert.True(await row.CountAsync() == 1, $"報表上找不到「{rowFirstCell}」那一列（或不只一列）。");
            var cells = await row.Locator("td").AllInnerTextsAsync();
            return cells.Skip(from).Take(count).Select(Money).ToArray();
        }

        // ── 帳齡 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task AR帳齡_五個區間與合計對得上種子_已收清與作廢不列入()
        {
            var page = await OpenAsync($"/ARAging?asOf={LocalToday}&searchCustomer=E2E-AGC&searched=true");

            // 未到期、1-30、31-60（3000 已收 1000）、61-90、90+、合計。E2E-AGR6（已收清 9999）
            // 與 E2E-AGR7（作廢 8888）若被算進來，90+ 會變成 14999 或 13888。
            var v = await RowMoneyAsync(page, "E2E-AGC", 1, 6);
            Assert.Equal(new[] { 1000m, 2000m, 2000m, 4000m, 5000m, 14000m }, v);
        }

        [Fact]
        public async Task AP帳齡_五個區間與合計對得上種子_已付清與作廢不列入()
        {
            var page = await OpenAsync($"/APAging?asOf={LocalToday}&searchSupplier=E2E-AGS&searched=true");

            var v = await RowMoneyAsync(page, "E2E-AGS", 1, 6);
            Assert.Equal(new[] { 1100m, 2200m, 2200m, 4400m, 5500m, 15400m }, v);
        }

        // ── 呆帳提列 ────────────────────────────────────────────────────────

        /// <summary>
        /// 與 AR 帳齡同一批發票、同一口徑（本幣未收、依到期日），損失率與 ECL 共用預設：
        /// 未到期 1000×0.5%=5、1-30 2000×2%=40、31-60 2000×5%=100、61-90 4000×10%=400、90+ 5000×30%=1500。
        /// 改寫前它拿出貨單代 AR——E2E-AGC 沒有任何出貨，這裡會是一張空表。
        /// </summary>
        [Fact]
        public async Task 呆帳提列_與AR帳齡同口徑_各區間提列金額正確()
        {
            var page = await OpenAsync($"/BadDebtProvision?asOfDate={LocalToday}&searchCustomer=E2E-AGC&searched=true");

            var rows = page.Locator("table tbody tr").Filter(new LocatorFilterOptions { HasText = "E2E-AGC" });
            var actual = new Dictionary<string, (decimal Amount, decimal Provision)>();
            for (var i = 0; i < await rows.CountAsync(); i++)
            {
                var c = await rows.Nth(i).Locator("td").AllInnerTextsAsync();
                actual[c[1].Trim()] = (Money(c[3]), Money(c[5]));
            }

            Assert.Equal(new Dictionary<string, (decimal, decimal)>
            {
                ["Current"] = (1000m, 5m),
                ["1-30"]    = (2000m, 40m),
                ["31-60"]   = (2000m, 100m),
                ["61-90"]   = (4000m, 400m),
                ["90+"]     = (5000m, 1500m),
            }, actual);
        }

        // ── 試算表 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task 試算表_專用期間只有種子那張分錄_借貸各一千且平衡()
        {
            var page = await OpenAsync($"/TrialBalance?fromPeriod={E2EPeriod}&toPeriod={E2EPeriod}&searched=true");

            Assert.True(await page.Locator(".alert-success").CountAsync() > 0,
                "試算表沒有顯示「借貸平衡」。");

            // 欄位：科目、名稱、期初、本期借、本期貸、期末。只驗本期借貸——期初含其他測試的歷史分錄。
            var debitRow  = await RowMoneyAsync(page, "E2E-6101", 3, 1);
            var creditRow = await RowCellsAsync(page, "E2E-1101");
            Assert.Equal(1000m, debitRow[0]);
            Assert.Equal("", creditRow[3].Trim());            // 本期借方空白
            Assert.Equal(1000m, Money(creditRow[4]));         // 本期貸方
        }

        [Fact]
        public async Task 試算表_當期含全部流程測試的分錄仍借貸平衡()
        {
            // 這支驗的是**所有其他測試過的帳**：任何一條流程產生不平衡的分錄，這裡就會紅。
            var period = LocalToday[..7].Replace("-", "");
            var page = await OpenAsync($"/TrialBalance?fromPeriod={period}&toPeriod={period}&searched=true");

            var danger = page.Locator(".alert-danger");
            Assert.True(await danger.CountAsync() == 0,
                $"期間 {period} 試算表借貸不平衡：{(await danger.CountAsync() > 0 ? await danger.First.InnerTextAsync() : "")}");
            Assert.True(await page.Locator(".alert-success").CountAsync() > 0, $"期間 {period} 試算表沒有結果。");
        }

        private static async Task<IReadOnlyList<string>> RowCellsAsync(IPage page, string rowFirstCell)
        {
            var row = page.Locator("table tbody tr").Filter(new LocatorFilterOptions
            {
                Has = page.Locator($"td:first-child:text-is('{rowFirstCell}')"),
            });
            Assert.True(await row.CountAsync() == 1, $"報表上找不到「{rowFirstCell}」那一列。");
            return await row.Locator("td").AllInnerTextsAsync();
        }

        // ── 批次：執行 → 輪詢資料庫 → 重新查詢畫面 ───────────────────────────

        private static async Task RunBatchAsync(IPage page, string action, string resultSql)
        {
            var errors = new List<string>();
            await page.ClickAsync($"[data-action='{action}']");

            for (var waited = 0; waited < 15_000; waited += 250)
            {
                if (await SeedFixture.ScalarAsync<long>(resultSql) > 0) return;
                if (await page.Locator("#errorToast.show").CountAsync() > 0)
                    Assert.Fail($"{action} 失敗：{await page.InnerTextAsync("#errorMsg")}");
                await Task.Delay(250);
            }
            Assert.Fail($"{action} 按下後 15 秒內資料庫沒有結果。");
        }

        [Fact]
        public async Task NRV_執行批次後減損品的淨變現價值與減損金額正確()
        {
            var page = await OpenAsync($"/NrvAssessment?searchPeriod={E2EPeriod}&searched=true");
            await RunBatchAsync(page, "runNrv",
                $"""SELECT count(*) FROM "InventoryNrvAssessments" WHERE "PeriodId"='{E2EPeriod}' AND "ProductCode"='E2E-NRV01'""");

            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

            // 欄位：期間、品號、數量、帳面成本、售價、銷售費用、NRV、減損。
            // 售價 80、銷售費用 80×2% = 1.6、NRV 78.4、減損 (100 − 78.4) × 10 = 216。
            var row = page.Locator("table tbody tr").Filter(new LocatorFilterOptions { HasText = "E2E-NRV01" });
            Assert.True(await row.CountAsync() == 1, "NRV 結果沒有 E2E-NRV01 那一列（或不只一列）。");
            var c = await row.Locator("td").AllInnerTextsAsync();
            Assert.Equal(10m,   Money(c[2]));
            Assert.Equal(100m,  Money(c[3]));
            Assert.Equal(80m,   Money(c[4]));
            Assert.Equal(1.6m,  Money(c[5]));
            Assert.Equal(78.4m, Money(c[6]));
            Assert.Equal(216m,  Money(c[7]));
        }

        /// <summary>
        /// ECL 彙總**全廠**未結 AR，其他測試開的發票也會進來，無法寫死總額。
        /// 所以用資料庫當對照：以**本幣**餘額（AmountLocal − 已收 × 匯率，與帳齡表同一口徑）依到期日分區，
        /// 與 ECL 存下的各區餘額逐區比對；再驗種子那 14000 確實都落在該在的區間裡。
        /// 對照用本幣而不是照抄 ECL 的算法，是刻意的——兩者對不上，就是 ECL 口徑錯了。
        /// </summary>
        [Fact]
        public async Task ECL_執行批次後各帳齡區間餘額等於未結應收本幣餘額()
        {
            Assert.True(await SeedFixture.ScalarAsync<long>("""SELECT count(*) FROM "EclLossRates" """) == 0,
                "測試庫設了自訂 ECL 損失率，本測試依預設五區間撰寫，請改寫或清掉設定。");

            var page = await OpenAsync($"/Ecl?searchPeriod={E2EPeriod}&searched=true");
            await RunBatchAsync(page, "runEcl",
                $"""SELECT count(*) FROM "EclAllowances" WHERE "PeriodId"='{E2EPeriod}' AND "PlantId"='P1'""");

            var today = LocalToday;
            foreach (var (bucket, lo, hi, rate, seeded) in new (string, int, int, decimal, decimal)[]
            {
                ("Current", int.MinValue, 0, 0.005m, 1000m),
                ("1-30",    1,  30, 0.02m, 2000m),
                ("31-60",   31, 60, 0.05m, 2000m),
                ("61-90",   61, 90, 0.10m, 4000m),
                ("90+",     91, int.MaxValue, 0.30m, 5000m),
            })
            {
                var range = $"""('{today}'::date - "DueDate") BETWEEN {lo} AND {hi}""";
                var expected = await SeedFixture.ScalarAsync<decimal>($"""
                    SELECT COALESCE(SUM("AmountLocal" - "ReceivedAmount" * "ExchangeRate"), 0) FROM "ARInvoices"
                    WHERE "PlantId"='P1' AND "Status" NOT IN (2,3) AND {range}
                    """);
                var mine = await SeedFixture.ScalarAsync<decimal>($"""
                    SELECT COALESCE(SUM("AmountLocal" - "ReceivedAmount" * "ExchangeRate"), 0) FROM "ARInvoices"
                    WHERE "PlantId"='P1' AND "Status" NOT IN (2,3) AND "CustomerCode"='E2E-AGC' AND {range}
                    """);
                Assert.True(mine == seeded, $"種子前提不成立：E2E-AGC 在 {bucket} 應為 {seeded}，資料庫是 {mine}。");

                var actual = await SeedFixture.ScalarAsync<decimal>($"""
                    SELECT COALESCE(SUM("ArBalance"),0) FROM "EclAllowances"
                    WHERE "PeriodId"='{E2EPeriod}' AND "PlantId"='P1' AND "AgingBucket"='{bucket}'
                    """);
                var ecl = await SeedFixture.ScalarAsync<decimal>($"""
                    SELECT COALESCE(SUM("EclAmount"),0) FROM "EclAllowances"
                    WHERE "PeriodId"='{E2EPeriod}' AND "PlantId"='P1' AND "AgingBucket"='{bucket}'
                    """);
                Assert.True(actual == expected, $"ECL {bucket} 區餘額 {actual}，未結應收本幣餘額是 {expected}。");
                Assert.Equal(Math.Round(expected * rate, 4), ecl);
            }

            // 畫面：重新查詢後合計列要等於資料庫合計。
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var total = await SeedFixture.ScalarAsync<decimal>($"""
                SELECT COALESCE(SUM("EclAmount"),0) FROM "EclAllowances" WHERE "PeriodId"='{E2EPeriod}' AND "PlantId"='P1'
                """);
            var shown = Money(await page.Locator("table tfoot td").Last.InnerTextAsync());
            // 畫面是 decimal.ToString(格式)，中點一律遠離零；Math.Round 預設是銀行家捨入（取偶）。
            // 合計落在 .xx5 時兩者差一分（實例：2054.525 → 畫面 2054.53、取偶 2054.52），要用同一規則比。
            Assert.Equal(Math.Round(total, 2, MidpointRounding.AwayFromZero), Math.Round(shown, 2));
        }
    }
}
