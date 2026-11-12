using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 推論工作的兩條車道（規格 §11.2 C1／C2）。
///
/// <para>分車道**不是為了公平**，是因為兩者的等待成本完全不對稱：
/// 互動查詢後面有一個人盯著畫面，建索引後面沒有。</para>
/// </summary>
public enum InferenceLane
{
    /// <summary>使用者的提問。有人在等，優先。</summary>
    Interactive,

    /// <summary>
    /// 建索引的批次嵌入。**只在互動車道完全沒有人時才進場**，
    /// 而且在每一批之間重新排隊——手冊一改就重嵌（§10.3），
    /// 那正好會與提問搶同一顆 CPU／同一張卡。
    /// </summary>
    Background,
}

/// <summary>
/// 系統忙碌而**沒有真的送出推論**時拋出。
///
/// <para><b>刻意繼承 <see cref="OllamaException"/>。</b> 既有的兩個接住點
/// （<c>QaToolQueryService</c> 的摘要降級、<c>QaAssistantController</c> 的錯誤回覆）
/// 都已經在攔 <see cref="OllamaException"/>，繼承讓忙碌自動走上正確的路：
/// 摘要階段被擋 → <c>SummarySkipped</c>（表格照樣給，不是整題失敗），
/// 選工具階段被擋 → 使用者看到一句看得懂的話。**這條繼承關係是有作用的，不是分類學。**</para>
/// </summary>
public class InferenceBusyException : OllamaException
{
    public InferenceBusyException(string message) : base(message) { }
}

/// <summary>
/// 推論並發閘門（規格 §11.2 C1／C2／C5／C6）。
/// </summary>
public interface IInferenceGate
{
    /// <summary>
    /// 取得一個推論名額。回傳值 dispose 時歸還。
    /// 忙碌到不該再排隊時拋 <see cref="InferenceBusyException"/>，**不會**默默等下去。
    /// </summary>
    Task<IDisposable> EnterAsync(InferenceLane lane, CancellationToken ct = default);
}

/// <summary>
/// 單機的推論閘門實作。
///
/// <para><b>為什麼應用層還要再擋一次。</b>Ollama 自己有 <c>OLLAMA_NUM_PARALLEL</c> 與
/// <c>OLLAMA_MAX_QUEUE</c>，但它滿載時回的是 HTTP 503——對使用者而言是一句技術錯誤，
/// 而且在那之前應用層還會一直放行、把請求堆到對面去。擋在這裡才能給出一句人話，
/// 也才能決定**誰**先被擋（§11.2 C1）。</para>
///
/// <para>⚠️ <b>這兩個值必須自己對齊 Ollama 的環境變數</b>，程式**驗證不了**——
/// Ollama 的 API 不吐它自己的 <c>OLLAMA_NUM_PARALLEL</c>。訂得比對面大，
/// 多出來的那些照樣會撞上 503；訂得比對面小只是保守，不會壞。所以預設取小的那一邊。</para>
///
/// <para><b>每使用者只准一則在飛（§11.2 C5）。</b>沒有這一條，全域佇列深度會重演
/// Core 速率限制註解裡記著的那個坑：「全域單一計數器 → 任一匿名者打滿即令全站無法使用」。
/// 一個人開五個分頁就能把深度吃光，其他人全部收到忙碌。
/// 這同時也壓低了 §8.5 A2 的差分攻擊速率——問一題要幾十秒，一次只能問一題。</para>
///
/// <para>⚠️ <b>不跨實例（§11.2 C6）。</b>專案沒有 Redis／分散式快取，屬單機部署假設。
/// 日後 scale-out，這裡的計數會各算各的，等於限制放大 N 倍。</para>
/// </summary>
public sealed class InferenceGate : IInferenceGate
{
    public const string MaxConcurrentKey = "Features:QaAssistant:MaxConcurrentInference";
    public const string MaxQueueDepthKey = "Features:QaAssistant:MaxQueueDepth";

    /// <summary>
    /// 預設 1。**CPU 推論下這不是保守，是唯一合理值**——同一顆 CPU 上跑兩題，
    /// 兩題都變慢，總吞吐不會變好，只是兩個人一起等。有 GPU 且已調高
    /// <c>OLLAMA_NUM_PARALLEL</c> 時才該往上調。
    /// </summary>
    public const int DefaultMaxConcurrent = 1;

    /// <summary>
    /// 預設 4（不含正在跑的那些）。再多就是把「等不到」偽裝成「很慢」——
    /// 一題要幾十秒，排第五個的人等到的時候早就換去用畫面了。
    /// </summary>
    public const int DefaultMaxQueueDepth = 4;

    /// <summary>背景車道重新探詢互動車道的間隔。</summary>
    private const int BackgroundPollMs = 250;

    private readonly SemaphoreSlim _slots;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger<InferenceGate>? _log;
    private readonly int _maxConcurrent;
    private readonly int _maxQueueDepth;

    /// <summary>互動車道**排隊中＋執行中**的總數。背景車道靠它決定要不要讓路。</summary>
    private int _interactivePending;

    /// <summary>目前各有一則在飛的使用者。值本身沒用到，要的是 key 的唯一性。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _busyUsers = new(StringComparer.Ordinal);

