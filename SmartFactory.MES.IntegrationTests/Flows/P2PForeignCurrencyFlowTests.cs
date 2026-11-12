using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 的 1-8：以外幣採購單重跑整條 P2P，驗《對帳規格》§5.17 的
    /// 換算鏈——交易幣金額、匯率、本幣金額三者一致。
    ///
    /// 為什麼單獨開一個類別（而非併進 <see cref="P2PVariantFlowTests"/>）：外幣案例要動到
    /// Currencies／CurrencyRates 這兩張**沒有廠別欄位**的全域主檔，FlowTestBase 的 Purge
    /// 是以廠別為界的，清不掉也不該清（併發的其他測試可能正在用）。把它隔離在自己的
    /// 類別與廠別裡，全域主檔的寫入就只發生在一處，且刻意寫成 idempotent、不在 Dispose
    /// 刪除——留下兩筆幣別主檔的代價，遠小於刪掉別人正在用的幣別。
    ///
    /// 幣別代碼刻意用 ZZU 而非 USD：真實幣別代碼可能被其他測試或種子資料以不同小數位
    /// 建立，撞上就會讓本測試的預期金額無聲位移。
    /// </summary>
    [Collection("Integration")]
    public class P2PForeignCurrencyFlowTests : P2PVariantTestBase
    {
        protected override string Plant => "ZZP2PF";

        private const string Fx = "ZZU";
        private const decimal RecvRate = 30m;   // 收貨日匯率
        private const decimal PayRate  = 32m;   // 付款日匯率（本幣貶值 → 應付變貴 → 匯兌損失）

        public P2PForeignCurrencyFlowTests(TestWebApplicationFactory factory) : base(factory)
            => SeedCurrencyAsync().GetAwaiter().GetResult();

        /// <summary>
        /// 補上外幣主檔與兩個日期的匯率。全域表，故一律先查後補。
        /// 補完必須清幣別快取（10 分鐘）：同一輪測試若已有人取過幣別清單，
        /// 快取裡是「沒有 ZZU」的舊清單，小數位會靜靜退回預設值 2。
        /// </summary>
        private async Task SeedCurrencyAsync()
        {
            using var scope = NewScope(out var db);

            if (!await db.Currencies.AsNoTracking().AnyAsync(c => c.CurrencyCode == Fx))
                db.Currencies.Add(new Currency
                {
                    CurrencyCode = Fx, CurrencyName = "流程測試外幣",
                    Symbol = "Z$", DecimalPlaces = 2, IsActive = true,
                });

            foreach (var (date, rate) in new[] { (OrderDate, RecvRate), (PayDate, PayRate) })
                if (!await db.CurrencyRates.AsNoTracking()
                        .AnyAsync(r => r.CurrencyCode == Fx && r.RateDate == date))
                    db.CurrencyRates.Add(new CurrencyRate
                    {
                        CurrencyCode = Fx, RateDate = date, BuyRate = rate, SellRate = rate,
                    });

            await db.SaveChangesAsync();
            Svc<ICurrencyService>(scope).InvalidateCache();
        }

        /// <summary>
        /// 外幣全鏈：進貨（匯率 30）→ 上架 → 認列 → 付款（匯率 32）。
        ///
        /// 三個判準：
        ///   ① 存貨與暫估應付都必須以**本幣**入帳，否則 2152 兩端幣別不同、永遠平不掉；
        ///   ② 認列後 2152 收斂為 0（借貸同為本幣淨成本）；
        ///   ③ 付款日匯率不同 → 差額必須落在匯兌損益（5199），而不是留在 2100。
        /// </summary>
        [Fact]
        public async Task ForeignCurrencyPurchase_PostsInBaseCurrency_AndRecognisesFxLossOnPayment()
        {
            const decimal qty = 100m, price = 250m, taxRate = 0.05m;
            const decimal netFx   = qty * price;              // 25,000 外幣
            const decimal grossFx = netFx * (1m + taxRate);   // 26,250 外幣

            await CreateApprovedPoAsync(qty, price, taxRate, Fx);
            var grId = await CreateGoodsReceiptAsync(qty, price, taxRate, Fx, RecvRate);
            await PutAwayAsync(grId, qty, RecvRate);

            string apId;
            using (var scope = NewScope(out var db))
            {
                var invoice = await Svc<IAPInvoiceService>(scope).AccrueForReceivedQtyAsync(grId, 1, qty);
                apId = invoice.APInvoiceId;

                // 發票以交易幣記帳、另存本幣總額與匯率快照
                Assert.Equal(Fx, invoice.CurrencyCode);
                Assert.Equal(RecvRate, invoice.ExchangeRate);
                Assert.Equal(grossFx, invoice.TotalAmount);
                Assert.Equal(grossFx * RecvRate, invoice.AmountLocal);

                var gl = await GlNetByAccountAsync(db, Plant);
                // ① 存貨必須是本幣：100 × 250 × 30
                Assert.Equal(netFx * RecvRate, Net(gl, StandardAccounts.RawMaterialInventoryCode));
                // ② 上架貸 / 認列借 同為本幣淨成本 → 精準收斂
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));
            }

            // ③ 以付款日匯率結清：差額進匯兌損失，2100 不得留殘值
            using (var scope = NewScope(out var db))
            {
                var fin = Svc<IFinPaymentService>(scope);
                var pmt = await fin.CreateAPPaymentAsync(new APPaymentRequest(
                    Supplier, PayDate, PaymentMethod.Wire, Fx, PayRate, grossFx, Bank, null, null,
                    new List<PaymentAllocationLine> { new(apId, grossFx) }), UserId, Plant);
                await fin.ConfirmAPPaymentAsync(pmt.APPaymentId);

                var confirmed = await db.APPayments.IgnoreQueryFilters()
                    .SingleAsync(p => p.APPaymentId == pmt.APPaymentId);
                // 匯兌損失 = 26,250 × (32 − 30)
                Assert.Equal(grossFx * (PayRate - RecvRate), confirmed.ExchangeGainLoss);

                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m, Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Equal(0m, Net(gl, StandardAccounts.GrIrAPClearingCode));
                Assert.Equal(grossFx * (PayRate - RecvRate), Net(gl, StandardAccounts.FxLossCode));
                Assert.Equal(0m, gl.Values.Sum());
            }
        }
    }
}
