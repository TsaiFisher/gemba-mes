using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Models;

namespace SmartFactory.MES.Modules.QA.Services;

/// <inheritdoc />
public class QaTurnStore : IQaTurnStore
{
    private readonly QaDbContext _db;

    public QaTurnStore(QaDbContext db) => _db = db;

    public async Task<long?> CreateAsync(QaTurnDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(draft.UserId)) return null;

        var row = new QaTurn
        {
            ThreadId = draft.ThreadId,
            RoundNo = draft.RoundNo,
            UserId = draft.UserId,
            // 提問長度上限由 QaAssistantController.MaxQuestionLength 擋在前面，
            // 但服務層也可能被整合測試以外的路徑呼叫；截斷比讓 DbUpdateException
            // 把整題打掉合理——這一列是稽核，不是答案。
            Question = Truncate(draft.Question, 500),
            Outcome = draft.Outcome,
            ToolKey = draft.ToolKey,
            ParamsJson = draft.ParamsJson,
            CandidatesJson = draft.CandidatesJson,
            RowsReturned = draft.RowsReturned,
            CreatedAt = DateTime.UtcNow,
            ResolvedAt = draft.Resolved ? DateTime.UtcNow : null,
        };

        _db.QaTurns.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task<QaTurnPending?> FindPendingAsync(
        long turnId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        // 🚨 UserId 一定要進 where，不是查出來再比對——兩者在正確的情形下等價，
        // 但只有前者能讓「忘了比對」變成查不到資料，而不是靜默放行（§11.3）。
        var row = await _db.QaTurns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Id == turnId && t.UserId == userId && t.ResolvedAt == null, ct);

        if (row is null) return null;

        return new QaTurnPending(
            row.Id, row.ThreadId, row.RoundNo, row.Question, row.ToolKey, row.ParamsJson,
            ParseCandidates(row.CandidatesJson));
    }

    public async Task<QaTurnExecuted?> FindExecutedAsync(
        long turnId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        // 只認真的跑出過結果的那兩種結局。NeedsConfirm／NoTool／ToolRejected 都不算——
        // 前者連查都還沒查，後兩者根本沒有工具可存。
        var row = await _db.QaTurns
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == turnId
                                      && t.UserId == userId
                                      && t.ToolKey != null
                                      && (t.Outcome == ExecutedAnswered || t.Outcome == ExecutedSummarySkipped), ct);

        return row is null ? null : new QaTurnExecuted(row.Id, row.ToolKey!, row.ParamsJson ?? "{}");
    }

    /// <summary>
    /// 「已執行」的兩種結局字面。**寫成常數是因為它們要進 EF 查詢**——
    /// 在 <c>Where</c> 裡呼叫 <c>nameof</c> 沒問題，但寫成陣列再 <c>Contains</c>
    /// 會讓這一段變得比它該有的樣子難讀，而它是一道授權邊界。
    /// </summary>
    private const string ExecutedAnswered = nameof(Contracts.QaToolQueryOutcome.Answered);
    private const string ExecutedSummarySkipped = nameof(Contracts.QaToolQueryOutcome.SummarySkipped);

    public async Task ResolveAsync(
        long turnId, string userId, string outcome, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return;

        // ResolvedAt == null 也在條件裡：已定局的列不得被改寫，否則重放一次
        // 就能把一輪已經執行過的查詢改成別的結局，稽核紀錄跟著失真。
        await _db.QaTurns
            .Where(t => t.Id == turnId && t.UserId == userId && t.ResolvedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.Outcome, outcome)
                      .SetProperty(t => t.ResolvedAt, DateTime.UtcNow), ct);
    }

    public Task<int> TrimAsync(DateTime cutoffUtc, CancellationToken ct = default) =>
        _db.QaTurns.Where(t => t.CreatedAt < cutoffUtc).ExecuteDeleteAsync(ct);

    /// <summary>
    /// 候選工具鍵的還原。**格式不合法時回空清單而不是拋例外**：這一欄只影響
    /// 「確認那一輪允許選哪幾支」，而該輪本來就會再跑一次完整權限檢查（§8.7）。
    /// 為了一欄壞掉的稽核資料讓使用者的查詢整個失敗，代價不對。
    /// </summary>
    internal static IReadOnlyList<QaTurnCandidate> ParseCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<QaTurnCandidate[]>(json, CandidateJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>候選清單的序列化設定。camelCase 與寫入端（<see cref="SerializeCandidates"/>）必須是同一份。</summary>
    private static readonly JsonSerializerOptions CandidateJson = new(JsonSerializerDefaults.Web);

    /// <summary>候選清單的序列化。與 <see cref="ParseCandidates"/> 成對，故放在同一個類別。</summary>
    internal static string? SerializeCandidates(IReadOnlyList<QaTurnCandidate> candidates) =>
        candidates.Count == 0 ? null : JsonSerializer.Serialize(candidates, CandidateJson);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
