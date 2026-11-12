using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFactory.MES.Modules.RPS.Contracts;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 一次翻譯的結果。<paramref name="Dropped"/> 為空才代表「這張常用查詢等於那一題」。
/// </summary>
/// <param name="Dropped">
/// 沒有帶進常用查詢的工具參數名。**呼叫端必須把它顯示出來**——
/// 見 <see cref="ToolParamsToSavedQuery"/> 的類別註解。
/// </param>
public readonly record struct SavedQueryTranslation(
    SavedQueryInput Input,
    IReadOnlyList<string> Dropped,
    string? ErrorMessage);

/// <summary>
/// 把一次自然語言查詢用過的**工具參數**翻成**常用查詢**的輸入（§十三 Phase 3）。
///
/// <para>存在的理由是兩套詞彙不同：工具參數描述查詢語意（<c>customerCode</c>），
/// 常用查詢存的是目標畫面的網址參數（<c>searchCustomer</c>）。對照表在
/// <see cref="ReportScreenBinding.ParamMap"/>。</para>
///
/// <para><b>🚨 對不上的參數一律丟掉，而且一定要回報丟了哪些。</b>
/// 常用查詢的入口是報表畫面（§7.9-6），畫面查詢列表達不了的條件本來就帶不過去。
/// 但靜默丟掉的話，使用者會以為那張常用查詢等於他剛才問的那一題——
/// 而它其實**範圍更寬**，數字會比當初看到的大，且沒有任何跡象。</para>
///
/// <para><b>放在 RPS 而不是 QA</b>：畫面查詢列有哪些欄位是 MVC 這一側的事，
/// QA 不該知道 <c>searchProduct</c> 這種名字存在（與 <see cref="ReportScreenBindings"/>
/// 當初不放進 <c>IReportToolDescriptor</c> 是同一個界線）。</para>
/// </summary>
public static class ToolParamsToSavedQuery
{
    /// <summary>期間欄位在工具參數裡的名字。與 <c>ReportRequestBase.Period</c> 的 camelCase 一致。</summary>
    private const string PeriodKey = "period";

    /// <summary>
    /// 翻譯。<paramref name="toolParamsJson"/> 解不開或工具沒有畫面繫結時，
    /// <c>ErrorMessage</c> 給值（資源鍵，與 <c>SavedQueryService</c> 的錯誤鍵同一套）。
    /// </summary>
    public static SavedQueryTranslation Translate(string toolKey, string name, string? toolParamsJson)
    {
        if (ReportScreenBindings.Find(toolKey) is not { } binding)
            return new(default, [], "SavedQuery_UnknownTool");

        JsonObject obj;
        try
        {
            obj = JsonNode.Parse(string.IsNullOrWhiteSpace(toolParamsJson) ? "{}" : toolParamsJson)
                      as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new(default, [], "SavedQuery_ParamsInvalid");
        }

        RelativePeriod? period = null;
        var filters = new Dictionary<string, string?>(StringComparer.Ordinal);
        var dropped = new List<string>();

        foreach (var (key, node) in obj)
        {
            var raw = node?.ToString();

            // 空值不算條件，也不算被丟掉的條件——它本來就沒有限縮任何東西。
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (string.Equals(key, PeriodKey, StringComparison.Ordinal))
            {
                // 列舉一律以**名稱**解析（§7.9 落地紀錄第 3 點）：以序數存下來的舊查詢，
                // 在 RelativePeriod 中間插一個成員之後會默默指向另一個期間。
                if (Enum.TryParse<RelativePeriod>(raw, ignoreCase: false, out var p)) period = p;
                else dropped.Add(key);
                continue;
            }

            if (binding.ParamMap?.TryGetValue(key, out var filterKey) == true
                && binding.FilterKeys.Contains(filterKey, StringComparer.Ordinal))
            {
                filters[filterKey] = raw;
                continue;
            }

            // 對不上的一律丟掉並回報。**不要試著猜**：猜錯的方向是把一個
            // 語意不同的欄位塞進去，而畫面照樣打得開，只是查的不是同一件事。
            dropped.Add(key);
        }

        return new(new SavedQueryInput(toolKey, name, period, filters), dropped, null);
    }
}
