using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests
{
    /// <summary>
    /// 「從XX單匯入」類按鈕的 E2E 守門，涵蓋全站所有含匯入功能的 _Form：
    /// PurchaseOrder(從請購單)、GoodsReceipt(從採購單)、GoodsReturn(從進貨單)、
    /// MrpRun(從訂單)、GoodsShipment(從銷售訂單)、SalesReturn(從出貨單)。
    ///
    /// 背景（2026-07-21 事故）：PurchaseOrder/_Form.cshtml 的 onSupplierChange(e, sel)
    /// 函式內又宣告了一次 `const sel`，與參數同名 → 整段 inline &lt;script&gt; parse 失敗，
    /// 該 _Form 內所有 data-action 綁定的函式（含 openPrModal）全部沒有掛到 window 上，
    /// 按鈕變成點了沒反應。這類 bug 是純 JS 語法錯誤，dotnet build/單元測試/整合測試
    /// 都看不到（它們不執行瀏覽器 JS），只有讓真瀏覽器解析並執行這段 script 才抓得到，
    /// 故以此 E2E 測試為唯一防線。
    /// </summary>
    [Collection("UI Tests")]
    public class ImportModalFormInteractionTests
    {
        private readonly UiTestFixture _fixture;
        public ImportModalFormInteractionTests(UiTestFixture fixture) => _fixture = fixture;

        public record ImportScreen(
            string Name, string IndexPath, string NewButtonSelector, string PanelReadySel,
            string? GateFieldName, string ImportAction, string ModalId);

        public static IEnumerable<object[]> Screens => new List<object[]>
        {
            new object[] { new ImportScreen("PurchaseOrder",
                "/PurchaseOrder", "button[data-url$='/PurchaseOrder/Create']", "#poForm",
                null, "openPrModal", "prImportModal") },
            new object[] { new ImportScreen("GoodsReceipt",
                "/GoodsReceipt", "button[data-url$='/GoodsReceipt/Create']", "#grForm",
                "SupplierCode", "openPoModal", "poImportModal") },
            new object[] { new ImportScreen("GoodsReturn",
                "/GoodsReturn", "button[data-url$='/GoodsReturn/Create']", "#grtForm",
                "SupplierCode", "openGrModal", "grImportModal") },
            new object[] { new ImportScreen("MrpRun",
                "/MrpRun", "button[data-url$='/MrpRun/Create']", "#mrpRunForm",
                null, "openImportModal", "importOrdersModal") },
            new object[] { new ImportScreen("GoodsShipment",
                "/GoodsShipment", "button[data-url$='/GoodsShipment/Create']", "#gsForm",
                "CompanyCode", "importFromSO", "soImportModal") },
            new object[] { new ImportScreen("SalesReturn",
                "/SalesReturn", "button[data-url$='/SalesReturn/Create']", "#grForm",
                "CompanyCode", "importFromGS", "gsImportModal") },
        };

        // ── 匯入按鈕：點擊後對應 Modal 開啟，且面板全程無 JS error ─────────────────
        [Theory]
        [MemberData(nameof(Screens))]
        public async Task ImportButton_OpensModal_NoJsError(ImportScreen s)
        {
            var page = await _fixture.NewPageAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            await _fixture.SignInAsync(page);

            var form = new DocFormPage(page, _fixture.BaseUrl);
            await form.GotoIndexAsync(s.IndexPath);
            await form.OpenNewPanelAsync(s.NewButtonSelector, s.PanelReadySel);

            // 需先選定往來對象（供應商/客戶）者，缺種子資料時跳過本輪斷言（不誤紅）。
            if (s.GateFieldName != null)
            {
                var hasOption = await form.SelectFirstOptionAsync(s.GateFieldName);
                if (!hasOption) return;
            }

            var opened = await form.OpenImportModalAsync(s.ImportAction, s.ModalId);

            Assert.True(opened,
                $"[{s.Name}] 點擊 data-action=\"{s.ImportAction}\" 後 #{s.ModalId} 未在時限內顯示" +
                "（面板 inline script 可能語法錯誤導致全域函式未掛載，或 data-action 委派失聯）。");
            Assert.Empty(jsErrors);
        }
    }
}
