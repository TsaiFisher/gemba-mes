using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 月結清單「備註」裡系統產生的那一段，存成語系無關的代碼 <c>[[Key|參數|參數]]</c>，
    /// 顯示時才依當下語系從 resx 取格式套參數。
    ///
    /// <para>原本直接存中文句子（「已鎖定 3 筆工單成本為 Final。」「NRV 存貨跌價 JE-…（增提 216）」），
    /// 英文介面整欄中文，而且存進資料庫之後就沒辦法再翻。使用者自己打的備註照原文存、照原文顯示；
    /// 改版前已存的中文備註沒有代碼，也照原文顯示。</para>
    /// </summary>
    public static partial class MonthEndNote
    {
        /// <summary>產生代碼。參數以不變文化格式化；參數裡的 <c>|</c>、<c>]</c> 會被換掉以免破壞格式。</summary>
        public static string Token(string key, params object?[] args)
            => "[[" + key + string.Concat(args.Select(a => "|" + Sanitize(Convert.ToString(a, CultureInfo.InvariantCulture)))) + "]]";

        /// <summary>把備註裡的代碼換成當下語系的文字；<paramref name="localize"/> 找不到鍵就回傳 null，該段原樣保留。</summary>
        public static string Render(string? notes, Func<string, string?> localize)
        {
            if (string.IsNullOrEmpty(notes)) return notes ?? "";
            return TokenRx().Replace(notes, m =>
            {
                var format = localize(m.Groups["key"].Value);
                if (format is null) return m.Value;
                var args = m.Groups["args"].Value.Split('|', StringSplitOptions.None).Skip(1).Cast<object>().ToArray();
                try { return string.Format(CultureInfo.CurrentCulture, format, args); }
                catch (FormatException) { return m.Value; }
            });
        }

        private static string Sanitize(string? s) => (s ?? "").Replace("|", "/").Replace("]", ")");

        [GeneratedRegex(@"\[\[(?<key>\w+)(?<args>(?:\|[^|\]]*)*)\]\]")]
        private static partial Regex TokenRx();
    }
}
