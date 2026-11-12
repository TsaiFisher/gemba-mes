using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Timezone;

/// <summary>
/// 庫存作業的跨時區正確性——用**真實**的 <see cref="DateTimeService"/> 各跑一遍
/// UTC+14 與 UTC−11，斷言業務日確實跟著廠別時區走。
///
/// 為什麼不能只靠既有那些「mock 一個固定日期再斷言等於它」的測試：
/// 那種寫法證明的是「有呼叫 IDateTimeService」，不是「換時區結果會變」。
/// 兩者的差別正好是這次修掉的一整類缺陷——程式**根本沒呼叫**服務、直接讀
/// 伺服器時鐘，mock 從頭到尾沒被問過，測試照樣綠。
///
/// 每支測試都同時斷言兩件事：
///   ① 各時區的業務日 == 該時區的「今天」（值正確）
///   ② 兩個時區的結果相差恰好一天（真的有跟著換，不是巧合相等）
/// 只驗 ① 的話，「永遠回 UTC 日」的實作在部分時段仍會矇混過關。
/// </summary>
public class InventoryTimezoneOperationTests 
{
    private const string PlantId = "P001";

    /// <summary>過帳日刻意設成固定的過去日期：作廢若沿用它而非「當下的當地日」即被抓到。</summary>
    private static readonly DateOnly PostDate = new(2026, 3, 1);

    private static readonly string[] BothZones =
        { TimezoneTestKit.FarEast, TimezoneTestKit.FarWest };

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

    // ── FIFO 成本層的入庫日 ──────────────────────────────────────────────

    /// <summary>
    /// 成本層的入庫日＝**單據的業務日**，兩個時區必須拿到**同一個值**。
    ///
    /// 這條原本斷言的是相反的事（跟著廠別時區走、兩地差一天）。方向本身沒錯——
    /// 當時的守備對象是有人直接讀 <c>DateTime.UtcNow</c>——但守錯了層級：
    /// 同一次過帳早就有業務日了（<c>MovementDate</c>、分錄日、期間歸屬全用它），
    /// 成本層卻自己再去問一次時鐘。兩者只在**單據倒填**時分岔，
    /// 而分岔時 FIFO 會取錯層的單價，沒有任何稽核看得到（2026-09-07 修正）。
    ///
    /// 所以現在要守的不變量倒過來：層的日期**不得**隨任何時鐘變動。
    /// 刻意用固定的過去日 <see cref="PostDate"/> 過帳，並在兩個相差 25 小時的時區
    /// 各跑一次——只要有人把時鐘呼叫加回去，兩邊就會變成「今天」而不再相等。
    ///
    /// 業務日本身的時區正確性由產生它的那一層負責，見下方
    /// <c>VoidPosting_MovementDateAndReversalJournalDate_FollowPlantTimeZone</c>。
    /// </summary>
    [Fact]
    public async Task CostLayer_ReceiptDate_IsTheDocumentDate_NotAnyClock()
    {
        var dates = new Dictionary<string, DateOnly>();

        foreach (var tz in BothZones)
        {
            using var ctx = NewContext();             // 每個時區用各自乾淨的 DB
            SeedMasterData(ctx, CostMethod.Fifo);

            var posting = new InventoryPostingService(
                ctx, new InventoryService(ctx),
                new InventoryCostLayerService(ctx),
                new Mock<ISystemParamService>().Object,
                TimezoneTestKit.RealIn(tz));

            await posting.PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId      = PlantId,
                DocNo        = "GR-TZ01",
                LineNo       = 1,
                ProductCode  = "MAT-A",
                LocationCode = "LOC1",
                Qty          = 10m,
                UnitPrice    = 5m,
                TypeCode     = "PI",
                TypeCategory = "Purchase",
                MovementDate = PostDate,
                UomCode      = "PC",
                StockUomCode = "PC",
                CreatedBy    = "tester",
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(false),
                SetDocPosted               = () => { },
            });
            await ctx.SaveChangesAsync();

