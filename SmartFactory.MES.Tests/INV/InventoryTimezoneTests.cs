using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// INV 日期欄位的來源守門。分兩件事：
///   ① 產生業務日的那一層（過帳服務）必須走 IDateTimeService.GetLocalDate()，
///      不得用 DateTime.Today（伺服器當地）或 DateTime.UtcNow（UTC）——跨時區廠別的核心保證；
///   ② **消費**業務日的那一層（成本層服務）必須原樣接受呼叫端傳入的日期，
///      不得自己再生一個。2026-09-07 之前 ReceiptDate 就是在這裡被重新生出來的，
///      於是倒填的單據會建出帶著「今天」的成本層，FIFO 取價順序整個位移。
/// </summary>
public class InventoryTimezoneTests : TestBase
{
    private const string PlantId = "P001";

    /// <summary>
    /// 成本層的入庫日**原樣採用呼叫端傳入的業務日**，不會自己再產生一個。
    ///
    /// 這條原本斷言的是「等於 <c>GetLocalDate()</c>」——當時的守備對象是
    /// <c>DateTime.UtcNow</c>（伺服器時鐘），方向沒錯，但守錯了層級：
    /// 同一次過帳早就有一個業務日了（<c>StockMovement.MovementDate</c>、
    /// <c>JournalEntry.EntryDate</c>、期間歸屬全用它），成本層卻自己又問了一次時鐘。
    /// 兩者只在**單據倒填**時分岔（單據日期欄位是開放的，只有期間鎖擋著），
    /// 分岔時 FIFO 會用錯層的單價，而沒有任何稽核看得到。
    ///
    /// 現在時區正確性由「產生單據日的那一層」負責（見
    /// <c>InventoryTimezoneOperationTests</c> 的 MovementDate 相關案例），
    /// 這一層只負責**不要把它弄丟**。刻意傳一個與任何「今天」都不同的日期。
    /// </summary>
    [Fact]
    public async Task InventoryCostLayer_AddLayer_UsesCallerBusinessDate_NotToday()
    {
        _context.CurrentPlantId = PlantId;
        var svc = new InventoryCostLayerService(_context);
        var backdated = new DateOnly(2020, 2, 29);   // 明顯不可能是任何時區的「今天」

        await svc.AddLayerAsync(PlantId, "MAT001", "A-01", "LOT1", qty: 100m, unitCost: 12.5m,
            sourceDocNo: "PE-001", receiptDate: backdated);
        await _context.SaveChangesAsync();

        var layer = await _context.Set<InventoryCostLayer>().SingleAsync();
        Assert.Equal(backdated, layer.ReceiptDate);
    }

    /// <summary>
    /// 反向守門：<c>InventoryCostLayerService</c> **不得**取得任何時間服務。
    ///
    /// 它寫進去的每一個日期都是「某次過帳的業務日」，一律由呼叫端傳入。
    /// 只要它拿得到時鐘，就永遠有機會再生出一個與該次過帳不同的日期——
    /// 那正是 2026-09-07 之前 ReceiptDate 的缺陷。把它變成結構上的不可能。
    /// （原本這條斷言的是相反的事：必須注入 IDateTimeService，用來擋
    /// <c>DateTime.UtcNow</c>。守備對象仍在，只是移到了產生單據日的那一層。）
    /// </summary>
    [Fact]
    public void InventoryCostLayerService_CannotOriginateABusinessDate()
    {
        var ctors = typeof(InventoryCostLayerService).GetConstructors();
        Assert.Single(ctors);
        var paramTypes = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IDateTimeService), paramTypes);

        // 「繞過建構式直接讀 DateTime.UtcNow」這一路刻意**不**用原始碼字串掃描來擋：
        // 註解裡本來就必須寫得出「不可以用 GetLocalDate」，掃字串會掃到說明自己
        // （R22 的 StripComments 就是為了這個而寫的）。
        // 那條路由行為測試守著——CostLayer_ReceiptDate_IsTheDocumentDate_NotAnyClock
        // 在相差 25 小時的兩個時區都斷言層的日期等於固定的過去日，
        // 任何時鐘呼叫加回來都會讓它變成「今天」而立刻紅。
    }

    [Fact]
    public void InventoryPostingService_RequiresDateTimeService()
    {
        // 防止 InventoryPostingService 退化回 DateTime.UtcNow
        var ctors = typeof(InventoryPostingService).GetConstructors();
        Assert.Single(ctors);
        var paramTypes = ctors[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(IDateTimeService), paramTypes);
    }
}
