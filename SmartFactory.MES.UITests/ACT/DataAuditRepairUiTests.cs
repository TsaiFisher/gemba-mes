using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.ACT
{
    /// <summary>
    /// 資料自癒走畫面：採購明細已收數量偏離來源進貨單 → 資料稽核畫面列出 → 勾選、確認 → 依來源單據重算寫回。
    /// 服務本身由整合測試 DerivedFieldRepairIntegrationTests 驗；這裡驗的是畫面接線（勾選送出的鍵、確認框、AntiForgery、重新整理後消失）。
    /// </summary>
    [Collection("UI Tests")]
    public class DataAuditRepairUiTests
    {
        private const string Po = "E2E-RPRPO1";
        private readonly UiTestFixture _fixture;
        public DataAuditRepairUiTests(UiTestFixture fixture) => _fixture = fixture;

        private static Task CleanAsync() => SeedFixture.ExecAsync($"""
            DELETE FROM "PurchaseOrderLines" WHERE "PurchaseOrderId"='{Po}';
            DELETE FROM "PurchaseOrders" WHERE "PurchaseOrderId"='{Po}';
            """);

        [Fact]
        public async Task 已收數量偏離_資料稽核畫面勾選修正_依來源單據重算_卡片消失()
        {
            await CleanAsync();
            // 帳上記已收 5，但沒有任何進貨單指向它 → 應為 0
            await SeedFixture.ExecAsync($"""
                INSERT INTO "PurchaseOrders" ("PurchaseOrderId","OrderDate","CreatedBy","SupplierCode","TotalAmount","CreatedAt","UpdatedAt","PlantId","CreatedByRoleId","ApprovalStatus")
                VALUES ('{Po}', current_date, 'admin', 'E2E-CO01', 0, now(), now(), 'P1', '1', 2);
                INSERT INTO "PurchaseOrderLines" ("PurchaseOrderId","LineNo","ProductCode","OrderQty","StockQty","UnitPrice","ExchangeRate","TaxRate","LineAmount","TaxAmount","TotalAmount","IsClosed","ReceivedQty")
                VALUES ('{Po}', 1, 'E2E-RM01', 10, 10, 1, 1, 0, 10, 0, 10, false, 5);
                """);
            try
            {
                var page = await _fixture.NewPageAsync();
                await page.GotoAsync($"{_fixture.BaseUrl}/DataAudit");
                var row = page.Locator("#repairTable tbody tr", new() { HasText = Po });
                await Assertions.Expect(row).ToHaveCountAsync(1, new() { Timeout = 15_000 });
                await Assertions.Expect(row).ToContainTextAsync("5");

                string? confirmText = null;
                page.Dialog += async (_, d) => { confirmText = d.Message; await d.AcceptAsync(); };
                await row.Locator("input.repair-key").CheckAsync();
                await page.ClickAsync("#repairBtn");

                // 成功後頁面重新整理，這一列不再出現
                await Assertions.Expect(page.Locator("#repairTable tbody tr", new() { HasText = Po })).ToHaveCountAsync(0, new() { Timeout = 15_000 });
                Assert.False(string.IsNullOrWhiteSpace(confirmText), "送出前要先跳確認框");
                Assert.Equal("0", await SeedFixture.ScalarAsync<string>(
                    $"""SELECT "ReceivedQty"::numeric(18,0)::text FROM "PurchaseOrderLines" WHERE "PurchaseOrderId"='{Po}'"""));
            }
            finally
            {
                await CleanAsync();
            }
        }
    }
}
