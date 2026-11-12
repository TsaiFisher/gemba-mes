using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA;

/// <summary>
/// <see cref="InferenceGate"/> 的守門測試（規格 §11.2 C1／C2／C5）。
///
/// <para><b>這一類程式最容易的壞法是「看起來有在限流」。</b>名額少還一次、
/// 例外路徑忘了把計數還回去、背景車道其實沒讓路——三者都不會有錯誤訊息，
/// 症狀一律是「系統偶爾特別慢」或「過一陣子就沒人問得動了」。
/// 所以下面每一支都直接量狀態，不量「有沒有拋例外」。</para>
/// </summary>
public class InferenceGateTests
{
    /// <summary>
    /// 60 秒僅作卡住偵測，不是延遲門檻；逾時原因須另行確認。
    /// 呼叫端同步環境阻塞取消完成的缺陷，由可控制排程的回歸測試直接驗證。
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 等一個非同步結果落地；逾時就以**說得出真相的訊息**失敗。
    ///
    /// <para>🚨 <b>為什麼不直接用 <c>Task.WaitAsync</c> 加上限</b>：它逾時拋的是
    /// <see cref="TimeoutException"/>。在「取消要傳遞得到」那一支裡，斷言等的是
    /// <see cref="OperationCanceledException"/>，於是紅字寫的是**例外型別不符**——
    /// 看起來像「閘門沒聽取消」，而真相多半是機器忙。
    /// 分辨這兩件事的成本，會全部落在下一個看到紅字的人身上。</para>
    /// </summary>
    private static async Task<T> Settled<T>(Task<T> task, string what)
    {
        await ReachedAsync(task, what);
        return await task;
    }

