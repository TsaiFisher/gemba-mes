using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.QA.Controllers;

/// <summary>
/// 手冊 Q&amp;A 對話畫面（S-7；契約在 <c>.claude/commands/new-screen-projectz.md</c> §12）。
///
/// <para><b>這支控制器只做三件事：檢索、送進模型、把答案拆成可查證的形狀。</b>
/// 它刻意沒有任何寫入路徑——§12.6 第 1 條：使用者的提問與模型的答案一律不得寫進向量庫
/// （規格 §8.7「業務資料絕不可進入向量庫」）。提問裡很可能有單號、料號、客戶名，
/// 一旦落進 <c>DocChunks</c>，它就會出現在**別人**的檢索結果裡，而且沒有任何權限欄位擋得住，
/// 因為那張表的權限是靠 <c>MenuKey</c> 對應手冊頁來的。</para>
///
/// <para><b>不繞過 <see cref="ILlmClient"/> 直接打 Ollama</b>（§12.5）：<c>think:false</c>、逾時、
/// 截斷判定都在那一層。實測開著思考是 528 tokens／43.4 秒，關掉是 58 tokens／4.7 秒——
/// 繞過去等於把那 9 倍差距重新踩一次。</para>
/// </summary>
[Authorize]
public class QaAssistantController : ModuleControllerBase
{
    private const string MenuKey = "QaAssistant";

    /// <summary>
    /// 提問長度上限。CPU 推論的 prefill 時間與 prompt token 數成正比（§10.8 的字數預算同一個理由），
    /// 而超長提問還會在檢索端把 <c>ts_query</c> 撐成上百個詞。
    /// </summary>
    private const int MaxQuestionLength = 500;

    /// <summary>
    /// 資料查詢一次最多回幾列給畫面（Phase 2）。與 <c>QaToolQueryService.MaxSummaryRows</c>
    /// 是兩個不同用途的旋鈕，見 <see cref="AskData"/>。
    /// </summary>
    private const int MaxDisplayRows = 200;

    /// <summary>檢索取幾塊。與 <c>ManualGenerationBenchmarkTests</c> 的 <c>TopK</c> 一致——
    /// 畫面若用不同的值，量到的品質就不是使用者拿到的品質。</summary>
    private const int TopK = 5;

