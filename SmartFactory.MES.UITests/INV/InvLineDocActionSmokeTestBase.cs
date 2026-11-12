using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// INV「表頭＋動態明細列」單據的 <see cref="DocActionSmokeTestBase"/> 特化。
    ///
    /// 這一系列單據（報廢／調撥／借出／委外領料／重工領料／盤盈虧／樣品／跨廠調撥）
    /// 共用同一份 scaffold：<c>#linesTbody</c> + <c>data-action="addLine"</c> +
    /// <c>select.product-select</c> + <c>Lines[i].Qty</c>。填表邏輯集中在這裡，
    /// 各單據只宣告自己的路徑與表單 id。
    ///
    /// ⚠️ 共用 scaffold **不等於**共用 handler：每個畫面的 script 區塊是各自寫的，
    /// 少掛一個 window.xxx 只會在那一個畫面壞掉。所以仍然逐個單據都要有一支測試，
    /// 不能驗一個就當全家族都好。
    /// </summary>
    public abstract class InvLineDocActionSmokeTestBase : DocActionSmokeTestBase
    {
        protected InvLineDocActionSmokeTestBase(UiTestFixture fixture) : base(fixture) { }

        /// <summary>
        /// 表頭是否有必填的「往來對象」（借出單、樣品單有）。
        /// 乾淨資料庫一筆公司主檔都沒有，由種子的 E2E-CO01 提供。
        /// </summary>
        protected virtual bool RequiresEntityCode => false;

        /// <summary>明細用的品號。種子在 E2E-LRAW 備有庫存。</summary>
        protected virtual string LineProductCode => "E2E-RM01";

        /// <summary>
        /// 明細列的來源倉／儲位。種子把 E2E-RM01 的 1000 顆全放在 E2E-WRAW／E2E-LRAW，
        /// 這是全庫唯一有這顆料的儲位。
        ///
        /// ⚠️ 不可留空：報廢單、調撥單存檔即自動核准並過帳（乾淨庫沒有簽核範本），
        /// InventoryPostingService 是逐「儲位」查可用量的——儲位空字串 = 可用量 0，
        /// 過帳被擋 → 面板停在原地 → 列表當然開不出單。畫面本身不會自動幫你挑儲位
        /// （onProductChange 只把有庫存的儲位排到選單最前面），真人也得自己選。
        /// 2026-08-01 nightly 的 8 個失敗（ScrapDoc／StockTransfer 各 4）就是這個缺口。
        /// </summary>
        protected virtual string StockWhCode       => "E2E-WRAW";
        protected virtual string StockLocationCode => "E2E-LRAW";

        /// <summary>調撥類單據的目的倉／儲位（不可與來源同一個）。</summary>
        protected virtual string TargetWhCode       => "E2E-WSTG";
        protected virtual string TargetLocationCode => "E2E-LSTG";

        /// <summary>
        /// 這一族單據要送出明細就得有可用庫存。流程測試會吃掉 E2E-RM01，且測試順序不固定，
        /// 故每次建單前把種子重跑一次，確保庫存回到基準值（種子冪等，只影響 E2E- 那組）。
        /// </summary>
        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            if (RequiresEntityCode)
            {
                await page.SelectOptionAsync($"{PanelBodySel} #EntityCodeSelect", "E2E-CO01");
                await page.WaitForTimeoutAsync(500);   // onEntityCodeChange 可能連帶帶出其他欄位
            }

            await FillHeaderExtrasAsync(page);

            await page.ClickAsync($"{PanelBodySel} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);

            var lastRow = page.Locator($"{PanelBodySel} #linesTbody tr").Last;
            await lastRow.Locator("select.product-select").SelectOptionAsync(LineProductCode);
            // onProductChange 內含 fetch（帶出倉庫／儲位／UOM），要等回來才填數量
            await page.WaitForTimeoutAsync(1000);

            // 2026-10-12：選了品號，單位下拉只留可用單位（uom-step.js），且預選庫存單位。
            // 判準直接取同一支 API——不在測試裡另抄一份單位清單，種子改了也不會假紅／假綠。
            var uomSel = lastRow.Locator("select[name$='.UomCode']");
            if (await uomSel.CountAsync() > 0)
            {
                var info = await page.EvaluateAsync<System.Text.Json.JsonElement>(
                    "async c => (await fetch('/api/uom/product/' + encodeURIComponent(c))).json()", LineProductCode);
                var expected = info.GetProperty("uoms").EnumerateArray().Select(e => e.GetString()!).OrderBy(x => x).ToList();
                var actual = (await uomSel.First.Locator("option").EvaluateAllAsync<string[]>("os => os.map(o => o.value)"))
                    .OrderBy(x => x).ToList();
                Assert.Equal(expected, actual);
                Assert.Equal(info.GetProperty("stockUom").GetString(), await uomSel.First.InputValueAsync());
            }

            var qty = lastRow.Locator("input[name$='.Qty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");

            // 倉別先於儲位：onWhChange 會依倉別重建儲位選項並清掉原值，順序顛倒等於白選。
            // 沒有這些欄位的單據（借出／樣品／委外領料／重工領料走表頭倉別）自然略過。
            await SelectLineOptionAsync(page, lastRow, ".WhCode",           StockWhCode);
            await SelectLineOptionAsync(page, lastRow, ".LocationCode",     StockLocationCode);
            await SelectLineOptionAsync(page, lastRow, ".FromWhCode",       StockWhCode);
            await SelectLineOptionAsync(page, lastRow, ".FromLocationCode", StockLocationCode);
            await SelectLineOptionAsync(page, lastRow, ".ToWhCode",         TargetWhCode);
            await SelectLineOptionAsync(page, lastRow, ".ToLocationCode",   TargetLocationCode);
        }

        /// <summary>
        /// 選明細列上某個下拉的指定值；該下拉不存在、或沒有這個選項時靜靜略過。
        ///
        /// 用 <c>name$=</c> 後綴比對即可區分同族欄位：<c>Lines[0].FromWhCode</c> 不會被
        /// <c>[name$='.WhCode']</c> 命中（前一個字元是 m 不是點），From／To 兩組因此各自對號入座。
        /// </summary>
        private static async Task SelectLineOptionAsync(IPage page, ILocator row, string nameSuffix, string value)
        {
            var sel = row.Locator($"select[name$='{nameSuffix}']").First;
            if (await sel.CountAsync() == 0) return;
            if (await sel.Locator($"option[value='{value}']").CountAsync() == 0) return;

            await sel.SelectOptionAsync(value);
            await page.WaitForTimeoutAsync(300);   // onWhChange／onLocChange 會連動重建選項
        }

        /// <summary>各單據表頭若還有其他必填欄位，覆寫這裡補。</summary>
        protected virtual Task FillHeaderExtrasAsync(IPage page) => Task.CompletedTask;
    }
}
