using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 5-9 列印：**版面只能人眼看，金額一致性可以斷言**（作業書 §14.6 的原話）。
    ///
    /// 這一格長期記著「尚未做」。做起來之後第一支就紅了，而且是在真實資料上驗得到的：
    ///
    /// 六張單據（銷貨訂單／出貨單／銷貨退回／採購單／進貨單／進貨退出）的表頭
    /// <c>TotalAmount</c> 一律是**本位幣**（模型註解逐字寫著「本位幣總金額（各明細
    /// AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計」），
    /// 而列印畫面的明細欄印的是 <c>l.LineAmount</c>／<c>l.TotalAmount</c>——**交易幣**。
    /// 最後一列卻把本幣總額擺在同一欄底下當「合計」，還用**第一行幣別**的小數位格式化。
    ///
    /// 本國幣單據上兩者相等，所以沒有人發現。外幣單據上：
    ///   dev 庫的 GS-2609-00012（USD，匯率 31.7）明細欄合計 210，
    ///   「合計」那一列印的是 6,657 —— 那是新台幣，而且沒有任何地方標示幣別。
    /// 等於告訴美國客戶他欠 6,657。
    ///
    /// 沒有任何既有檢查會叫：兩個數字各自都是對的（帳上就是這麼記的），
    /// 錯的是「把它們放在同一欄」這個版面決定，而版面沒有任何自動檢查。
    ///
    /// 斷言方式刻意是**解析真的產出的 HTML**，不是讀模型欄位：
    /// 這一格要驗的就是「印出來的紙上，合計等不等於上面那一欄的加總」。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class PrintAmountConsistencyTests : PostingResidueTestBase
    {
        public PrintAmountConsistencyTests(AuthedScheduleFactory factory) : base(factory) { }

        private const string Ccy      = "USD";
        private const decimal Rate    = 31.7m;
        private const decimal Qty     = 3m;
        private const decimal Price   = 70m;
        private const string Customer = "PRTC-CUS";
        private const string Uom      = "PRTC-EA";
        private const string Product  = "PRTC-P01";
        private const string Category = "PRTC-CAT";
        private const string Doc      = "PRTC-GS-01";

        private static readonly DateOnly ShipDate = new(2097, 6, 15);

        /// <summary>
        /// 外幣出貨單的列印：明細欄的合計，必須等於「合計」那一列印出來的數字。
        ///
        /// 失效形狀是**印出來的紙上有兩個幣別混在同一欄**，而客戶看不出來——
        /// 系統裡每一個數字都是對的，錯的是版面。
        /// </summary>
        [Fact]
        public async Task ForeignCurrencyShipment_PrintedTotal_EqualsThePrintedColumn()
        {
            await SeedForeignCurrencyShipmentAsync();

            var (client, _) = await NewAuthedClientAsync();
            var html = await client.GetStringAsync($"/GoodsShipment/Print/{Doc}");

            var lineAmounts = AmountsInColumn(html, "tbody").Select(x => x.Amount).ToList();
            var footRows    = AmountsInColumn(html, "tfoot");

            Assert.NotEmpty(lineAmounts);
            Assert.Equal(Qty * Price, lineAmounts.Sum());   // 交易幣：3 × 70 = 210

            // 掛著交易幣別的那一列，金額必須等於上面那一欄的加總
            var txnRow = Assert.Single(footRows.Where(r => r.Label.Contains(Ccy)));
            Assert.Equal(lineAmounts.Sum(), txnRow.Amount);

            // 本幣總額仍要印，但是**另外一列、另外標示**，不能混在交易幣那一欄底下當合計
            var localRow = Assert.Single(footRows.Where(r => !r.Label.Contains(Ccy)));
            Assert.Equal(Qty * Price * Rate, localRow.Amount);
            Assert.NotEqual(txnRow.Amount, localRow.Amount);
        }

        /// <summary>
        /// 合計旁邊必須看得到幣別。
        ///
        /// 就算金額對了，一張沒有幣別的外幣單據照樣是錯的——收到的人只會照本國幣理解。
        /// 明細列有「幣別」欄，合計列原本什麼都沒有。
        /// </summary>
        [Fact]
        public async Task ForeignCurrencyShipment_PrintedTotal_ShowsTheCurrency()
        {
            await SeedForeignCurrencyShipmentAsync();

            var (client, _) = await NewAuthedClientAsync();
            var html = await client.GetStringAsync($"/GoodsShipment/Print/{Doc}");

            var foot = Regex.Match(html, "<tfoot>(.*?)</tfoot>", RegexOptions.Singleline).Groups[1].Value;
            Assert.Contains(Ccy, foot);
        }

        /// <summary>
        /// PUR 三張單（採購單／進貨單／進貨退出）的版面不一樣——合計印在表格**外面**的
        /// 一段文字裡，不是 tfoot。同一個缺陷、同一個修法，但改的是文字輸出，
        /// 回歸起來完全無聲，故另外釘一支。
        /// </summary>
        [Fact]
        public async Task ForeignCurrencyReceipt_PrintedTotals_AreLabelledByCurrency()
        {
            await SeedForeignCurrencyReceiptAsync();

            var (client, _) = await NewAuthedClientAsync();
            var html = await client.GetStringAsync($"/GoodsReceipt/Print/{GrDoc}");
            var text = Regex.Replace(html, "<.*?>", " ");

            // 交易幣合計：3 × 70 = 210，且掛著 USD
            Assert.Matches($@"{Ccy}\s*[：:]\s*210(\.00)?", text);
            // 本幣合計另立一行、標明本幣代碼；金額是 6,657（含千分位）
            Assert.Contains("6,657", text);
            // 而且兩個數字不會只出現一個——舊行為是只印本幣那一個
            Assert.Contains("210", text);
        }

        // ── 解析 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 取出某一段裡「靠右對齊的金額格」。刻意只認 <c>text-end</c> 的格子——
        /// 數量欄同樣靠右，故再以「有沒有千分位或兩位小數」無法區分，
        /// 改以**欄位位置**取：金額欄是列印表格裡最後一個 text-end 欄位。
        /// </summary>
        private static List<(string Label, decimal Amount)> AmountsInColumn(string html, string section)
        {
            var block = Regex.Match(html, $"<{section}>(.*?)</{section}>", RegexOptions.Singleline).Groups[1].Value;
            var rows  = Regex.Matches(block, "<tr>(.*?)</tr>", RegexOptions.Singleline);
            var result = new List<(string, decimal)>();
            foreach (Match row in rows)
            {
                var cells = Regex.Matches(row.Groups[1].Value,
                    "<td[^>]*class=\"[^\"]*text-end[^\"]*\"[^>]*>(.*?)</td>", RegexOptions.Singleline);
                if (cells.Count == 0) continue;
                var last = Regex.Replace(cells[^1].Groups[1].Value, "<.*?>", "").Trim();
                // 標籤取整列的純文字（扣掉金額本身），用來分辨「交易幣合計」與「本幣合計」
                var label = Regex.Replace(row.Groups[1].Value, "<.*?>", " ");
                label = label.Replace(last, " ").Replace("&nbsp;", " ").Trim();
                if (decimal.TryParse(last, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    result.Add((label, v));
            }
            return result;
        }

        // ── 佈景 ────────────────────────────────────────────────────────────

        private static async Task EnsurePrintMastersAsync(SmartFactory.MES.Data.ApplicationDbContext db)
        {
            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(u => u.UomCode == Uom))
                db.Uoms.Add(new Uom { UomCode = Uom, UomName = "個", DecimalPlaces = 4 });
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Category))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Category, CategoryName = "列印測試類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                });
            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Product))
                db.Products.Add(new Product
                {
                    ProductCode = Product, ProductName = "列印測試料", CategoryCode = Category,
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });
            await db.SaveChangesAsync();
        }


        private const string GrDoc    = "PRTC-GR-01";
        private const string Supplier = "PRTC-SUP";

        private async Task SeedForeignCurrencyReceiptAsync()
        {
            using var scope = NewScope(out var db);
            await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(d => d.GoodsReceiptId == GrDoc).ExecuteDeleteAsync();

            await EnsurePrintMastersAsync(db);
            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Supplier))
                db.Companies.Add(new Company
                {
                    CompanyCode = Supplier, CompanyName = "列印測試廠商", IsSupplier = true, IsActive = true,
                });
            await db.SaveChangesAsync();

            var doc = new SmartFactory.MES.Modules.PUR.Models.GoodsReceipt
            {
                GoodsReceiptId = GrDoc, ReceiptDate = ShipDate, SupplierCode = Supplier,
                PlantId = Plant, CreatedBy = "admin", CreatedByRoleId = RoleId,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                TotalAmount = Qty * Price * Rate,
            };
            doc.Lines.Add(new SmartFactory.MES.Modules.PUR.Models.GoodsReceiptLine
            {
                GoodsReceiptId = GrDoc, LineNo = 1, ProductCode = Product,
                ReceiptQty = Qty, StockQty = Qty, UomCode = Uom, StockUomCode = Uom,
                UnitPrice = Price, CurrencyCode = Ccy, ExchangeRate = Rate,
                LineAmount = Qty * Price, TaxRate = 0m, TaxAmount = 0m,
                TotalAmount = Qty * Price, AmountLocal = Qty * Price * Rate,
            });
            db.GoodsReceipts.Add(doc);
            await db.SaveChangesAsync();
        }

        private async Task SeedForeignCurrencyShipmentAsync()
        {
            using var scope = NewScope(out var db);

            await db.GoodsShipments.IgnoreQueryFilters()
                .Where(d => d.GoodsShipmentId == Doc).ExecuteDeleteAsync();

            await EnsurePrintMastersAsync(db);
            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Customer))
                db.Companies.Add(new Company
                {
                    CompanyCode = Customer, CompanyName = "列印測試客戶", IsCustomer = true, IsActive = true,
                });
            await db.SaveChangesAsync();

            var doc = new GoodsShipment
            {
                GoodsShipmentId = Doc, ShipDate = ShipDate, CompanyCode = Customer,
                PlantId = Plant, CreatedBy = "admin", CreatedByRoleId = RoleId,
                Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Approved,
                TotalAmount = Qty * Price * Rate,      // 表頭是本幣（模型註解如此定義）
            };
            doc.Lines.Add(new GoodsShipmentLine
            {
                GoodsShipmentId = Doc, LineNo = 1, ProductCode = Product,
                ShipQty = Qty, StockQty = Qty, UomCode = Uom, StockUomCode = Uom,
                UnitPrice = Price, CurrencyCode = Ccy, ExchangeRate = Rate,
                LineAmount = Qty * Price,              // 明細是交易幣
                TaxRate = 0m, TaxAmount = 0m,
                TotalAmount = Qty * Price,
                AmountLocal = Qty * Price * Rate,
            });
            db.GoodsShipments.Add(doc);
            await db.SaveChangesAsync();
        }
    }
}
