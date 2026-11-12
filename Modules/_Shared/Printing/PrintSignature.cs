using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules._Shared.Printing;

/// <summary>列印單簽核區（_PrintBase）的「製作／審核／核准」三格要印的名字；空字串＝留白供手簽。</summary>
public sealed record PrintSignature(string Created, string Reviewed, string Approved)
{
    public static readonly PrintSignature Blank = new("", "", "");
}

/// <summary>
/// 依單據的簽核紀錄填列印簽核區（2026-10-28 使用者裁決）：
/// <list type="bullet">
/// <item>製作＝建單人；單據沒有建單人欄位時取送審人。</item>
/// <item>只有一位核准者 → 印在「核准」（一關簽核那個人就是最後決定者，印在「審核」會讓核准欄空著、看起來像沒核准）。</item>
/// <item>兩位以上 → 最後一位印「核准」，其餘依簽核順序印「審核」。</item>
/// <item>簽核中 → 已核准的人印「審核」，「核准」留白。</item>
/// <item>只看最近一次送審；被駁回／撤回／退回的那一輪不印（那些人並沒有核准這張單現在的內容）。</item>
/// <item>代理簽核印「代理人（代 原簽核人）」。</item>
/// </list>
/// </summary>
public sealed class PrintSignatureService(ApplicationDbContext db)
{
    public async Task<PrintSignature> ForAsync(string documentId, string? createdBy, string onBehalfFormat, string separator)
    {
        // 簽核申請／紀錄都掛著廠別與資料權限過濾；能進到 Print 就已通過單據本身的權限，
        // 這裡只認單號，不再讓「看單的人的角色」決定簽核區印不印得出來。
        var request = await db.ApprovalRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.DocumentId == documentId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();

        var approvals = new List<(string ApproverId, string? OnBehalfOf)>();
        if (request is { Status: ApprovalStatus.Pending or ApprovalStatus.Approved })
            approvals = (await db.ApprovalLogs.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.RequestId == request.Id && l.Action == ApprovalStatus.Approved)
                    .OrderBy(l => l.ActionTime)
                    .Select(l => new { l.ApproverId, l.OnBehalfOfUserId })
                    .ToListAsync())
                .Select(l => (l.ApproverId, l.OnBehalfOfUserId)).ToList();

        var creator = !string.IsNullOrEmpty(createdBy) ? createdBy : request?.RequesterId;
        var ids = approvals.SelectMany(a => new[] { a.ApproverId, a.OnBehalfOf }).Append(creator)
            .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<string, string>()
            : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);
        string Name(string? id) => id == null ? "" : names.TryGetValue(id, out var n) ? n : id;

        var approverNames = approvals
            .Select(a => a.OnBehalfOf == null ? Name(a.ApproverId) : string.Format(onBehalfFormat, Name(a.ApproverId), Name(a.OnBehalfOf)))
            .ToList();
        return Compose(Name(creator), approverNames, request?.Status == ApprovalStatus.Approved, separator);
    }

    /// <summary>
    /// 分格規則本體（純函式，供單元測試）。<paramref name="approvers"/> 依簽核先後排序；
    /// <paramref name="finished"/>＝整張單已核准完成。
    /// </summary>
    public static PrintSignature Compose(string creator, IReadOnlyList<string> approvers, bool finished, string separator)
    {
        if (approvers.Count == 0) return new PrintSignature(creator, "", "");
        if (!finished) return new PrintSignature(creator, string.Join(separator, approvers), "");
        return new PrintSignature(creator, string.Join(separator, approvers.Take(approvers.Count - 1)), approvers[^1]);
    }
}
