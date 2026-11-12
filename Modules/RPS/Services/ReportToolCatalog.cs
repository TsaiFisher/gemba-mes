using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 一支工具加上「目前使用者可否執行」的判定結果。
///
/// 刻意保留不可執行的項目而非過濾掉：§7.9 L2 要求常用查詢清單對無權限的項目
/// **顯示「無權限」而不是隱藏**——使用者自己存的查詢突然從清單消失，會被當成資料遺失來報修。
/// </summary>
public readonly record struct ReportToolAccess(IReportToolDescriptor Tool, bool IsAuthorized);

/// <summary>
/// 報表工具目錄（§5.1）。列舉所有已登錄的 <see cref="IReportToolDescriptor"/>，
/// 供常用查詢、排程設定與未來的 LLM function-calling schema 取用。
/// </summary>
public interface IReportToolCatalog
{
    /// <summary>全部已登錄的工具，依 <c>ToolKey</c> 排序。不含權限判定。</summary>
    IReadOnlyList<IReportToolDescriptor> All { get; }

    /// <summary>依 <c>ToolKey</c> 取工具；查無回 <c>null</c>（工具被移除後舊的常用查詢仍會指向它）。</summary>
    IReportToolDescriptor? Find(string toolKey);

    /// <summary>
    /// 目前使用者可否執行該工具。查無工具一律回 <c>false</c>——
    /// 「不存在」不可被當成「不需檢查」放行。
    /// </summary>
    Task<bool> IsAuthorizedAsync(string toolKey);

    /// <summary>全部工具＋逐一權限判定，依 <c>ToolKey</c> 排序。</summary>
    Task<IReadOnlyList<ReportToolAccess>> GetAllWithAccessAsync();
}

/// <inheritdoc />
public class ReportToolCatalog : IReportToolCatalog
{
    private readonly IPermissionService _permissionService;
    private readonly Dictionary<string, IReportToolDescriptor> _byKey;
    private readonly List<IReportToolDescriptor> _ordered;

    public ReportToolCatalog(
        IEnumerable<IReportToolDescriptor> tools,
        IPermissionService permissionService)
    {
        _permissionService = permissionService;

        _ordered = tools.OrderBy(t => t.ToolKey, StringComparer.Ordinal).ToList();

        // 重複 ToolKey 是設定錯誤，必須當場炸開：ToolKey 同時是常用查詢與排程的外部識別，
        // 兩支工具搶同一鍵時「存下來的查詢會執行到哪一支」取決於註冊順序——
        // 那是會安靜給出錯誤數字的失敗，不能容忍。由 ReportToolCatalogTests 守門。
        var duplicates = _ordered
            .GroupBy(t => t.ToolKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"報表工具 ToolKey 重複：{string.Join(", ", duplicates)}。" +
                "ToolKey 必須全域唯一且對齊該報表的 MenuKey。");

        _byKey = _ordered.ToDictionary(t => t.ToolKey, StringComparer.Ordinal);
    }

    public IReadOnlyList<IReportToolDescriptor> All => _ordered;

    public IReportToolDescriptor? Find(string toolKey)
        => string.IsNullOrEmpty(toolKey) ? null
         : _byKey.TryGetValue(toolKey, out var tool) ? tool : null;

    public async Task<bool> IsAuthorizedAsync(string toolKey)
    {
        var tool = Find(toolKey);
        return tool is not null && await CheckAsync(tool);
    }

    public async Task<IReadOnlyList<ReportToolAccess>> GetAllWithAccessAsync()
    {
        var result = new List<ReportToolAccess>(_ordered.Count);
        foreach (var tool in _ordered)
            result.Add(new ReportToolAccess(tool, await CheckAsync(tool)));
        return result;
    }

    /// <summary>
    /// ⚠️ 動詞依各工具自行宣告，**不可在這裡硬寫成 <c>CanRead</c>**。
    /// 2026-08-14 裁決後 30 支報表畫面的閘門恰好都是 <c>CanRead</c>，但那是現況不是契約：
    /// 把判定寫死等於讓「某支畫面日後另立閘門」變成無痕放寬
    /// （<c>CanRead</c> 恆為 <c>CanCreate</c> 的真超集，改動不會有任何錯誤訊息）。
    ///
    /// ToolKey 同時作為 MenuKey 使用（兩者現況 1:1，由 ReportToolCatalogTests 守門）。
    /// </summary>
    private Task<bool> CheckAsync(IReportToolDescriptor tool) => tool.Permission switch
    {
        ReportToolPermission.CanRead   => _permissionService.CanRead(tool.ToolKey),
        ReportToolPermission.CanCreate => _permissionService.CanCreate(tool.ToolKey),
        ReportToolPermission.CanPrint  => _permissionService.CanPrint(tool.ToolKey),
        _ => throw new InvalidOperationException(
                 $"工具 {tool.ToolKey} 宣告了未知的權限動詞 {tool.Permission}。"),
    };
}