    public InferenceGate(IConfiguration configuration,
                         IHttpContextAccessor? http = null,
                         ILogger<InferenceGate>? log = null)
    {
        _maxConcurrent = Positive(configuration.GetValue<int?>(MaxConcurrentKey), DefaultMaxConcurrent);
        _maxQueueDepth = AtLeastZero(configuration.GetValue<int?>(MaxQueueDepthKey), DefaultMaxQueueDepth);
        _slots = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
        _http = http;
        _log = log;
    }

    public async Task<IDisposable> EnterAsync(InferenceLane lane, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Gate completion must not wait for the caller's synchronization context
        // (for example a saturated test runner) to process cancellation or release.
        return lane == InferenceLane.Background
            ? await EnterBackgroundAsync(ct).ConfigureAwait(false)
            : await EnterInteractiveAsync(ct).ConfigureAwait(false);
    }

    private async Task<IDisposable> EnterInteractiveAsync(CancellationToken ct)
    {
        var owner = OwnerKey();

        // ① 每使用者一則。**先於佇列深度檢查**——否則同一個人就能把深度吃光，
        //    後面那些即使會被擋，也已經先把別人擠出去了。
        if (owner is not null && !_busyUsers.TryAdd(owner, 0))
            throw new InferenceBusyException(
                "你已經有一則提問正在推論中，請等它完成或按取消後再問下一題。");

        var pending = Interlocked.Increment(ref _interactivePending);
        try
        {
            // ② 佇列深度。已經在跑的不算排隊，所以上限是「名額＋深度」。
            if (pending > _maxConcurrent + _maxQueueDepth)
                throw new InferenceBusyException(
                    $"推論服務忙碌中（已有 {pending - 1} 則在排隊）。請稍後再試，" +
                    "或直接使用報表畫面查詢——那條路不經過推論。");

            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 沒拿到名額就得把兩個計數都還回去，否則閘門會慢慢自我鎖死。
            Interlocked.Decrement(ref _interactivePending);
            if (owner is not null) _busyUsers.TryRemove(owner, out _);
            throw;
        }

        return new Lease(this, InferenceLane.Interactive, owner);
    }

    private async Task<IDisposable> EnterBackgroundAsync(CancellationToken ct)
    {
        // 讓路：互動車道只要還有人（排隊或執行中）就不進場。
        //
        // ⚠️ 這是**禮讓不是保證**：讓完路到真的拿到名額之間，仍可能有互動請求插進來。
        // 不用鎖去消除那個縫隙，是因為呼叫端本來就是分批進來的
        //（`DocIndexBuilder` 每 `EmbedBatchSize` 筆一次），每一批的邊界就是一次讓路點；
        // 為了那個縫隙把兩條車道綁進同一把鎖，代價是背景重建會擋住互動查詢——
        // 那正好是這整段要避免的事。
        while (Volatile.Read(ref _interactivePending) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(BackgroundPollMs, ct).ConfigureAwait(false);
        }

        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(this, InferenceLane.Background, null);
    }

    /// <summary>
    /// 使用者識別。**沒有 HTTP 情境就回 <c>null</c>**（背景服務、測試），
    /// 代表「不套用每使用者限制」——那是正確行為不是漏網：背景工作走的是背景車道，
    /// 本來就已經被讓路規則管住了。
    /// </summary>
    private string? OwnerKey()
    {
        var user = _http?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        // 用 name 而非 NameIdentifier：本專案的 Identity 兩者都在，name 在日誌裡看得懂，
        // 而這裡的用途是限流不是授權，不需要不可變的主鍵。
        var name = user.Identity.Name;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private void Exit(InferenceLane lane, string? owner)
    {
        _slots.Release();
        if (lane != InferenceLane.Interactive) return;

        Interlocked.Decrement(ref _interactivePending);
        if (owner is not null) _busyUsers.TryRemove(owner, out _);
    }

    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;
    private static int AtLeastZero(int? value, int fallback) => value is >= 0 ? value.Value : fallback;

    /// <summary>
    /// 名額的租約。**dispose 必須恰好一次**——多還一次會讓名額憑空變多，
    /// 少還一次會讓閘門永久少一個名額，兩種都不會有錯誤訊息。
    /// </summary>
    private sealed class Lease : IDisposable
    {
        private readonly InferenceGate _gate;
        private readonly InferenceLane _lane;
        private readonly string? _owner;
        private int _disposed;

        public Lease(InferenceGate gate, InferenceLane lane, string? owner)
        {
            _gate = gate;
            _lane = lane;
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _gate.Exit(_lane, _owner);
        }
    }
}

/// <summary>
/// 不限流的閘門。**只給既有單元測試用**——它們量的是 HTTP 行為（逾時翻譯、前綴、錯誤訊息），
/// 排隊行為由 <c>InferenceGateTests</c> 單獨量。正式路徑一律注入真的那一支。
/// </summary>
public sealed class NullInferenceGate : IInferenceGate
{
    public static readonly NullInferenceGate Instance = new();

    private static readonly IDisposable NoOp = new Lease();

    public Task<IDisposable> EnterAsync(InferenceLane lane, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NoOp);
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }
}
