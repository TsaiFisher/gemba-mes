using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.ACT
{
    /// <summary>
    /// 月結的批次按鈕與分支：步驟 4 實際費率重估（串「部門實際費用」畫面）、步驟 6 外幣重評與 ECL、
    /// 步驟 7 NRV、跳過步驟、步驟 8 之後取消月結、步驟 9 期末評價入總帳。
    ///
    /// <para><see cref="PeriodCloseUiTests"/> 走的是「每一步直接按完成」的關帳主線，
    /// 這些批次按鈕與分支從沒被按過。只動種子 ㉑㉒ 的 2090-01 與 E2E 專屬資料；
    /// 預期值全部由種子 ㉒ 的設計推得（見該段註解），結束時期間一律還原 Open。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MonthEndBatchUiTests
    {
        private readonly UiTestFixture _fixture;
        public MonthEndBatchUiTests(UiTestFixture fixture) => _fixture = fixture;

        private const string Period    = "209001";
        private const string Checklist = "209001-P1";

        private static Task<string?> Scalar(string sql) => SeedFixture.ScalarAsync<string>(sql);

        private static Task<string?> StepStatusAsync(int step) => Scalar(
            $"""SELECT "Status"::text FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}' AND "StepNo"={step}""");

        private static Task<string?> PeriodStatusAsync() => Scalar(
            $"""SELECT "Status"::text FROM "AccountingPeriods" WHERE "PeriodId"='{Period}' AND "PlantId"='P1'""");

        private async Task OpenMonthEndAsync(IPage page) =>
            await page.GotoAsync($"{_fixture.BaseUrl}/MonthEnd?periodId={Period}",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        /// <summary>按下後輪詢直到 SQL 回傳非零、或紅色錯誤 toast 出現。</summary>
        private static async Task WaitDbAsync(IPage page, string countSql, string what)
        {
            for (var waited = 0; waited < 15_000; waited += 250)
            {
                if (await SeedFixture.ScalarAsync<long>(countSql) > 0) return;
                if (await page.Locator("#errorToast.show").CountAsync() > 0)
                    Assert.Fail($"{what} 失敗：{await page.InnerTextAsync("#errorMsg")}");
                await Task.Delay(250);
            }
            Assert.Fail($"{what}：15 秒內資料庫沒有結果。");
        }

        private async Task CompleteStepAsync(IPage page, int step, string action = "completeStep", int expected = 2)
        {
            await page.ClickAsync($"button[data-action='{action}'][data-step-no='{step}']");
            await WaitDbAsync(page,
                $"""SELECT count(*) FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}' AND "StepNo"={step} AND "Status"={expected}""",
                $"月結步驟 {step}（{action}）");
            await OpenMonthEndAsync(page);
        }

        [Fact]
        public async Task 部門費用串到步驟4重估_外幣重評_NRV_跳過_取消月結_步驟9入帳()
        {
            await SeedFixture.ResetAsync();
            try
            {
                var page = await _fixture.NewPageAsync();
                await _fixture.SignInAsync(page);

                // ① 在「部門實際費用」畫面把 2090-01 E2E-WC01 從種子的 300／150 改成人工 1000、製費 600
                await page.GotoAsync($"{_fixture.BaseUrl}/DepartmentActualCost?searched=true&periodId={Period}&workCenterCode=E2E-WC01",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                await page.Locator("[data-url*='/DepartmentActualCost/Edit']").First.ClickAsync();
                await page.WaitForSelectorAsync("#sidePanelBody input[name='ActualLaborCost']");
                await page.FillAsync("#sidePanelBody input[name='ActualLaborCost']", "1000");
                await page.FillAsync("#sidePanelBody input[name='ActualOverheadCost']", "600");
                await page.ClickAsync("#sidePanelBody button[type='submit']");
                await WaitDbAsync(page,
                    $"""SELECT count(*) FROM "DepartmentActualCosts" WHERE "PeriodId"='{Period}' AND "WorkCenterCode"='E2E-WC01' AND "ActualLaborCost"=1000 AND "ActualOverheadCost"=600""",
                    "部門實際費用修改存檔");

                // ② 初始化月結清單，完成 1～3
                await OpenMonthEndAsync(page);
                await page.ClickAsync("[data-action='initChecklist']");
                await WaitDbAsync(page, $"""SELECT count(*) FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}'""", "初始化");
                await OpenMonthEndAsync(page);
                for (var s = 1; s <= 3; s++) await CompleteStepAsync(page, s);

                // ③ 步驟 4：輸入框必須帶入畫面存的費用，確認送出即重估
                await page.ClickAsync("button[data-action='runActualRateReest'][data-step-no='4']");
                await Assertions.Expect(page.Locator("#deptCostModal.show")).ToBeVisibleAsync();
                Assert.Equal("E2E-WC01:1000:600", (await page.InputValueAsync("#deptCostInput")).Trim());
                await page.ClickAsync("#btnConfirmDeptCost");
                await WaitDbAsync(page,
                    """SELECT count(*) FROM "WorkOrderActualCosts" WHERE "WorkOrderId"='E2E-WO9001' AND "Status"=1""",
                    "步驟 4 實際費率重估");

                // 實際人工率 1000÷2h=500、製費率 600÷(2h報工+1h換線)=200 → 人工 1000、製費 600
                Assert.Equal("1000.0000/600.0000", await Scalar(
                    """SELECT "ActualLabor"::text || '/' || "ActualOverhead"::text FROM "WorkOrderActualCosts" WHERE "WorkOrderId"='E2E-WO9001'"""));
                // 調整分錄逐工單拆行：貸 5110 700、貸 5120 450、借 5130 1150
                Assert.Equal("5110:-700.0000|5120:-450.0000|5130:1150.0000", await Scalar("""
                    SELECT string_agg(l."AccountCode" || ':' || (l."Debit" - l."Credit")::text, '|' ORDER BY l."AccountCode")
                    FROM "JournalLines" l JOIN "JournalEntries" e ON e."JournalId" = l."JournalId"
                    WHERE e."SourceDocType"='ActualRateReest' AND e."PeriodId"='209001' AND l."SourceRef"='E2E-WO9001'
                    """));
                await OpenMonthEndAsync(page);
                await CompleteStepAsync(page, 4);

                // ④ 步驟 5：跳過（狀態 Skipped=3），跳過也算完成、後面的步驟可以繼續
                await CompleteStepAsync(page, 5, action: "skipStep", expected: 3);

                // ⑤ 步驟 6：外幣重評，期末匯率 E2E-CUR:32
                //    AR 未結 60 → 1800 → 1920（利益 +120）；AP 未結 150 → 4500 → 4800（應付變大＝損失 −300）。
                await page.ClickAsync("button[data-action='runFxRevaluation'][data-step-no='6']");
                await Assertions.Expect(page.Locator("#fxRatesModal.show")).ToBeVisibleAsync();
                await page.FillAsync("#fxRatesInput", "E2E-CUR:32");
                await page.ClickAsync("#btnConfirmFx");
                await WaitDbAsync(page,
                    $"""SELECT count(*) FROM "FxRevaluationEntries" WHERE "PeriodId"='{Period}' AND "DocId" IN ('E2E-FXR1','E2E-FXP1')""",
                    "步驟 6 外幣重評");
                // 格式：單號:未結外幣:重評後本幣:差額
                Assert.Equal("E2E-FXP1:150.0000:4800.0000:-300.0000|E2E-FXR1:60.0000:1920.0000:120.0000",
                    await Scalar($"""
                        SELECT string_agg("DocId" || ':' || "OutstandingAmount"::text || ':' || "RevaluedAmountLocal"::text || ':' || "ExchangeGainLoss"::text, '|' ORDER BY "DocId")
                        FROM "FxRevaluationEntries" WHERE "PeriodId"='{Period}' AND "DocId" IN ('E2E-FXR1','E2E-FXP1')
                        """));
                // 同一步的 ECL 批次（基準日＝期末）
                await page.ClickAsync("button[data-action='runEclBatch'][data-step-no='6']");
                await WaitDbAsync(page,
                    $"""SELECT count(*) FROM "EclAllowances" WHERE "PeriodId"='{Period}' AND "PlantId"='P1'""",
                    "步驟 6 ECL");
                await OpenMonthEndAsync(page);
                await CompleteStepAsync(page, 6);

                // ⑥ 步驟 7：NRV 批次（與 NRV 畫面同一支服務；E2E-NRV01 減損 216）
                await page.ClickAsync("button[data-action='runNrvBatch'][data-step-no='7']");
                await WaitDbAsync(page,
                    $"""SELECT count(*) FROM "InventoryNrvAssessments" WHERE "PeriodId"='{Period}' AND "ProductCode"='E2E-NRV01'""",
                    "步驟 7 NRV 批次");
                Assert.Equal("216.0000", await Scalar(
                    $"""SELECT "ImpairmentLoss"::text FROM "InventoryNrvAssessments" WHERE "PeriodId"='{Period}' AND "ProductCode"='E2E-NRV01'"""));
                await OpenMonthEndAsync(page);
                await CompleteStepAsync(page, 7);

                // ⑦ 步驟 8 封鎖 → Closing；取消月結 → 期間回 Open、步驟 8 回 Pending
                await CompleteStepAsync(page, 8);
                Assert.Equal("1", await PeriodStatusAsync());
                await page.ClickAsync("button[data-action='cancelClosing']");
                for (var waited = 0; waited < 15_000 && await PeriodStatusAsync() != "0"; waited += 250)
                {
                    if (await page.Locator("#errorToast.show").CountAsync() > 0)
                        Assert.Fail($"取消月結失敗：{await page.InnerTextAsync("#errorMsg")}");
                    await Task.Delay(250);
                }
                Assert.Equal("0", await PeriodStatusAsync());
                Assert.Equal("0", await StepStatusAsync(8));

                // ⑧ 重新封鎖，步驟 9 完成時把外幣重評／NRV／ECL 入總帳
                await OpenMonthEndAsync(page);
                await CompleteStepAsync(page, 8);
                await CompleteStepAsync(page, 9);

                // 外幣：逐張拆行；利益借控制科目、損失貸控制科目
                Assert.Equal("1170:120.0000|2100:-300.0000|4190:-120.0000|5199:300.0000", await Scalar($"""
                    SELECT string_agg(code || ':' || net::text, '|' ORDER BY code) FROM (
                      SELECT l."AccountCode" code, sum(l."Debit" - l."Credit") net
                      FROM "JournalLines" l JOIN "JournalEntries" e ON e."JournalId" = l."JournalId"
                      WHERE e."SourceDocType"='FxRevaluation' AND e."SourceDocId"='{Period}' AND e."EntryDate"='2090-01-31'
                        AND l."SourceRef" IN ('E2E-FXR1','E2E-FXP1')
                      GROUP BY l."AccountCode") x
                    """));
                // 次期初迴轉：同金額反向，日期 2090-02-01
                Assert.Equal("0", await Scalar($"""
                    SELECT coalesce(sum(l."Debit" - l."Credit") FILTER (WHERE e."SourceDocType"='FxRevaluation')
                                  + sum(l."Debit" - l."Credit") FILTER (WHERE e."SourceDocType"='FxRevaluationReversal' AND e."EntryDate"='2090-02-01'), -1)::int::text
                    FROM "JournalLines" l JOIN "JournalEntries" e ON e."JournalId" = l."JournalId"
                    WHERE e."SourceDocId"='{Period}' AND l."AccountCode"='1170'
                    """));
                // NRV／ECL：備抵等於評估合計（seed 清過 2090-01 的分錄，調整前備抵為 0）
                Assert.Equal("true", await Scalar($"""
                    SELECT ((SELECT sum("Debit") FROM "JournalLines" l JOIN "JournalEntries" e ON e."JournalId"=l."JournalId"
                             WHERE e."SourceDocType"='NrvWriteDown' AND e."SourceDocId"='{Period}' AND l."AccountCode"='5240')
                          = (SELECT sum("ImpairmentLoss") FROM "InventoryNrvAssessments" WHERE "PeriodId"='{Period}' AND "PlantId"='P1'))::text
                    """));
                Assert.Equal("true", await Scalar($"""
                    SELECT (coalesce((SELECT sum("Debit") FROM "JournalLines" l JOIN "JournalEntries" e ON e."JournalId"=l."JournalId"
                             WHERE e."SourceDocType"='EclAllowance' AND e."SourceDocId"='{Period}' AND l."AccountCode"='6250'), 0)
                          = (SELECT sum("EclAmount") FROM "EclAllowances" WHERE "PeriodId"='{Period}' AND "PlantId"='P1'))::text
                    """));
                Assert.Contains("[[Note_FxPosted|", await Scalar(
                    $"""SELECT "Notes" FROM "ChecklistItems" WHERE "ChecklistId"='{Checklist}' AND "StepNo"=9"""));
            }
            finally
            {
                await SeedFixture.ScalarAsync<long>(
                    $"""UPDATE "AccountingPeriods" SET "Status"=0, "ClosedAt"=NULL, "ClosedBy"=NULL WHERE "PeriodId"='{Period}' AND "PlantId"='P1' RETURNING 1""");
            }
        }
    }
}