    /// <summary>取消必須傳遞得到，但**逾時不等於沒傳遞到**——兩者要分開報。</summary>
    private static async Task AssertCanceledAsync(Task task, string what)
    {
        await ReachedAsync(task, what);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static async Task ReachedAsync(Task task, string what)
    {
        if (ReferenceEquals(await Task.WhenAny(task, Task.Delay(Settle)), task)) return;

        Assert.Fail($"{what}：等了 {Settle.TotalSeconds:F0} 秒仍未完成。" +
            "請檢查取消、名額釋放與排程；不可僅憑逾時判定為機器忙碌。");
    }

    private static IConfiguration Config(int? maxConcurrent = null, int? maxQueue = null)
    {
        var d = new Dictionary<string, string?>();
        if (maxConcurrent is not null)
            d[InferenceGate.MaxConcurrentKey] = maxConcurrent.Value.ToString();
        if (maxQueue is not null)
            d[InferenceGate.MaxQueueDepthKey] = maxQueue.Value.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    /// <summary>
    /// 假的 HTTP 情境。<paramref name="user"/> 為 <c>null</c> 時模擬「沒有登入使用者」
    /// （背景服務、健康檢查），此時每使用者限制不該套用。
    /// </summary>
    private sealed class FakeHttp : IHttpContextAccessor
    {
        public FakeHttp(string? user)
        {
            var ctx = new DefaultHttpContext();
            if (user is not null)
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, user) }, "test"));
            HttpContext = ctx;
        }

        public HttpContext? HttpContext { get; set; }
    }

    // ── C1：名額 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 名額用完時_後來的會等在閘門而不是一起衝出去()
    {
        var gate = new InferenceGate(Config(maxConcurrent: 1), new FakeHttp(null));

        var first = await gate.EnterAsync(InferenceLane.Interactive);
        var second = gate.EnterAsync(InferenceLane.Interactive);

        // 名額只有一個，第二個必須還沒完成。
        Assert.False(second.IsCompleted, "第二個請求沒有等在閘門——名額根本沒生效。");

        first.Dispose();
        var lease = await Settled(second, "名額釋放後的第二個請求");
        lease.Dispose();
    }

    /// <summary>
    /// **名額歸還必須恰好一次。** 多還一次的症狀是限流靜靜失效（名額憑空變多），
    /// 少還一次是閘門慢慢自我鎖死。兩種都沒有錯誤訊息，所以這裡連 dispose 兩次一起量。
    /// </summary>
    [Fact]
    public async Task 租約重複dispose_不會讓名額憑空變多()
    {
        var gate = new InferenceGate(Config(maxConcurrent: 1), new FakeHttp(null));

        var lease = await gate.EnterAsync(InferenceLane.Interactive);
        lease.Dispose();
        lease.Dispose();          // ← 重複歸還

        var a = await gate.EnterAsync(InferenceLane.Interactive);
        var b = gate.EnterAsync(InferenceLane.Interactive);

        Assert.False(b.IsCompleted,
            "重複 dispose 之後名額變多了——限流已經失效，而且不會有任何錯誤訊息。");

        a.Dispose();
        (await Settled(b, "重複 dispose 之後的第二個請求")).Dispose();
    }

    // ── C1：佇列深度 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 深度滿了要**立刻說忙**，不是默默排下去。每人一則的限制擋在前面，
    /// 所以這裡必須用不同使用者才測得到深度本身。
    /// </summary>
    [Fact]
    public async Task 佇列深度滿了_立刻回忙碌而不是無限排隊()
    {
        var http = new FakeHttp("u0");
        var gate = new InferenceGate(Config(maxConcurrent: 1, maxQueue: 1), http);

        var running = await gate.EnterAsync(InferenceLane.Interactive);   // 佔住唯一名額

        SetUser(http, "u1");
        var queued = gate.EnterAsync(InferenceLane.Interactive);          // 排進深度 1
        Assert.False(queued.IsCompleted);

        SetUser(http, "u2");
        var busy = await Assert.ThrowsAsync<InferenceBusyException>(
            () => gate.EnterAsync(InferenceLane.Interactive));

        // 訊息要指得出路：報表畫面那條路不經過推論。
        Assert.Contains("報表畫面", busy.Message);

        running.Dispose();
        (await Settled(queued, "排隊中的請求")).Dispose();
    }

    /// <summary>
    /// **被擋下來的那個不能留下痕跡。** 例外路徑忘了把計數還回去，
    /// 閘門就會一次比一次早說忙，最後誰也進不來——而每一次的外觀都只是「系統很忙」。
    /// </summary>
    [Fact]
    public async Task 被擋下來之後_閘門回得到原狀()
    {
        var http = new FakeHttp("u0");
        var gate = new InferenceGate(Config(maxConcurrent: 1, maxQueue: 0), http);

        var running = await gate.EnterAsync(InferenceLane.Interactive);

        SetUser(http, "u1");
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<InferenceBusyException>(
                () => gate.EnterAsync(InferenceLane.Interactive));

        running.Dispose();

        // 三次被擋之後，閘門必須完全乾淨：拿得到、而且拿得到的只有一個。
        var again = await Settled(gate.EnterAsync(InferenceLane.Interactive), "三次被擋之後的重新進場");
        again.Dispose();
    }

    // ── C5：每使用者一則 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 同一個使用者同時問第二題_立刻被擋()
    {
        var http = new FakeHttp("alice");
        var gate = new InferenceGate(Config(maxConcurrent: 4, maxQueue: 4), http);

        var first = await gate.EnterAsync(InferenceLane.Interactive);

        var ex = await Assert.ThrowsAsync<InferenceBusyException>(
            () => gate.EnterAsync(InferenceLane.Interactive));
        Assert.Contains("取消", ex.Message);   // 訊息要告訴他怎麼脫身

        // 換一個人就進得來——被擋的是這個人，不是整個系統。
        SetUser(http, "bob");
        var bob = await Settled(gate.EnterAsync(InferenceLane.Interactive), "換一個使用者的進場");

        first.Dispose();
        bob.Dispose();
    }

    /// <summary>
    /// 一題要**兩次**推論（選工具、寫摘要），中間會還一次名額。
    /// 每使用者限制若是「一題只准進一次」，第二次就會把自己擋在門外，
    /// 而症狀是每一題都固定降級成 `SummarySkipped`——看起來像模型不穩。
    /// </summary>
    [Fact]
    public async Task 同一個使用者先後兩次推論_第二次不會被自己擋住()
    {
        var http = new FakeHttp("alice");
        var gate = new InferenceGate(Config(maxConcurrent: 1), http);

        (await gate.EnterAsync(InferenceLane.Interactive)).Dispose();
        var second = await Settled(gate.EnterAsync(InferenceLane.Interactive), "同一個使用者的第二次推論");
        second.Dispose();
    }

    [Fact]
    public async Task 沒有登入使用者時_不套用每使用者限制()
    {
        // 背景服務與測試都走這條。回 null 是正確行為不是漏網：
        // 背景工作走背景車道，已經被讓路規則管住了。
        var gate = new InferenceGate(Config(maxConcurrent: 2), new FakeHttp(null));

        var a = await gate.EnterAsync(InferenceLane.Interactive);
        var b = await Settled(gate.EnterAsync(InferenceLane.Interactive), "沒有登入使用者時的第二個請求");

        a.Dispose();
        b.Dispose();
    }

    // ── C2：背景讓路 ─────────────────────────────────────────────────────────

    /// <summary>
    /// **背景不得在互動車道有人時進場**（§11.2 C2）。手冊一改就重嵌，
    /// 那正好與提問搶同一顆 CPU；沒有這條，使用者的提問會排在幾百個切塊後面。
    /// </summary>
    [Fact]
    public async Task 互動車道有人排隊時_背景不進場()
    {
        var http = new FakeHttp("alice");
        var gate = new InferenceGate(Config(maxConcurrent: 1), http);

        var interactive = await gate.EnterAsync(InferenceLane.Interactive);

        var background = gate.EnterAsync(InferenceLane.Background);
        await Task.Delay(300);   // 大於一個 poll 週期，確認它真的是在等而不是還沒排到
        Assert.False(background.IsCompleted, "互動查詢還在跑，背景嵌入卻已經進場了。");

        interactive.Dispose();
        (await Settled(background, "互動車道清空後的背景進場")).Dispose();
    }

    [Fact]
    public async Task 互動車道沒人時_背景照常進場()
    {
        var gate = new InferenceGate(Config(maxConcurrent: 1), new FakeHttp(null));

        var lease = await Settled(gate.EnterAsync(InferenceLane.Background), "互動車道沒人時的背景進場");
        lease.Dispose();
    }

    /// <summary>
    /// 背景在等的時候必須聽得進取消——否則停用功能或關機時，
    /// 重建索引那條路會卡在讓路迴圈裡不放手。
    /// </summary>
    [Fact]
    public async Task 背景等待中可被取消()
    {
        var http = new FakeHttp("alice");
        var gate = new InferenceGate(Config(maxConcurrent: 1), http);

        using var interactive = await gate.EnterAsync(InferenceLane.Interactive);
        using var cts = new CancellationTokenSource();

        var background = gate.EnterAsync(InferenceLane.Background, cts.Token);
        cts.Cancel();

        await AssertCanceledAsync(background, "背景等待中的取消傳遞");
    }

    [Theory]
    [InlineData(InferenceLane.Interactive)]
    [InlineData(InferenceLane.Background)]
    public async Task 等待取消不依賴呼叫端同步環境排程(InferenceLane lane)
    {
        var gate = new InferenceGate(Config(maxConcurrent: 1), new FakeHttp(null));
        using var running = await gate.EnterAsync(InferenceLane.Interactive);
        using var cts = new CancellationTokenSource();
        var context = new PausedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task<IDisposable> waiting;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            waiting = gate.EnterAsync(lane, cts.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.False(waiting.IsCompleted);
        try
        {
            cts.Cancel();
            // A captured continuation reports its dependency immediately, so the old
            // implementation fails without waiting for the watchdog or a busy CI runner.
            await Settled(Task.WhenAny(waiting, context.Posted.Task), "不依賴呼叫端排程的取消");
            Assert.True(waiting.IsCompleted,
                "取消仍等待呼叫端 SynchronizationContext 執行 continuation。");
            await AssertCanceledAsync(waiting, "取消結果");
        }
        finally
        {
            context.Drain();
        }
    }

    private sealed class PausedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public TaskCompletionSource<bool> Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            Posted.TrySetResult(true);
        }

        public void Drain()
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (_pending.TryDequeue(out var item)) item.Callback(item.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    // ── 繼承關係是有作用的 ───────────────────────────────────────────────────

    /// <summary>
    /// <see cref="InferenceBusyException"/> 必須仍是 <see cref="OllamaException"/>。
    /// **這不是分類學**：`QaToolQueryService` 的摘要降級與 `QaAssistantController`
    /// 的錯誤回覆都在攔 `OllamaException`，繼承斷掉的話，忙碌會變成
    /// 一個沒人接的例外——整題失敗（連查到的表格都丟掉），而不是降級成 SummarySkipped。
    /// </summary>
    [Fact]
    public void 忙碌例外仍是Ollama例外()
    {
        Assert.IsAssignableFrom<OllamaException>(new InferenceBusyException("x"));
    }

    private static void SetUser(FakeHttp http, string name) =>
        http.HttpContext!.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, name) }, "test"));
}