            var layer = await ctx.Set<InventoryCostLayer>().SingleAsync();
            Assert.Equal(PostDate, layer.ReceiptDate);       // 不是任何一邊的「今天」
            dates[tz] = layer.ReceiptDate;
        }

        // 兩個相差 25 小時的時區拿到同一天——證明它真的不看時鐘。
        Assert.Equal(dates[TimezoneTestKit.FarEast], dates[TimezoneTestKit.FarWest]);
    }

    // ── 過帳作廢：異動日與反向分錄日 ────────────────────────────────────

    [Fact]
    public async Task VoidPosting_MovementDateAndReversalJournalDate_FollowPlantTimeZone()
    {
        var movementDates = new Dictionary<string, DateOnly>();
        var journalDates  = new Dictionary<string, DateOnly>();

        foreach (var tz in BothZones)
        {
            using var ctx = NewContext();
            SeedMasterData(ctx);

            var seq = 0;
            var docNo = new Mock<IDocNumberService>();
            docNo.Setup(d => d.NextAsync("JournalEntry", It.IsAny<string>(),
                    It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult($"JRN{++seq:D6}"));

            var posting = new InventoryPostingService(
                ctx, new InventoryService(ctx),
                new Mock<IInventoryCostLayerService>().Object,
                new Mock<ISystemParamService>().Object,
                TimezoneTestKit.RealIn(tz),
                journalSvc: new JournalEntryService(ctx, docNo.Object, new AccountingPeriodService(ctx)));

            await posting.PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId      = PlantId,
                DocNo        = "GR-TZ02",
                LineNo       = 1,
                ProductCode  = "MAT-A",
                LocationCode = "LOC1",
                Qty          = 10m,
                UnitPrice    = 25m,
                TypeCode     = "PI",
                TypeCategory = "Purchase",
                MovementDate = PostDate,
                UomCode      = "PC",
                StockUomCode = "PC",
                CreatedBy    = "tester",
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(false),
                SetDocPosted               = () => { },
            });
            await ctx.SaveChangesAsync();

            await posting.VoidDocAsync(new VoidDocRequest
            {
                PlantId    = PlantId,
                DocNo      = "GR-TZ02",
                Reason     = "跨時區測試",
                CreatedBy  = "tester",
                SetDocVoid = () => { },
            });
            await ctx.SaveChangesAsync();

            var reversalMovement = await ctx.StockMovements.IgnoreQueryFilters()
                .Where(m => m.DocNo == "GR-TZ02")
                .OrderBy(m => m.CreatedAt)
                .LastAsync();
            var reversalJournal = await ctx.JournalEntries.IgnoreQueryFilters()
                .OrderBy(j => j.JournalId)
                .LastAsync();

            Assert.Equal(TimezoneTestKit.TodayIn(tz), reversalMovement.MovementDate);
            Assert.Equal(TimezoneTestKit.TodayIn(tz), reversalJournal.EntryDate);

            movementDates[tz] = reversalMovement.MovementDate;
            journalDates[tz]  = reversalJournal.EntryDate;
        }

        AssertOneDayApart(movementDates);
        AssertOneDayApart(journalDates);
    }

    // ── 備料揀料移倉的異動日 ────────────────────────────────────────────

    [Fact]
    public async Task MaterialStaging_MovementDate_FollowsPlantTimeZone()
    {
        var dates = new Dictionary<string, DateOnly>();

        foreach (var tz in BothZones)
        {
            using var ctx = NewContext();
            SeedMasterData(ctx);
            ctx.Inventories.Add(new Inventory
            {
                PlantId = PlantId, ProductCode = "MAT-A",
                LocationCode = "LOC1", LotNo = "", QtyOnHand = 100m, UnitCost = 5m,
            });

            var order = new MaterialStagingOrder
            {
                StagingId = "IM-TZ01", PlantId = PlantId, Status = "Confirmed",
                StagingLocationCode = "STG1", CreatedBy = "tester",
            };
            order.Lines.Add(new MaterialStagingLine
            {
                StagingId = "IM-TZ01", LineNo = 1, ProductCode = "MAT-A",
                RequiredQty = 10m, PickedQty = 0m,
                FromLocationCode = "LOC1", UomCode = "PC",
            });
            ctx.Set<MaterialStagingOrder>().Add(order);
            await ctx.SaveChangesAsync();

            // 攔下送進過帳引擎的請求，看它帶的是哪一天
            PostSamePlantTransferRequest? captured = null;
            var posting = new Mock<IInventoryPostingService>();
            posting.Setup(p => p.PostSamePlantTransferAsync(
                    It.IsAny<PostSamePlantTransferRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PostSamePlantTransferRequest, CancellationToken>((r, _) =>
                {
                    captured = r;
                    foreach (var line in r.Lines) line.SetQtyPutAway(line.StockQty);
                    r.SetDocPosted();
                })
                .Returns(Task.CompletedTask);

            var svc = new MaterialStagingService(
                ctx, new InventoryReservationService(ctx), posting.Object,
                TimezoneTestKit.RealIn(tz));

            await svc.StageAsync(order, "tester");

            Assert.NotNull(captured);
            Assert.Equal(TimezoneTestKit.TodayIn(tz), captured!.MovementDate);
            dates[tz] = captured.MovementDate;
        }

        AssertOneDayApart(dates);
    }

    // ── 共用 ────────────────────────────────────────────────────────────

    private static void SeedMasterData(Data.ApplicationDbContext ctx,
        CostMethod costMethod = CostMethod.MovingAverage)
    {
        ctx.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        ctx.StorageLocations.Add(new StorageLocation
            { WhCode = "WH01", LocationCode = "LOC1", LocationName = "LOC1" });
        ctx.StorageLocations.Add(new StorageLocation
            { WhCode = "WH01", LocationCode = "STG1", LocationName = "STG1" });
        ctx.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "RAW", CategoryName = "原料",
            CostMethod = costMethod, IsActive = true
        });
        ctx.Products.Add(new Product { ProductCode = "MAT-A", CategoryCode = "RAW", IsActive = true });
        ctx.SaveChanges();
    }

    /// <summary>
    /// UTC+14 與 UTC−11 相距 25 小時，當地日期恆差一天。
    /// 少了這條，「兩邊都回同一個伺服器日期」的實作在 ① 剛好成立的時段仍會過關。
    /// </summary>
    private static void AssertOneDayApart(IReadOnlyDictionary<string, DateOnly> dates)
    {
        // 差距是 1 或 2，不是恆為 1：兩地相距 25 小時，所以每天有整整一小時
        // （UTC 10:00–11:00，FarEast 已跨日而 FarWest 還沒）差距是 2。
        // 寫死 == 1 會讓這條每天固定紅一小時。要守的是「兩邊不是同一天」，故用範圍。
        var gap = dates[TimezoneTestKit.FarEast].DayNumber - dates[TimezoneTestKit.FarWest].DayNumber;
        Assert.InRange(gap, 1, 2);
    }
}
