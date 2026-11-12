using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 付款單／收款單的「作廢入口是否真的存在」。
    ///
    /// 這一組守的不是伺服端行為，而是**畫面上按不按得到**——2026-09-05 人工驗收 4-3
    /// 實測發現兩邊寫反了：
    ///
    ///   ・<c>FinPaymentService.VoidAPPaymentAsync</c> 明寫「僅已付款單據可作廢」，
    ///     而且只有那條路徑會回補發票已核銷金額、回補銀行餘額、開 APPayment-Void 回沖傳票；
    ///   ・列表 View 的條件卻是 <c>Status != Paid</c>，正好把它藏起來。
    ///
    /// 於是整條作廢回沖從操作員的角度**完全不存在**：付錯款只能去改資料庫。
    /// 而服務層的單元測試全綠——它們直接呼叫服務，繞過了唯一的入口。
    ///
    /// 這種「服務做得好好的、按鈕沒露出來」的缺陷，只有渲染真的 HTML 才看得見，
    /// 所以放在整合測試而不是單元測試。斷言鎖 <c>data-action</c> 與 <c>data-id</c>
    /// 的組合，因為那正是前端委派唯一認得的東西（見 crud-panel 的 data-action 機制）。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class SettlementVoidEntryPointTests : PostingResidueTestBase
    {
        private const string Supplier  = Prefix + "-VSUP";
        private const string Customer  = Prefix + "-VCUS";
        private const string PaymentId = Prefix + "-VPAY1";
        private const string ReceiptId = Prefix + "-VRCP1";

        private static readonly DateOnly Day = new(2097, 7, 20);

        public SettlementVoidEntryPointTests(AuthedScheduleFactory factory) : base(factory) { }

        public override void Dispose()
        {
            using (var scope = NewScope(out var db))
            {
                db.Database.ExecuteSqlRaw(
                    @"DELETE FROM ""APPayments"" WHERE ""APPaymentId"" LIKE 'RESID%';
                      DELETE FROM ""ARReceipts"" WHERE ""ARReceiptId"" LIKE 'RESID%';
                      DELETE FROM ""Companies"" WHERE ""CompanyCode"" LIKE 'RESID-V%';");
            }
            base.Dispose();
        }

        private async Task SeedAsync()
        {
            using var scope = NewScope(out var db);
            db.Database.ExecuteSqlRaw(
                @"DELETE FROM ""APPayments"" WHERE ""APPaymentId"" LIKE 'RESID%';
                  DELETE FROM ""ARReceipts"" WHERE ""ARReceiptId"" LIKE 'RESID%';");

            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Supplier))
                db.Companies.Add(new Company
                {
                    CompanyCode = Supplier, CompanyName = "作廢入口測試供應商",
                    IsSupplier = true, IsActive = true,
                });
            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Customer))
                db.Companies.Add(new Company
                {
                    CompanyCode = Customer, CompanyName = "作廢入口測試客戶",
                    IsCustomer = true, IsActive = true,
                });

            // 已付款／已收款：這正是伺服端唯一會做回沖的狀態
            db.APPayments.Add(new APPayment
            {
                APPaymentId = PaymentId, PaymentDate = Day, SupplierCode = Supplier,
                CurrencyCode = "NTD", ExchangeRate = 1m, Amount = 100m, AmountLocal = 100m,
                Status = APPaymentStatus.Paid, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.ARReceipts.Add(new ARReceipt
            {
                ARReceiptId = ReceiptId, ReceiptDate = Day, CustomerCode = Customer,
                CurrencyCode = "NTD", ExchangeRate = 1m, Amount = 100m, AmountLocal = 100m,
                Status = ARReceiptStatus.Received, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task PaidPayment_ListRow_ExposesVoidAction()
        {
            await SeedAsync();
            var (client, _) = await NewAuthedClientAsync();

            var html = await client.GetStringAsync(
                $"/APPayment?searched=true&searchSupplier={Supplier}");

            Assert.Contains(PaymentId, html);
            Assert.Contains($"data-action=\"voidPayment\" data-id=\"{PaymentId}\"", html);
        }

        [Fact]
        public async Task ReceivedReceipt_ListRow_ExposesVoidAction()
        {
            await SeedAsync();
            var (client, _) = await NewAuthedClientAsync();

            var html = await client.GetStringAsync(
                $"/ARReceipt?searched=true&searchCustomer={Customer}");

            Assert.Contains(ReceiptId, html);
            Assert.Contains($"data-action=\"voidReceipt\" data-id=\"{ReceiptId}\"", html);
        }
    }
}
