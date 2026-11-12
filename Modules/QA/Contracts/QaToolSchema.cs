namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 一支工具在 function-calling schema 裡的形狀（§11.1）。
///
/// <para>屬性刻意**全為參考型別**：R5b 會掃 <c>Modules.*</c> 下 public class 的
/// 非可空實值型別屬性並要求 resx 雙語鍵，而這是序列化契約、不是表單模型，
/// 補鍵等於造假欄位名。</para>
/// </summary>
public sealed record QaToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, QaToolProperty> Properties);

/// <summary>單一參數的 JSON Schema 片段。</summary>
/// <param name="Type">JSON 型別：<c>string</c>／<c>integer</c>／<c>number</c>／<c>boolean</c>。</param>
/// <param name="EnumValues">
/// 合法值。**這一欄是準確率的關鍵**：2026-08-21 實測，模型在有 <c>enum</c> 的欄位
/// （18 個值的 <c>period</c>）三題全對，而沒有 <c>enum</c> 的 <c>sortBy</c> 被憑空編出
/// <c>"salesAmount"</c>（§11.1 實測更正）。<c>null</c> ＝ 該型別沒有列舉值。
/// </param>
/// <param name="Format">JSON Schema 的 <c>format</c>，目前只用 <c>date</c>。</param>
public sealed record QaToolProperty(
    string Type,
    IReadOnlyList<string>? EnumValues = null,
    string? Format = null);

/// <summary>
/// 組出**已通過 L1 權限過濾**的工具 schema（§8.4、§11.1）。
/// </summary>
public interface IQaToolSchemaBuilder
{
    /// <summary>
    /// ⚠️ 回傳的清單**只含目前使用者有權限的工具**。
    ///
    /// <para><b>過濾必須發生在這裡（組 schema），不是在執行工具的時候。</b>
    /// 2026-08-21 實測目錄外提問時，模型拒答的同時**把它看得到的工具全部列給使用者**
    /// ——放到執行階段才擋，工具目錄本身就已經洩漏了（§11.1 實測更正）。</para>
    /// </summary>
    Task<IReadOnlyList<QaToolDefinition>> BuildAsync(CancellationToken ct = default);
}
