using System.Reflection;
using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.RPS.Contracts;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 由 <see cref="IReportToolDescriptor.RequestType"/> 反射產生 function-calling schema，
/// 並套用 L1 權限過濾（§8.4、§11.1）。
/// </summary>
public class QaToolSchemaBuilder : IQaToolSchemaBuilder
{
    /// <summary>
    /// 是否允許含金額的工具進入自然語言目錄。**預設 <c>false</c>**，依 §十三 Phase 2：
    /// 「機密等級最高的報表（銷售金額、毛利、成本）第一版排除在 NL 目錄外，只留畫面入口。」
    ///
    /// <para><b>這道閘門與 L1 權限過濾是兩件事，兩者都在。</b>L1 回答「這個人能不能看」，
    /// 本閘門回答「這支能不能用自然語言問到」——後者對**有權限的人也生效**，
    /// 因為 §8.5 的彙總洩漏是 NL 入口獨有的風險：走畫面要一步步點，走 NL 是一句話。</para>
    ///
    /// <para>要放行就把它設成 <c>true</c>（單一組態鍵，改完重啟即可），
    /// 這是刻意做成一個旋鈕而不是散在各處的判斷。</para>
    /// </summary>
    public const string AllowMonetaryKey = "Features:QaAssistant:AllowMonetaryTools";

    private readonly IReportToolAccessBatch _access;
    private readonly bool _allowMonetary;

    public QaToolSchemaBuilder(IReportToolAccessBatch access, IConfiguration configuration)
    {
        _access = access;
        _allowMonetary = configuration.GetValue(AllowMonetaryKey, false);
    }

    /// <summary>
    /// **不送給模型的參數。** 每一筆都有具體理由，不是「先簡化」：
    ///
    /// <list type="bullet">
    /// <item><c>PlantId</c>——🚨 **安全**。<c>null</c> ＝依執行 scope 的授權範圍，
    ///   **不是**「查全部廠」（見 <c>ReportRequestBase.PlantId</c> 註解）。
    ///   讓模型填廠別等於給它一個跨廠查詢的參數，而使用者可以用自然語言指定。</item>
    /// <item><c>TimeZoneId</c>——系統決定。讓模型填會讓「今天」差一天（§5.2.1）。</item>
    /// <item><c>PageNumber</c>／<c>PageSize</c>——由呼叫端／排程控制，不是語意的一部分。</item>
    /// <item><c>SortBy</c>——⚠️ 沒有列舉值可宣告，而實測正是這一欄被憑空編出
    ///   <c>"salesAmount"</c>（§11.1）。各工具的 <c>Description</c> 已載明預設排序
    ///   （「依金額降冪排名」），移掉它不損失表達力。</item>
    /// <item><c>AbsolutePeriod</c>——複合型別（<c>DateRange</c>），且 <c>Period</c> 依契約
    ///   **優先**於它。這一輪不展開巢狀 object schema；要支援明確日期區間時再加。</item>
    /// <item><c>DateBasis</c>——🚨 **2026-08-21 live 實測後追加**。問「上個月哪個客戶買最多」
    ///   （完全沒提到日期基準），模型自己填了 <c>dateBasis:"OrderDate"</c>。
    ///   而 <c>Default</c> 的語意是「由各工具自行決定其慣用基準＝維持該報表現行行為」——
    ///   模型一填就默默改掉了報表行為，**而數字看起來完全合理、也自洽**。
    ///   這正是 §9.3 點名最難發現的那一類錯誤。使用者要指定基準時走畫面，不走 NL。</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        nameof(ReportRequestBase.PlantId),
        nameof(ReportRequestBase.TimeZoneId),
        nameof(ReportRequestBase.PageNumber),
        nameof(ReportRequestBase.PageSize),
        nameof(ReportRequestBase.SortBy),
        nameof(ReportRequestBase.AbsolutePeriod),
        nameof(ReportRequestBase.DateBasis),
    };

    public async Task<IReadOnlyList<QaToolDefinition>> BuildAsync(CancellationToken ct = default)
    {
        var access = await _access.GetAccessAsync(ct);

        return access
            .Where(a => a.IsAuthorized)          // ← L1 過濾，發生在組 schema 的階段
            // §十三 Phase 2 的機密閘門。**同樣發生在組 schema 的階段**，理由與 L1 一字不差：
            // 模型拒答時會把它看得到的工具全部列給使用者看（§11.1 實測），
            // 放到執行階段擋，工具目錄本身就已經洩漏了。
            .Where(a => _allowMonetary || !a.Tool.ContainsMonetaryValues)
            .Select(a => new QaToolDefinition(
                a.Tool.ToolKey,
                a.Tool.Description,
                BuildProperties(a.Tool.RequestType)))
            .ToList();
    }

    private static IReadOnlyDictionary<string, QaToolProperty> BuildProperties(Type requestType)
    {
        var result = new Dictionary<string, QaToolProperty>(StringComparer.Ordinal);

        foreach (var p in requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Excluded.Contains(p.Name)) continue;
            if (!p.CanWrite) continue;

            var prop = Describe(p.PropertyType);
            if (prop is null) continue;          // 不支援的型別一律略過，不猜

            // JSON 慣例是 camelCase，且 Ollama 回傳的 arguments 也是這個形狀。
            result[Camel(p.Name)] = prop;
        }

        return result;
    }

    private static QaToolProperty? Describe(Type t)
    {
        // 可空實值型別解包：int? 與 int 對模型而言是同一件事（差別在 required，本輪不宣告 required）。
        t = Nullable.GetUnderlyingType(t) ?? t;

        if (t.IsEnum)
            return new QaToolProperty("string", Enum.GetNames(t));

        if (t == typeof(string))   return new QaToolProperty("string");
        if (t == typeof(bool))     return new QaToolProperty("boolean");
        if (t == typeof(DateOnly) || t == typeof(DateTime))
            return new QaToolProperty("string", null, "date");

        if (t == typeof(int) || t == typeof(long) || t == typeof(short))
            return new QaToolProperty("integer");

        if (t == typeof(decimal) || t == typeof(double) || t == typeof(float))
            return new QaToolProperty("number");

        return null;
    }

    private static string Camel(string s)
        => string.IsNullOrEmpty(s) || char.IsLower(s[0]) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
