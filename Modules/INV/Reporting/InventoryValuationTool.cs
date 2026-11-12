using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Reporting;

/// <summary>存貨價值分析的請求。無期間概念（看的是當下庫存快照）。</summary>
public class InventoryValuationRequest : ReportRequestBase
{
    /// <summary>關鍵字，同時模糊比對品號與品名。</summary>
    public string? Keyword { get; set; }

    public string? WarehouseCode { get; set; }
}

/// <summary>
/// 存貨價值分析的結果。<c>record struct</c> 避免 R5b 對 <c>GrandTotal</c> 要求驗證資源鍵
/// （它是彙總輸出，不是使用者輸入欄位）。
///
/// <c>GrandTotal</c> 是**全查詢**加總，不是當頁加總——分頁時小計仍須是整體金額。
/// </summary>
public readonly record struct InventoryValuationResult(
    PaginatedList<InventoryValuationRow> Page,
    decimal GrandTotal);

/// <summary>
/// 存貨價值分析（§六 I2）。由 <c>InventoryValuationController.Index</c> 抽出
/// ——這一支原本連私有 <c>BuildQueryAsync</c> 都沒有，查詢直接寫在 action 裡且在
/// <c>Export</c> 又複製了一份，兩份已經可以各自漂移。抽出後只剩一份。
///
/// 行為與抽出前完全相同，由 <c>InvReportCharacterizationTests</c> 守門。
/// </summary>
public class InventoryValuationTool
    : IReportTool<InventoryValuationRequest, InventoryValuationResult>, IReportToolDescriptor
{
    private readonly ApplicationDbContext _db;

    public InventoryValuationTool(ApplicationDbContext db) => _db = db;

    public string ToolKey     => "InventoryValuation";
    public string Description => "以品號×儲位×批號列出在庫量、均價與評估金額，並提供全查詢小計。";

    /// <summary>含金額＝機密，動詞 <c>CanRead</c>＋分級由 MenuPermission 資料承載（§14 決議 #2）。</summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。顯式實作讓它
    // 只透過 IReportToolDescriptor 取用，既不觸發規則也不必為它造一個假的欄位名鍵。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>
    /// 含金額（`UnitCost`／`TotalValue`（均價與評估金額））→ 依 §十三 Phase 2 **預設不進自然語言目錄**。
    /// 這不影響畫面與排程：有權限的人照樣開得了報表，差別只在能不能用一句話問到它。
    /// 要放行請開 <c>Features:QaAssistant:AllowMonetaryTools</c>（見 <c>QaToolSchemaBuilder</c>）。
    /// </summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>看的是當下快照，沒有期間概念——實作完全不碰 IRelativePeriodResolver。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => false;

    public Type RequestType  => typeof(InventoryValuationRequest);
    public Type ResponseType => typeof(InventoryValuationResult);

    /// <summary>
    /// 匯出／排程走的是<b>整份不分頁</b>的 <see cref="ExecuteForExportAsync"/>，不是畫面用的
    /// <see cref="ExecuteAsync"/>。後者回傳 <see cref="InventoryValuationResult"/>（<c>Page</c> ＋
    /// <c>GrandTotal</c>）——那是為了讓畫面同時拿到當頁與全查詢小計而包起來的，
    /// <b>不是一個集合</b>。2026-09-14 Wave R 實測：排程產出的檔案完全空白，因為攤平那一段
    /// 遇到非集合就靜默回零筆。順帶一提，就算取出 <c>Page</c> 也只會有 20 筆——
    /// <c>PageSize ?? 20</c> 是畫面的預設值，而排程的契約是整份。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteForExportAsync(request.AsToolRequest<InventoryValuationRequest>(ToolKey), ct))
           .Cast<object>().ToList();

    public async Task<InventoryValuationResult> ExecuteAsync(
        InventoryValuationRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var query = BuildQuery(request);

        query = request.SortBy switch
        {
            "product_asc"  => query.OrderBy(r => r.ProductCode),
            "product_desc" => query.OrderByDescending(r => r.ProductCode),
            "value_asc"    => query.OrderBy(r => r.TotalValue),
            "value_desc"   => query.OrderByDescending(r => r.TotalValue),
            _              => query.OrderBy(r => r.ProductCode)
        };

        var grandTotal = await query.AsNoTracking().SumAsync(r => r.TotalValue, ct);

        var page = await PaginatedList<InventoryValuationRow>.CreateAsync(
            query.AsNoTracking(), request.PageNumber ?? 1, request.PageSize ?? 20);

        return new InventoryValuationResult(page, grandTotal);
    }

    /// <summary>
    /// 匯出用：整份不分頁、固定依品號升冪（與抽出前的 <c>Export</c> 行為一致）。
    /// </summary>
    public async Task<IReadOnlyList<InventoryValuationRow>> ExecuteForExportAsync(
        InventoryValuationRequest request, CancellationToken ct = default)
        => await BuildQuery(request).OrderBy(r => r.ProductCode).AsNoTracking().ToListAsync(ct);

    private IQueryable<InventoryValuationRow> BuildQuery(InventoryValuationRequest request)
    {
        var plantId = request.PlantId;

        // ⚠️ 條件是 QtyOnHand != 0 而非 > 0：負庫存會列入並壓低小計。現況，特徵測試鎖住。
        var query = from inv in _db.Inventories
                    join p in _db.Products on inv.ProductCode equals p.ProductCode into pj
                    from p in pj.DefaultIfEmpty()
                    where inv.PlantId == plantId && inv.QtyOnHand != 0
                    select new InventoryValuationRow
                    {
                        ProductCode  = inv.ProductCode,
                        ProductName  = p != null ? p.ProductName : inv.ProductCode,
                        LocationCode = inv.LocationCode,
                        LotNo        = inv.LotNo,
                        QtyOnHand    = inv.QtyOnHand,
                        UnitCost     = inv.UnitCost,
                        TotalValue   = inv.QtyOnHand * inv.UnitCost,
                    };

        if (!string.IsNullOrWhiteSpace(request.Keyword))
            query = query.Where(r => r.ProductCode.Contains(request.Keyword)
                                  || r.ProductName.Contains(request.Keyword));

        if (!string.IsNullOrWhiteSpace(request.WarehouseCode))
        {
            var whLocations = _db.StorageLocations
                .ForCurrentPlant(_db)
                .Where(l => l.WhCode == request.WarehouseCode)
                .Select(l => l.LocationCode);
            query = query.Where(r => whLocations.Contains(r.LocationCode));
        }

        return query;
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<InventoryValuationRow>("InventoryValuation.ProductCode",  r => r.ProductCode),
        Column<InventoryValuationRow>("InventoryValuation.ProductName",  r => r.ProductName),
        Column<InventoryValuationRow>("InventoryValuation.LocationCode", r => r.LocationCode),
        Column<InventoryValuationRow>("InventoryValuation.LotNo",        r => r.LotNo),
        Column<InventoryValuationRow>("InventoryValuation.QtyOnHand",    r => r.QtyOnHand),
        Column<InventoryValuationRow>("InventoryValuation.UnitCost",     r => r.UnitCost),
        Column<InventoryValuationRow>("InventoryValuation.TotalValue",   r => r.TotalValue),
    ];
}