    /// <summary>
    /// 健康檢查的等待上限。**不能沿用 <c>ILlmClient</c> 的逾時**（預設 180 秒）——
    /// 那是留給生成的；拿它來開頁面，會讓「Ollama 所在主機無回應」變成整頁轉三分鐘。
    /// </summary>
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);

    // GET: /QaAssistant
    public async Task<IActionResult> Index()
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        // 關燈部署連 QaDbContext 都沒註冊（QAModule 直接 return），故一律延遲解析。
        // 建構式注入會讓手打網址變成「解析不到服務」的 500，而正確答覆是「沒有這個東西」。
        var diagnostics = HttpContext.RequestServices.GetService<IOllamaDiagnostics>();
        if (diagnostics is null) return NotFound();

        await PrepareIndexPermissionsAsync(MenuKey);

        // §17.8：關閉與降級都要出聲。三種故障（服務沒開／模型沒拉／組態填錯）在使用者眼中
        // 都是「助理不回答」，但處理方式完全不同，所以開頁面就先講清楚是哪一種。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(HealthTimeout);
        try
        {
            var health = await diagnostics.CheckAsync(cts.Token);
            ViewBag.Ready = health.Reachable && health.ChatModelPresent;
            ViewBag.HealthDetail = await DiagnosticDetailAsync(health.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            // 健康檢查失敗本身就是「未就緒」的一種，不該讓整頁開不起來——
            // 使用者仍該看得到畫面與原因，而不是一個沒有上下文的 500。
            Logger.LogWarning(ex, "推論服務健康檢查失敗。");
            ViewBag.Ready = false;
            ViewBag.HealthDetail = await DiagnosticDetailAsync(ex.Message);
        }

        return View();
    }

    /// <summary>
    /// 問一題。回傳 JSON：<c>{ body, isNoAnswer, citations[], elapsedMs, model }</c>。
    ///
    /// <para><b>整條路徑一個位元組都不落地。</b>見類別註解。</para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask(string? question)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        var search = HttpContext.RequestServices.GetService<IManualSearchService>();
        var llm = HttpContext.RequestServices.GetService<ILlmClient>();
        if (search is null || llm is null) return NotFound();

        question = (question ?? string.Empty).Trim();
        if (question.Length == 0) return JsonFail(Msg("QuestionRequired"));
        if (question.Length > MaxQuestionLength)
            return JsonFail(string.Format(Msg("QuestionTooLongFmt"), MaxQuestionLength));

        // 取消必須真的送到底（§12.5）：使用者按取消 → fetch abort → RequestAborted →
        // OllamaClient 的 HttpClient 斷線 → Ollama 停止生成。只把畫面上的東西藏起來，
        // 那顆 CPU 還要再跑五十秒，而下一題得排在它後面。
        var ct = HttpContext.RequestAborted;
        var started = DateTimeOffset.UtcNow;

        try
        {
            var hits = await search.SearchAsync(question, TopK, ct: ct);

            // **真的會進 prompt 的那幾塊**，不是檢索回傳的全部（見 SelectWithinBudget 的註解）。
            var given = QaPromptBuilder.SelectWithinBudget(hits);

            // 零命中直接拒答，不送模型。兩個理由，都不是為了省事：
            //   ① 沒有參考資料時，模型「不拒答」等於整段都是憑自己的知識編的——
            //      那是這個功能最危險的失敗，而它在這裡是可以從源頭消滅的。
            //   ② 定案模型實測 52.8 秒／題（CPU 推論），花這麼久去拿一個必然的常數字串，
            //      §12.5 說延遲是本類畫面的 UX 主軸。
            // ⚠️ 評測（ManualGenerationBenchmarkTests）**刻意不這樣做**：它要量的正是
            //    「空 context 時模型會不會亂答」。兩邊的差異僅止於此，是知情的。
            if (given.Count == 0)
                return JsonOk(new
                {
                    body = QaPromptBuilder.NoAnswer,
                    isNoAnswer = true,
                    citations = Array.Empty<QaCitationView>(),
                    elapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    model = string.Empty,
                });

            var answer = await llm.ChatAsync(
                QaPromptBuilder.SystemPrompt, QaPromptBuilder.BuildUserPrompt(question, hits), ct);

            var parts = QaPromptBuilder.Parse(answer.Text);

            // 答覆語系取自命中塊本身（檢索已對語系做硬過濾，§10.4），
            // 不取 UI 語系——英文使用者用中文提問時，拿到的是中文的塊。
            var lang = given[0].Lang;

            return JsonOk(new
            {
                body = parts.Body,
                isNoAnswer = parts.IsNoAnswer,
                citations = QaCitationPresenter.Build(parts.Citations, given, lang),
                elapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                model = answer.Model,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 使用者按了取消。連線已經斷了，回什麼都沒人收——但要留下訊號，
            // 否則「取消」與「逾時」在 log 裡會長得一樣。
            Logger.LogInformation("使用者取消了提問（等待 {Seconds:F1} 秒）。",
                (DateTimeOffset.UtcNow - started).TotalSeconds);
            return new EmptyResult();
        }
        catch (OllamaException ex)
        {
            // 推論服務的失敗要照原話講出來（§17.8）：OllamaClient 的訊息已經分好
            // 「連不上」「模型不存在」「被截斷」三種，在這裡收斂成一句通用錯誤等於把診斷丟掉。
            Logger.LogWarning(ex, "手冊 Q&A 生成失敗。");
            return JsonFail(PublicError(ex));
        }
        catch (DocIndexOutOfDateException ex)
        {
            Logger.LogWarning(ex, "手冊索引與目前的嵌入模型不一致。");
            return JsonFail(PublicError(ex));
        }
    }

    /// <summary>
    /// 問一題，逐 token 串流回來（§11.2 C4）。回傳 <c>application/x-ndjson</c>，一行一個 JSON：
    /// <c>{"stage":"…"}</c>（進到哪個階段）、<c>{"t":"…"}</c>（新增的一段文字）、
    /// <c>{"done":{…}}</c>（與 <see cref="Ask"/> 相同的完成負載）、<c>{"error":"…"}</c>（失敗）。
    ///
    /// <para><b>階段訊框只報階段，不報預估時間</b>（§11.2 C4-A）。佔掉整題七成以上的
    /// prefill，Ollama 在進行中不吐任何進度——計數器（<c>prompt_eval_count</c> 等）
    /// 只出現在最後一個回應。要在畫面上顯示倒數就只能用「token 數 ÷ 吞吐量」推估，
    /// 而吞吐量逐機器、逐模型都不同，寫死的那個數字換一台機器就會騙人。
    /// 所以這裡給的是「在做什麼、要讀多少」，不是「還要幾秒」。</para>
    ///
    /// <para><b>為什麼不是 SSE／EventSource</b>：<c>EventSource</c> 只能發 GET、不能帶自訂標頭，
    /// 於是 <c>[ValidateAntiForgeryToken]</c> 與既有的取消機制都得另外做一套。
    /// 用 <c>fetch</c> + <c>ReadableStream</c> 讀 NDJSON，防偽權杖、<c>RequestAborted</c> 取消、
    /// 權限檢查全部沿用 <see cref="Ask"/> 那條路徑，只有輸出的形狀不同。</para>
    ///
    /// <para><b>⚠️ 一旦開始輸出就不能再改 HTTP 狀態碼。</b>所以這支的失敗**一律是 200 加一行
    /// <c>error</c> 訊框**，不是 4xx/5xx——包含在第一個 token 之前就失敗的情形，
    /// 刻意不分成兩種形狀，否則畫面端要同時處理「HTTP 錯誤」與「錯誤訊框」兩條路，
    /// 而其中一條永遠很少被走到（＝很少被測到）。</para>
    ///
    /// <para><b>整條路徑一個位元組都不落地</b>，與 <see cref="Ask"/> 同一個理由（§12.6-1）。</para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AskStream(string? question)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        var search = HttpContext.RequestServices.GetService<IManualSearchService>();
        var llm = HttpContext.RequestServices.GetService<ILlmStreamClient>();
        if (search is null || llm is null) return NotFound();

        question = (question ?? string.Empty).Trim();
        if (question.Length == 0) return JsonFail(Msg("QuestionRequired"));
        if (question.Length > MaxQuestionLength)
            return JsonFail(string.Format(Msg("QuestionTooLongFmt"), MaxQuestionLength));

        var ct = HttpContext.RequestAborted;
        var started = DateTimeOffset.UtcNow;

        Response.ContentType = "application/x-ndjson; charset=utf-8";

        // 不加這一行，反向代理與中介層的緩衝會把「逐 token」重新攢成「一次全出」——
        // 那正是本功能要消滅的症狀，而且它不會有任何錯誤訊息。
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            // 階段訊框（§11.2 C4-A）。**只在真的會花掉可觀時間的邊界送**：
            // 檢索要先把提問送去嵌入（一次 Ollama 往返），之後的 prompt 組裝是字串串接，
            // 微秒等級——為它再送一格只會讓畫面閃一下，看起來像 bug 而不是進度。
            await WriteFrameAsync(new { stage = "search" }, ct);

            var hits = await search.SearchAsync(question, TopK, ct: ct);
            var given = QaPromptBuilder.SelectWithinBudget(hits);

            // 零命中直接拒答，不送模型——理由與 Ask 完全相同，見該處註解。
            if (given.Count == 0)
            {
                await WriteFrameAsync(new
                {
                    done = new
                    {
                        body = QaPromptBuilder.NoAnswer,
                        isNoAnswer = true,
                        citations = Array.Empty<QaCitationView>(),
                        elapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                        model = string.Empty,
                    },
                }, ct);
                return new EmptyResult();
            }

            var lang = given[0].Lang;
            LlmAnswer? final = null;

            // 先組好再送訊框：prompt 的**字數**是這一格唯一有價值的資訊。
            // Ollama 在 prefill 進行中不吐任何進度（計數器只在最後一個回應才有），
            // 所以這裡刻意不報時間、只報「要讀多少」——報時間就得寫死吞吐量，
            // 而那個數字逐機器逐模型都不同，換一台機器就是在騙人。
            var userPrompt = QaPromptBuilder.BuildUserPrompt(question, hits);
            await WriteFrameAsync(
                new { stage = "infer", chunks = given.Count, chars = userPrompt.Length }, ct);

            await foreach (var delta in llm.ChatStreamAsync(
                               QaPromptBuilder.SystemPrompt, userPrompt, ct))
            {
                if (delta.Final is { } done) { final = done; break; }
                await WriteFrameAsync(new { t = delta.Text }, ct);
            }

            // 列舉跑完卻沒拿到 Final：ChatStreamAsync 保證這種情形會拋，走不到這裡。
            // 留著是因為「半截答案被當成完整答案」是本功能最難看出來的壞法（見 LlmDelta）。
            if (final is not { } answer)
            {
                await WriteFrameAsync(new { error = Msg("StreamIncomplete") }, ct);
                return new EmptyResult();
            }

            var parts = QaPromptBuilder.Parse(answer.Text);

            // body 仍然整段回傳一次：畫面上累積的是**原始輸出**（含最後那行來源），
            // 而 Parse 之後的本文才是該留在畫面上的東西。讓伺服端給出權威版本，
            // 畫面就不必自己複製一份「怎麼把來源行拿掉」的規則——那份規則遲早會與這裡分歧。
            await WriteFrameAsync(new
            {
                done = new
                {
                    body = parts.Body,
                    isNoAnswer = parts.IsNoAnswer,
                    citations = QaCitationPresenter.Build(parts.Citations, given, lang),
                    elapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    model = answer.Model,
                },
            }, ct);

            return new EmptyResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogInformation("使用者取消了提問（串流，等待 {Seconds:F1} 秒）。",
                (DateTimeOffset.UtcNow - started).TotalSeconds);
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is OllamaException or DocIndexOutOfDateException)
        {
            Logger.LogWarning(ex, "手冊 Q&A 串流生成失敗。");
            await WriteFrameAsync(new { error = PublicError(ex) }, CancellationToken.None);
            return new EmptyResult();
        }
    }

    /// <summary>
    /// 寫一行 NDJSON 並**立刻 flush**。少了 flush，內容會留在 <c>PipeWriter</c> 裡等湊滿，
    /// 於是伺服端明明逐 token 寫出，畫面還是一次收到一大塊——症狀與沒做串流幾乎一樣。
    /// </summary>
    private async Task WriteFrameAsync<T>(T frame, CancellationToken ct)
    {
        await Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(frame, StreamJson) + "\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>NDJSON 的序列化設定。camelCase 與畫面端既有的欄位名一致。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions StreamJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// 用自然語言查資料（Phase 2，§9.1）。回傳 JSON：
    /// <c>{ outcome, answer, toolKey, paramsJson, headers[], rows[][], note, truncated, elapsedMs }</c>。
    ///
    /// <para><b>與 <see cref="Ask"/> 是兩條完全不同的路徑，刻意不合併成一支。</b>
    /// 手冊問答讀的是 <c>DocChunks</c>（沒有廠別、沒有資料層級過濾，靠 MenuKey 對應手冊頁）；
    /// 本動作讀的是**營運資料**，權限、廠別、資料層級全部走既有機制。
    /// 合成一支就得在裡面判斷「這一題是哪一種」——而判錯的方向是把營運資料當手冊資料處理。</para>
    ///
    /// <para><b>參數一定回傳給畫面</b>（<c>paramsJson</c>）：實測模型會自行填入問題裡
    /// 沒提到的參數（§11.1），而它填的值看起來完全合理。回顯是使用者唯一能發現這件事的地方（§9.2-3）。</para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AskData(string? question)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        var svc = HttpContext.RequestServices.GetService<IQaToolQueryService>();
        if (svc is null) return NotFound();

        question = (question ?? string.Empty).Trim();
        if (question.Length == 0) return JsonFail(Msg("QuestionRequired"));
        if (question.Length > MaxQuestionLength)
            return JsonFail(string.Format(Msg("QuestionTooLongFmt"), MaxQuestionLength));

        var ct = HttpContext.RequestAborted;
        var started = DateTimeOffset.UtcNow;

        try
        {
            var r = await svc.AskAsync(question, User, ct);

            // 回畫面的列數上限。**與摘要的 30 列是兩個不同的旋鈕**：那個管的是 prefill 耗時，
            // 這個管的是「一次往瀏覽器丟多少 JSON」。報表工具不分頁（分頁是畫面自己做的），
            // 一支存貨評估在真實資料上就是幾千列——整包丟過去會讓瀏覽器卡住，
            // 而症狀是「問完之後畫面沒反應」，看起來像推論還沒回來。
            var rows = r.Rows;
            var truncated = rows is not null && rows.Count > MaxDisplayRows;
            if (truncated) rows = rows!.Take(MaxDisplayRows).ToList();

            return JsonOk(DataPayload(r, rows, truncated, started));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogInformation("使用者取消了資料查詢（等待 {Seconds:F1} 秒）。",
                (DateTimeOffset.UtcNow - started).TotalSeconds);
            return new EmptyResult();
        }
        catch (ReportParamsInvalidException ex)
        {
            // 模型產的參數與工具契約對不起來。**這一段刻意不重試、也不改用預設值**——
            // §7.0.1-c 的理由對模型產的參數比對排程產的更強：靜默用預設值執行會產出
            // 一份看起來正常、期間卻不是使用者問的那一段的報表。
            Logger.LogWarning(ex, "模型產生的參數無法還原成請求。");
            return JsonFail(PublicError(ex));
        }
        catch (OllamaException ex)
        {
            Logger.LogWarning(ex, "資料查詢的推論階段失敗。");
            return JsonFail(PublicError(ex));
        }
    }

    /// <summary>
    /// 確認並執行前一輪解析出來的查詢（§11.3 第二輪）。回傳形狀與 <see cref="AskData"/> 完全相同——
    /// 畫面因此只需要一段渲染程式碼，不必為「確認過的結果」另寫一份。
    ///
    /// <para><paramref name="turnId"/> 是第一輪回傳的憑據；<paramref name="toolKey"/> 是使用者
    /// 從候選中挑的那一支（不送＝沿用模型選的）。參數編修以 <c>p_&lt;欄位名&gt;</c> 的形式
    /// 從表單取得——**刻意不接受整包 JSON**：那會讓確認畫面變成一個比模型還寬的參數入口，
    /// 而 <c>plantId</c>／<c>dateBasis</c>／<c>sortBy</c> 正是刻意不讓自然語言碰的三個欄位
    /// （<c>QaToolSchemaBuilder.Excluded</c>）。服務層還會再依 schema 過濾一次，這裡只是不先收。</para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmData(long turnId, string? toolKey)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        var svc = HttpContext.RequestServices.GetService<IQaToolQueryService>();
        if (svc is null) return NotFound();

        var ct = HttpContext.RequestAborted;
        var started = DateTimeOffset.UtcNow;

        var edits = Request.Form.Keys
            .Where(k => k.StartsWith(ParamPrefix, StringComparison.Ordinal))
            .ToDictionary(
                k => k[ParamPrefix.Length..],
                k => (string?)Request.Form[k].ToString(),
                StringComparer.Ordinal);

        try
        {
            var r = await svc.ConfirmAsync(turnId, toolKey, edits, User, ct);

            var rows = r.Rows;
            var truncated = rows is not null && rows.Count > MaxDisplayRows;
            if (truncated) rows = rows!.Take(MaxDisplayRows).ToList();

            return JsonOk(DataPayload(r, rows, truncated, started));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogInformation("使用者取消了資料查詢的確認（等待 {Seconds:F1} 秒）。",
                (DateTimeOffset.UtcNow - started).TotalSeconds);
            return new EmptyResult();
        }
        catch (ReportParamsInvalidException ex)
        {
            // 使用者在回顯畫面上改出了工具收不了的值（或列舉挑了不存在的選項）。
            // 與 AskData 同一個處置：不重試、不改用預設值執行（§7.0.1-c）。
            Logger.LogWarning(ex, "確認後的參數無法還原成請求。");
            return JsonFail(PublicError(ex));
        }
        catch (OllamaException ex)
        {
            Logger.LogWarning(ex, "資料查詢確認後的推論階段失敗。");
            return JsonFail(PublicError(ex));
        }
    }

    /// <summary>
    /// 把剛才那一次自然語言查詢**另存為常用查詢**（§十三 Phase 3）。
    ///
    /// <para><b>閉環的另一半在 Phase 0-B 就有了</b>（§7.9 L2 的「儲存此查詢」）。
    /// Phase 3 只是多一個入口，**刻意共用 <c>ISavedQueryService.SaveAsync</c> 那條路徑**：
    /// 權限、白名單過濾、同名覆寫、期間必填的規則因此只有一份。
    /// 另寫一條「NL 專用的存檔」會長出第二套規則，而兩套的差異會落在
    /// 「用問的存下來的」與「用點的存下來的」之間——最不該有差異的地方。</para>
    ///
    /// <para><b>🚨 憑據是 <paramref name="turnId"/>，不是畫面送回來的工具鍵與參數。</b>
    /// 收下客戶端給的參數等於讓「另存」變成一個可以憑空寫入任意查詢定義的入口，
    /// 而那張定義日後可能被掛上排程、定期寄出。只有真的跑出過結果的那一輪存得起來——
    /// 這同時滿足 §9.2-3「另存為排程報表之前必須先回顯確認」。</para>
    ///
    /// <para><b>存成的是常用查詢（<c>ScheduleKind = null</c>），不是排程。</b>
    /// 排程還要頻率、收件人、輸出語系，那些自然語言裡沒有；§7.9 給的路徑是
    /// 「先存常用查詢 → 用順了 → 到排程畫面加排程」。</para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveQuery(long turnId, string? name)
    {
        if (!await Permissions.CanRead(MenuKey)) return Forbid();

        var turns = HttpContext.RequestServices.GetService<IQaTurnStore>();
        var saved = HttpContext.RequestServices.GetService<ISavedQueryService>();
        if (turns is null || saved is null) return NotFound();

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return JsonFail(Msg("SavedQuery_NameRequired"));

        var ct = HttpContext.RequestAborted;

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        if (await turns.FindExecutedAsync(turnId, userId, ct) is not { } executed)
            return JsonFail(Msg("QaSaveQueryTurnGone"));

        var t = ToolParamsToSavedQuery.Translate(executed.ToolKey, name, executed.ParamsJson);
        if (t.ErrorMessage is not null) return JsonFail(SavedQueryMsg(t.ErrorMessage));

        // SaveAsync 會再檢查一次目標報表的權限（存不下自己執行不了的查詢），
        // 所以這裡不重複檢查——重複的那一份遲早與它分歧。
        var result = await saved.SaveAsync(t.Input, ct);
        if (!result.Success) return JsonFail(SavedQueryMsg(result.ErrorMessage));

        // 🚨 被丟掉的條件一定要講出來。畫面查詢列表達不了的條件本來就帶不過去，
        //    但不講的話，使用者會以為這張常用查詢等於他剛才問的那一題——
        //    而它其實**範圍更寬**，下次點開數字會比當初大，且沒有任何跡象。
        var dropped = t.Dropped.Count == 0
            ? null
            : string.Format(Msg("QaSaveQueryDroppedFmt"), string.Join("、", t.Dropped));

        return JsonOk(new { id = result.Id, name, dropped },
            dropped is null ? Msg("SavedQuery_Saved") : Msg("SavedQuery_Saved") + dropped);
    }

    /// <summary>
    /// 常用查詢的錯誤鍵（<c>SavedQuery_*</c>）轉在地化字串。
    /// 那些鍵是 <c>SavedQueryService</c> 的既有契約，共用同一批 resx——
    /// 在這裡另寫一套訊息，會讓同一個失敗在兩個入口說不同的話。
    /// </summary>
    private string SavedQueryMsg(string? key) =>
        string.IsNullOrEmpty(key) ? Msg("SavedQuery_LoadFailed") : Msg(key);

    /// <summary>參數編修的表單欄位前綴。畫面與伺服端共用同一個常數。</summary>
    public const string ParamPrefix = "p_";

    /// <summary>
    /// <see cref="AskData"/> 與 <see cref="ConfirmData"/> 共用的回傳負載。
    /// **兩支必須回一模一樣的形狀**——形狀分歧的代價不是錯誤而是畫面上少了一塊東西
    /// （例如確認過的結果忘了回顯查詢條件），沒有任何測試會自己發現。
    /// </summary>
    private object DataPayload(
        QaToolQueryResult r, IReadOnlyList<object?[]>? rows, bool truncated, DateTimeOffset started) =>
        new
        {
            outcome = r.Outcome.ToString(),

            // 兩個結局的服務層不帶文字（它沒有 IStringLocalizer，也不該有）。
            // 在這裡補上在地化訊息，畫面因此不必自己判斷 outcome 該顯示什麼——
            // 那份判斷一旦分成兩處，新增結局時漏掉的那一處會是空白氣泡。
            answer = r.Answer.Length > 0 ? r.Answer : r.Outcome switch
            {
                QaToolQueryOutcome.ConfirmExpired => Msg("QaConfirmExpired"),
                QaToolQueryOutcome.TooManyRounds => Msg("QaTooManyRounds"),

                // 訊息指向組態、不指向使用者：他有權限、問法也沒錯，
                // 錯的是這台機器現在被設定成把資料送到不被信任的端點（§17.11.8）。
                QaToolQueryOutcome.EgressBlocked => Msg("QaEgressBlocked"),
                _ => r.Answer,
            },
            toolKey = r.ToolKey,
            paramsJson = r.ParamsJson,
            headers = r.Headers,
            rows,
            totalRows = r.Rows?.Count ?? 0,
            truncated,
            note = r.Note,
            elapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,

            // ── §11.3 追問 ────────────────────────────────────────────────
            turnId = r.TurnId,
            confirmReason = r.ConfirmReason.ToString(),

            // 🚨 確認提示的文字在**伺服端**在地化。畫面自己依 confirmReason 選字串的話，
            //    「為什麼要問」這件事就有兩份定義，而它正是使用者判斷要不要往下按的依據。
            confirmHint = ConfirmHint(r.ConfirmReason),

            // 候選工具與它們的參數 schema：畫面靠這個把 enum 畫成下拉、其餘畫成輸入框。
            // 沒有它，回顯就只剩一段唯讀 JSON，使用者發現得了問題卻改不動（§9.2-3 要的是可修正）。
            candidates = (r.Candidates ?? []).Select(c => new
            {
                toolKey = c.Definition.Name,
                description = c.Definition.Description,
                paramsJson = c.ParamsJson,
                properties = c.Definition.Properties.ToDictionary(
                    kv => kv.Key,
                    kv => new { type = kv.Value.Type, enumValues = kv.Value.EnumValues, format = kv.Value.Format }),
            }),
        };

    /// <summary>需要確認的理由對應的說明文字。<c>None</c> 回空字串，畫面據此決定顯不顯示。</summary>
    private string ConfirmHint(QaConfirmReason reason) => reason switch
    {
        QaConfirmReason.Monetary => Msg("QaConfirmMonetary"),
        QaConfirmReason.MultipleTools => Msg("QaConfirmMultipleTools"),
        _ => string.Empty,
    };

    /// <summary>
    /// 手動重建索引（§12.7）。
    ///
    /// <para><b>閘門是 <c>CanUpdate</c> 而不是 <c>CanRead</c></b>，這也是 <c>QA_ASSISTANT</c>
    /// 矩陣裡 HQ 是 Full、MGR 只有 Read 的實際差別：問問題人人可做，重建索引是維運動作——
    /// 它會打滿一顆 CPU 幾分鐘，並與所有人的提問搶同一個推論槽（§11.2）。</para>
    ///
    /// <para>做的事與啟動時那一輪完全相同（重建 → 匯入擷取物 → 補嵌入），
    /// 因為在這個畫面出現之前，重建的唯一觸發方式就是重啟應用程式。</para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rebuild()
    {
        if (!await Permissions.CanUpdate(MenuKey)) return Forbid();

        var builder = HttpContext.RequestServices.GetService<IDocIndexBuilder>();
        var importer = HttpContext.RequestServices.GetService<IExtractManifestImporter>();
        if (builder is null || importer is null) return NotFound();

        var ct = HttpContext.RequestAborted;
        try
        {
            var result = await builder.RebuildAsync(ct);

            // 正式機的正常結果是「什麼都沒做」——manifest 不進 publish 產物，
            // 目錄不存在時匯入器直接返回且一列都不刪（§10.11.6）。
            await importer.ImportAsync(ct);

            // 補嵌入是這一輪唯一會花上幾分鐘的部分。中途被取消時 pending > 0，
            // 而增量是可續的：再按一次會從沒嵌到的那些繼續，不會重做已完成的。
            var (embedded, pending) = await builder.EmbedPendingAsync(ct);

            return JsonOk(new
            {
                filesScanned = result.FilesScanned,
                filesReindexed = result.FilesReindexed,
                chunksWritten = result.ChunksWritten,
                embedded,
                pending,
            }, string.Format(Msg("RebuildDoneFmt"),
                result.FilesReindexed, result.ChunksWritten, embedded, pending));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (OllamaException ex)
        {
            // 詞彙索引此時已經寫完了——它不需要任何模型（見 IDocIndexBuilder 的註解）。
            // 訊息要講清楚這件事，否則會被當成「整個重建失敗」而重按一次。
            Logger.LogWarning(ex, "補嵌入失敗，詞彙索引已完成。");
            return JsonFail(Msg("RebuildEmbedFailed") + ex.Message);
        }
    }

    /// <summary>
    /// 取共用資源字串。控制器回傳的訊息會直接顯示在畫面上，不得硬編碼中文（R7）。
    /// 沿用 <see cref="ModuleControllerBase"/> 的延遲解析慣例，不走建構式注入。
    /// </summary>
    /// <summary>
    /// 回給使用者的錯誤訊息（安全評估 2026-10-07 #5）。完整的例外訊息只進 log——
    /// 推論服務的原話含端點位址、模型名稱與組態鍵，對一般使用者沒有用處，卻描繪了內部架構。
    /// <para>推論忙碌例外照原話：它是操作指引（「請等上一題完成」「改用報表畫面」），不含內部資訊。</para>
    /// </summary>
    private string PublicError(Exception ex) => PublicErrorOf(ex, Msg);

    internal static string PublicErrorOf(Exception ex, Func<string, string> msg) => ex switch
    {
        InferenceBusyException => ex.Message,
        DocIndexOutOfDateException => msg("QaIndexOutOfDate"),
        ReportParamsInvalidException => msg("QaParamsInvalid"),
        _ => msg("QaInferenceUnavailable"),
    };

    /// <summary>
    /// 健康檢查的細節（服務沒開／模型沒拉／組態填錯）只給能重建索引的維護者看；
    /// 其他人只看到「尚未就緒」——細節對他們沒有可採取的動作，卻含端點與模型名稱。
    /// </summary>
    private async Task<string?> DiagnosticDetailAsync(string? detail) =>
        await Permissions.CanUpdate(MenuKey) ? detail : Msg("QaNotReadyContactAdmin");

    private string Msg(string key) =>
        HttpContext.RequestServices.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>()[key];
}
