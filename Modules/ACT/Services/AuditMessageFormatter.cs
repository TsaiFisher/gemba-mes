using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 把稽核發現的「訊息鍵＋參數」組成一句話。見 <see cref="AuditMessages"/> 的說明。
    /// </summary>
    public interface IAuditMessageFormatter
    {
        /// <summary>
        /// 以**目前 UI 語系**格式化。<paramref name="messageKey"/> 為空時（2026-09-21 之前產生的舊列）
        /// 回傳 <paramref name="fallback"/>——那些列只有當初存下的中文句子，沒有別的東西可用。
        /// </summary>
        string Format(string? messageKey, string? messageArgsJson, string fallback);

        /// <summary>
        /// 以**中性資源**（正體中文）格式化，供寫入 <c>ErrorDescription</c>。
        /// 存檔值不跟著顯示語系跑：它是給關帳失敗訊息、日誌與外部系統看的固定文字。
        /// </summary>
        string FormatDefault(string messageKey, IReadOnlyList<string> args);

        /// <summary>參數序列化為 JSON 陣列（存進 <c>DataAuditFinding.MessageArgs</c>）。</summary>
        static string ToJson(IReadOnlyList<string> args) => JsonSerializer.Serialize(args);
    }

    public sealed class AuditMessageFormatter : IAuditMessageFormatter
    {
        private readonly IStringLocalizer<ProjectZAuditResource> _localizer;

        public AuditMessageFormatter(IStringLocalizer<ProjectZAuditResource> localizer)
            => _localizer = localizer;

        public string Format(string? messageKey, string? messageArgsJson, string fallback)
        {
            if (string.IsNullOrWhiteSpace(messageKey)) return fallback;

            string[] args;
            try
            {
                args = string.IsNullOrWhiteSpace(messageArgsJson)
                    ? []
                    : JsonSerializer.Deserialize<string[]>(messageArgsJson) ?? [];
            }
            catch (JsonException)
            {
                // 參數壞掉不該讓整張報表開不起來；退回存檔時的句子，資訊量與舊資料相同。
                return fallback;
            }

            var template = _localizer[messageKey];
            // ResourceNotFound＝鍵被改名或漏補翻譯。回退到 fallback 而不是印出鍵名：
            // 鍵名對使用者沒有意義，存檔句子至少看得懂。
            if (template.ResourceNotFound) return fallback;

            return Safe(template.Value, args, fallback);
        }

        public string FormatDefault(string messageKey, IReadOnlyList<string> args)
        {
            // 中性資源（ProjectZAuditResource.resx）永遠存在，故這裡不做 fallback——
            // 取不到就是鍵打錯了，讓它顯眼，不要靜靜存一句怪話進資料庫。
            var template = _localizer.GetString(messageKey);
            return template.ResourceNotFound
                ? $"[{messageKey}] {string.Join(" / ", args)}"
                : Safe(template.Value, args, $"[{messageKey}] {string.Join(" / ", args)}");
        }

        /// <summary>
        /// 參數個數與模板不符時 <c>string.Format</c> 會擲 <c>FormatException</c>。
        /// 這條路徑跑在關帳閘門與每日排程上，為了一句描述讓關帳炸掉並不值得。
        /// </summary>
        private static string Safe(string template, IReadOnlyList<string> args, string fallback)
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template,
                                     args.Cast<object?>().ToArray());
            }
            catch (FormatException)
            {
                return fallback;
            }
        }
    }
}
