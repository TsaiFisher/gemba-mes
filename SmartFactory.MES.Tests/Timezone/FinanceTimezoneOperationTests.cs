using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Timezone;

/// <summary>
/// 財會分錄日的跨時區正確性。
///
/// 這是整個 <see cref="IDateTimeService"/> 體系裡後果最重的一段：分錄日決定分錄
/// 落在哪個會計期間。差一天在月初／月底就是差一個月——輕則撞期間鎖讓使用者莫名
/// 存不了檔，重則靜默記進已結帳的上個月。
///
/// 覆蓋的兩條路徑都是「以當下日期入帳」而非沿用來源單日期：
///   • 進貨退回的 AP 回沖分錄（<c>ReverseForReturnedQtyAsync</c>）
///   • 發票作廢的沖銷分錄（<c>VoidAsync</c>）
///
/// 前者原本寫死 <c>DateTime.UtcNow</c>，而**同一支服務**的後者用的是
/// <c>GetLocalDate()</c>——同一張發票的兩個沖銷動作會落在不同日期。
/// 既有的 FIN 測試抓不到：它們把 IDateTimeService 模擬成固定日期，
/// 而違規的那條路徑根本不呼叫它。
/// </summary>
public class FinanceTimezoneOperationTests
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";
    private const string GrId     = "GR-TZ-AP01";

    /// <summary>來源進貨日：刻意設成過去的固定日期，沖銷若誤用它就會被抓到。</summary>
    private static readonly DateOnly ReceiptDate = new(2026, 3, 1);

    private static readonly string[] BothZones =
        { TimezoneTestKit.FarEast, TimezoneTestKit.FarWest };

    [Fact]
    public async Task ApReturnReversalJournal_EntryDate_FollowsPlantTimeZone()
    {
        var dates = new Dictionary<string, DateOnly>();

        foreach (var tz in BothZones)
        {
            using var ctx = NewContext();
            var svc = BuildService(ctx, tz);
            SeedGoodsReceipt(ctx);

            await svc.AccrueForReceivedQtyAsync(GrId, 1, 10m);
            await svc.ReverseForReturnedQtyAsync(GrId, 1, 4m);

            var reversal = await SingleEntryAsync(ctx, "APInvoiceReturn");

            Assert.Equal(TimezoneTestKit.TodayIn(tz), reversal.EntryDate);
            // 而不是沿用來源進貨日
            Assert.NotEqual(ReceiptDate, reversal.EntryDate);
            dates[tz] = reversal.EntryDate;
        }

        AssertOneDayApart(dates);
    }

    [Fact]
    public async Task ApVoidReversalJournal_EntryDate_FollowsPlantTimeZone()
    {
        var dates = new Dictionary<string, DateOnly>();

        foreach (var tz in BothZones)
        {
            using var ctx = NewContext();
            var svc = BuildService(ctx, tz);
            SeedGoodsReceipt(ctx);

            var invoice = await svc.AccrueForReceivedQtyAsync(GrId, 1, 10m);
            await svc.VoidAsync(invoice.APInvoiceId);

            var reversal = await SingleEntryAsync(ctx, "APInvoice-Void");

            Assert.Equal(TimezoneTestKit.TodayIn(tz), reversal.EntryDate);
            Assert.NotEqual(ReceiptDate, reversal.EntryDate);
            dates[tz] = reversal.EntryDate;
        }

        AssertOneDayApart(dates);
    }

    /// <summary>
    /// 退貨回沖與作廢沖銷必須落在同一天——它們是同一張發票、同一個時刻的兩個動作。
    /// 這正是修掉的那個不一致：一邊 UTC、一邊當地日，跨午夜時會分屬兩個期間。
    /// </summary>
    [Fact]
    public async Task ReturnAndVoidReversals_ShareTheSameEntryDate()
    {
        using var ctx = NewContext();
        var svc = BuildService(ctx, TimezoneTestKit.FarEast);
        SeedGoodsReceipt(ctx);

        var invoice = await svc.AccrueForReceivedQtyAsync(GrId, 1, 10m);
        await svc.ReverseForReturnedQtyAsync(GrId, 1, 4m);
        await svc.VoidAsync(invoice.APInvoiceId);

        var ret  = await SingleEntryAsync(ctx, "APInvoiceReturn");
        var void_ = await SingleEntryAsync(ctx, "APInvoice-Void");

        Assert.Equal(ret.EntryDate, void_.EntryDate);
    }

    // ── 組裝 ────────────────────────────────────────────────────────────

    private static Data.ApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<Data.ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var ctx = new Data.ApplicationDbContext(options);
        ctx.Database.EnsureCreated();
        ctx.CurrentPlantId = PlantId;
        return ctx;
    }

    /// <summary>用真實的 JournalEntryService：要驗的正是寫進 DB 的分錄日本身。</summary>
    private static APInvoiceService BuildService(Data.ApplicationDbContext ctx, string tz)
    {
        var apSeq = 0;
        var jeSeq = 0;
        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("APInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"API{++apSeq:D6}"));
        docNo.Setup(d => d.NextAsync("JournalEntry", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"JE{++jeSeq:D6}"));

        var currency = new Mock<ICurrencyService>();
        currency.Setup(c => c.GetBaseCurrencyAsync()).ReturnsAsync("TWD");
        currency.Setup(c => c.RoundAsync(It.IsAny<decimal>(), It.IsAny<string>()))
            .ReturnsAsync((decimal a, string _) => Math.Round(a, 2, MidpointRounding.AwayFromZero));
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        // 期間服務回 null＝無鎖倉（本測試要驗的是日期取值，不是鎖倉行為）
        var periods = new Mock<IAccountingPeriodService>();
        periods.Setup(p => p.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountingPeriod?)null);
        periods.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountingPeriod?)null);

        return new APInvoiceService(
            ctx, docNo.Object, TimezoneTestKit.RealIn(tz), currency.Object,
            periodSvc: null,
            journalSvc: new JournalEntryService(ctx, docNo.Object, periods.Object));
    }

    private static void SeedGoodsReceipt(Data.ApplicationDbContext ctx)
    {
        ctx.Companies.Add(new Company
            { CompanyCode = SuppCode, CompanyName = "測試供應商", IsSupplier = true });

        var gr = new GoodsReceipt
        {
            GoodsReceiptId = GrId, ReceiptDate = ReceiptDate,
            CreatedBy = "tester", SupplierCode = SuppCode,
            Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = GrId, LineNo = 1, ProductCode = "MAT-A",
            ReceiptQty = 10m, UomCode = "PC", StockQty = 10m, StockUomCode = "PC",
            UnitPrice = 100m, TaxRate = 0.05m,
        });
        ctx.GoodsReceipts.Add(gr);
        ctx.SaveChanges();
    }

    private static async Task<JournalEntry> SingleEntryAsync(
        Data.ApplicationDbContext ctx, string sourceDocType)
        => await ctx.JournalEntries.IgnoreQueryFilters()
            .SingleAsync(j => j.SourceDocType == sourceDocType);

    private static void AssertOneDayApart(IReadOnlyDictionary<string, DateOnly> dates)
    {
        // 差距是 1 或 2，不是恆為 1：兩地相距 25 小時，所以每天有整整一小時
        // （UTC 10:00–11:00，FarEast 已跨日而 FarWest 還沒）差距是 2。
        // 寫死 == 1 會讓這條每天固定紅一小時。要守的是「兩邊不是同一天」，故用範圍。
        var gap = dates[TimezoneTestKit.FarEast].DayNumber - dates[TimezoneTestKit.FarWest].DayNumber;
        Assert.InRange(gap, 1, 2);
    }
}
