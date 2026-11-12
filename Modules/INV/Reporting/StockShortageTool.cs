using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.INV.Reporting;

/// <summary>缺貨分析的請求。無期間概念（看的是當下庫存快照）。</summary>
public class StockShortageRequest : ReportRequestBase
{
    /// <summary>關鍵字，同時模糊比對品號與品名。</summary>
    public string? Keyword { get; set; }

    /// <summary>在途截止日：只計到貨日（委外為應交日）在此日以前的在途。null＝今天起 30 天。
    /// 與投產料閘的 byDate 同義（投產取工單計畫開工日）。</summary>
    public DateOnly? ByDate { get; set; }
}

/// <summary>
/// 缺貨分析（§六 I3）。由 <c>StockShortageController.BuildQueryAsync</c> 抽出，由 <c>InvReportCharacterizationTests</c> 守門。
/// <para>2026-10-27 改為「目前可用＋預計可用」兩段（使用者裁決：要與投產料閘共用推算，否則不改）。
/// 原本可用量＝在手 − <c>Inventory.QtyReserved</c>，而那個欄位從沒有任何程式寫入（恆 0）——預留整個沒扣，待驗倉也算可用。
/// 現在三個數字全部取自 <see cref="IInventoryReservationService"/>，與投產（WorkOrderReleaseService 硬料閘）逐字同一套：
/// 目前可用＝GetPlantAvailableQtyAsync（排除待驗倉、扣有效在手預留，客供倉照計——理由見該方法文件）；
/// 在途＝GetAvailableInTransitAsync／GetAvailableOutsourceInTransitAsync（截止日前到貨、扣已被需求認領的量）。
/// 工單剩餘產出與待驗量投產料閘不計，這裡也不計——要加得兩邊一起加。</para>
/// </summary>
public class StockShortageTool
    : IReportTool<StockShortageRequest, IReadOnlyList<StockShortageRowVm>>, IReportToolDescriptor
{
    /// <summary>未指定截止日時的在途範圍（天）。</summary>
    public const int DefaultHorizonDays = 30;

    private readonly ApplicationDbContext _db;
    private readonly IInventoryReservationService _reservations;
    private readonly IDateTimeService _dt;

    public StockShortageTool(ApplicationDbContext db, IInventoryReservationService reservations, IDateTimeService dt)
    {
        _db           = db;
        _reservations = reservations;
        _dt           = dt;
    }

    public string ToolKey     => "StockShortage";
    public string Description => "列出目前可用量低於安全存量的品號，並以截止日前到貨的採購在途與委外在途推算預計可用量與預計缺口（與投產料閘同一套算法），依預計缺口降冪。目前可用量為在手量（不含待驗倉）減有效預留。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>——畫面閘門已於 2026-08-14 由 <c>CanCreate</c> 改為
    /// <c>CanRead</c>（覆寫 §8.1-終）。「照抄畫面」這條規則本身不變，變的是被照抄的那一端。</summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。顯式實作讓它
    // 只透過 IReportToolDescriptor 取用，既不觸發規則也不必為它造一個假的欄位名鍵。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`StockShortageRowVm` 只有安全存量／在手量／預留量／缺口量，無任何金額欄位）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>看的是當下快照，沒有期間概念——實作完全不碰 IRelativePeriodResolver。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => false;

    public Type RequestType  => typeof(StockShortageRequest);
    public Type ResponseType => typeof(IReadOnlyList<StockShortageRowVm>);

    /// <summary>
    /// 本工具的 <c>ExecuteAsync</c> 本來就回整份集合，直接轉交即可。
    /// 仍須顯式實作而非讓基底猜——猜錯的那兩支正是靜默產出空白報表的來源。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<StockShortageRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();

    public async Task<IReadOnlyList<StockShortageRowVm>> ExecuteAsync(
        StockShortageRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var plantId = request.PlantId ?? "";
        var byDate  = request.ByDate ?? _dt.GetLocalDate().AddDays(DefaultHorizonDays);

        // ⚠️ 刻意不篩 IsActive：已停用但安全存量 > 0 的品號照樣列出（現況，特徵測試鎖住）。
        var productQuery = _db.Products.Where(p => p.SafetyStock > 0);

        if (!string.IsNullOrWhiteSpace(request.Keyword))
            productQuery = productQuery.Where(p =>
                p.ProductCode.Contains(request.Keyword) ||
                p.ProductName.Contains(request.Keyword));

        var products = await productQuery
            .AsNoTracking()
            .Select(p => new { p.ProductCode, p.ProductName, p.StockUom, p.SafetyStock })
            .ToListAsync(ct);
        var codes = products.Select(p => p.ProductCode).ToList();

        // 在手（不含待驗）、預留、目前可用：共用服務批次版，與投產料閘的 GetPlantAvailableQtyAsync 同口徑
        var avail = await _reservations.GetPlantAvailabilityAsync(plantId, codes, ct);

        var rows = new List<StockShortageRowVm>();
        foreach (var p in products)
        {
            var a = avail[p.ProductCode];
            if (p.SafetyStock - a.Available <= 0) continue;   // 目前不缺就不必查在途
            rows.Add(new StockShortageRowVm
            {
                ProductCode    = p.ProductCode,
                ProductName    = p.ProductName,
                UomCode        = p.StockUom ?? "",
                SafetyStock    = p.SafetyStock,
                QtyOnHand      = a.OnHand,
                QtyReserved    = a.Reserved,
                QtyAvailable   = a.Available,
                QtyIncomingPo  = await _reservations.GetAvailableInTransitAsync(plantId, p.ProductCode, byDate, ct),
                QtyIncomingOpo = await _reservations.GetAvailableOutsourceInTransitAsync(plantId, p.ProductCode, byDate, ct),
            });
        }

        return rows
            .OrderByDescending(r => r.ProjectedShortageQty)
            .ThenByDescending(r => r.ShortageQty)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<StockShortageRowVm>("StockShortage.ProductCode",  r => r.ProductCode),
        Column<StockShortageRowVm>("StockShortage.ProductName",  r => r.ProductName),
        Column<StockShortageRowVm>("StockShortage.UomCode",      r => r.UomCode),
        Column<StockShortageRowVm>("StockShortage.SafetyStock",  r => r.SafetyStock),
        Column<StockShortageRowVm>("StockShortage.QtyOnHand",    r => r.QtyOnHand),
        Column<StockShortageRowVm>("StockShortage.QtyReserved",  r => r.QtyReserved),
        Column<StockShortageRowVm>("StockShortage.QtyAvailable", r => r.QtyAvailable),
        Column<StockShortageRowVm>("StockShortage.ShortageQty",  r => r.ShortageQty),
        Column<StockShortageRowVm>("StockShortage.QtyIncomingPo",  r => r.QtyIncomingPo),
        Column<StockShortageRowVm>("StockShortage.QtyIncomingOpo", r => r.QtyIncomingOpo),
        Column<StockShortageRowVm>("StockShortage.QtyProjected",  r => r.QtyProjected),
        Column<StockShortageRowVm>("StockShortage.ProjectedShortageQty", r => r.ProjectedShortageQty),
    ];
}
