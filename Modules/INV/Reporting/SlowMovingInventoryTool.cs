using SmartFactory.MES.Modules._Shared;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Reporting;

/// <summary>呆滯庫存的請求。</summary>
public class SlowMovingInventoryRequest : ReportRequestBase
{
    /// <summary>呆滯天數門檻，含端點（<c>&gt;=</c>）。</summary>
    public int IdleDaysThreshold { get; set; } = SlowMovingInventoryTool.DefaultIdleDays;

    public string? ProductCode { get; set; }
}

/// <summary>
/// 呆滯庫存的結果。<c>record struct</c> 而非 class：R5b 只掃 class，
/// <c>TooMany</c> 是輸出旗標不是使用者輸入欄位，不該被要求補驗證資源鍵。
/// </summary>
public readonly record struct SlowMovingInventoryResult(
    IReadOnlyList<SlowMovingInventoryRowVm> Rows,
    bool TooMany);

/// <summary>
/// 呆滯庫存（§六 I4）。由 <c>SlowMovingInventoryController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>InvReportCharacterizationTests</c> 守門。
/// </summary>
public class SlowMovingInventoryTool
    : IReportTool<SlowMovingInventoryRequest, SlowMovingInventoryResult>, IReportToolDescriptor
{
    public const int DefaultIdleDays = 90;

    /// <summary>
    /// 庫存掃描安全上限：呆滯天數須全載廠別在手庫存後於記憶體計算，
    /// 無法在 SQL 端先行截斷而不影響結果。超過上限即拒絕並請使用者以品號縮小範圍，
    /// 避免大廠無條件查詢造成 OOM。
    /// </summary>
    public const int MaxInventoryScan = 20000;

    private readonly ApplicationDbContext _db;
    private readonly IDateTimeService     _dateTime;

    public SlowMovingInventoryTool(ApplicationDbContext db, IDateTimeService dateTime)
    {
        _db       = db;
        _dateTime = dateTime;
    }

    public string ToolKey     => "SlowMovingInventory";
    public string Description => "列出最後異動日距今超過門檻天數的庫存（品號×儲位×批號），含庫存金額，依金額降冪。";

    /// <summary>含庫存金額＝機密，動詞 <c>CanRead</c>＋分級由 MenuPermission 資料承載。</summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。顯式實作讓它
    // 只透過 IReportToolDescriptor 取用，既不觸發規則也不必為它造一個假的欄位名鍵。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>
    /// 含金額（`UnitCost`／`InventoryValue`（庫存金額））→ 依 §十三 Phase 2 **預設不進自然語言目錄**。
    /// 這不影響畫面與排程：有權限的人照樣開得了報表，差別只在能不能用一句話問到它。
    /// 要放行請開 <c>Features:QaAssistant:AllowMonetaryTools</c>（見 <c>QaToolSchemaBuilder</c>）。
    /// </summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>看的是當下快照，沒有期間概念——實作完全不碰 IRelativePeriodResolver。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => false;

    public Type RequestType  => typeof(SlowMovingInventoryRequest);
    public Type ResponseType => typeof(SlowMovingInventoryResult);

    /// <summary>
    /// <see cref="ExecuteAsync"/> 回的是 <see cref="SlowMovingInventoryResult"/>（<c>Rows</c> ＋
    /// <c>TooMany</c>），不是集合，因此排程路徑必須由這裡取出 <c>Rows</c>。
    ///
    /// <para>🚨 <c>TooMany</c> 一律<b>拋例外而不是回空集合</b>：掃描量超標時
    /// <c>ExecuteAsync</c> 回的是 <c>([], true)</c>，直接當資料用會在收件匣產出一份
    /// 零筆的報表，與「這段期間確實沒有呆滯庫存」完全無從分辨——而兩者該做的事相反。
    /// 拋出去的訊息會寫進 <c>ReportRun.ErrorMessage</c>，使用者看得到原因與解法。</para>
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
    {
        var result = await ExecuteAsync(
            request.AsToolRequest<SlowMovingInventoryRequest>(ToolKey), user, ct);

        if (result.TooMany)
            throw new InvalidOperationException(
                PzText.Get("Inv_SlowMovingScanTooLargeFmt", MaxInventoryScan));

        return result.Rows.Cast<object>().ToList();
    }

    public async Task<SlowMovingInventoryResult> ExecuteAsync(
        SlowMovingInventoryRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var plantId = request.PlantId ?? "";
        var today   = request.AsOfDate ?? _dateTime.GetLocalDate(request.TimeZoneId);

        var invQuery = _db.Inventories.Where(i => i.PlantId == plantId && i.QtyOnHand > 0);

        if (!string.IsNullOrWhiteSpace(request.ProductCode))
            invQuery = invQuery.Where(i => i.ProductCode.Contains(request.ProductCode));

        if (await invQuery.CountAsync(ct) > MaxInventoryScan)
            return new SlowMovingInventoryResult([], true);

        var inventories = await invQuery.AsNoTracking().ToListAsync(ct);

        if (inventories.Count == 0)
            return new SlowMovingInventoryResult([], false);

        var productCodes = inventories.Select(i => i.ProductCode).Distinct().ToList();

        var productNames = await _db.Products
            .Where(p => productCodes.Contains(p.ProductCode))
            .AsNoTracking()
            .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName, ct);

        // 每個（品號, 儲位, 批號）的最後異動日
        var lastMovements = await _db.StockMovements
            .Where(m => m.PlantId == plantId && productCodes.Contains(m.ProductCode))
            .GroupBy(m => new { m.ProductCode, m.LocationCode, m.LotNo })
            .Select(g => new
            {
                g.Key.ProductCode,
                g.Key.LocationCode,
                g.Key.LotNo,
                LastDate = g.Max(x => x.MovementDate)
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var movementMap = lastMovements.ToDictionary(
            m => (m.ProductCode, m.LocationCode, m.LotNo),
            m => m.LastDate);

        var rows = inventories
            .Select(inv =>
            {
                var hasMovement = movementMap.TryGetValue(
                    (inv.ProductCode, inv.LocationCode, inv.LotNo), out var lastDate);
                // ⚠️ 查無異動紀錄者以哨兵值 9999 計，因此必定超過任何門檻而永遠上榜
                //    （Export 顯示成 "N/A"）。這是現況，特徵測試鎖住。
                var idleDays = hasMovement ? today.DayNumber - lastDate.DayNumber : 9999;
                return new SlowMovingInventoryRowVm
                {
                    ProductCode      = inv.ProductCode,
                    ProductName      = productNames.TryGetValue(inv.ProductCode, out var n) ? n : "",
                    LocationCode     = inv.LocationCode,
                    LotNo            = inv.LotNo,
                    QtyOnHand        = inv.QtyOnHand,
                    UnitCost         = inv.UnitCost,
                    LastMovementDate = hasMovement ? lastDate : null,
                    IdleDays         = idleDays,
                };
            })
            .Where(r => r.IdleDays >= request.IdleDaysThreshold)
            .OrderByDescending(r => r.InventoryValue)
            .ToList();

        return new SlowMovingInventoryResult(rows, false);
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.ProductCode",   r => r.ProductCode),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.ProductName",   r => r.ProductName),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.LocationCode",  r => r.LocationCode),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.LotNo",         r => r.LotNo),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.QtyOnHand",     r => r.QtyOnHand),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.UnitCost",      r => r.UnitCost),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.InventoryValue",r => r.InventoryValue),
        // 兩個「沒有值」的表示法照抄畫面現行輸出：無異動紀錄印破折號、
        // 呆滯天數的哨兵值 9999 印 N/A（不是 9999，那會被當成真的天數）。
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.LastMovementDate",
            r => r.LastMovementDate?.ToString("yyyy-MM-dd") ?? "—"),
        Column<SlowMovingInventoryRowVm>("SlowMovingInventory.IdleDays",
            r => r.IdleDays == 9999 ? "N/A" : r.IdleDays.ToString()),
    ];
}
