using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// CodeEntry 語系顯示名稱輔助擴充。
    /// 語言邏輯集中於此；語系直接讀 CultureInfo.CurrentUICulture（由 RequestLocalization cookie 驅動），
    /// Controller 不需傳任何參數，View 一律使用 DisplayName。
    /// </summary>
    public record CodeEntryOption(string Code, string DisplayName);

    public static class CodeEntryDisplayExtensions
    {
        /// <summary>
        /// 依當前 UI 語系回傳 List&lt;CodeEntryOption&gt;（Code + DisplayName）。
        /// en-US 且 NameEn 有值時顯示英文，否則顯示中文。
        /// </summary>
        public static async Task<List<CodeEntryOption>> ToDisplayOptionsAsync(
            this IQueryable<CodeEntry> query)
        {
            var items = await query
                .Select(e => new { e.Code, e.NameZh, e.NameEn })
                .ToListAsync();

            bool useEn = CultureInfo.CurrentUICulture.Name == "en-US";
            return items.Select(e => new CodeEntryOption(
                e.Code,
                useEn && !string.IsNullOrEmpty(e.NameEn) ? e.NameEn! : e.NameZh
            )).ToList();
        }

        /// <summary>
        /// 依當前 UI 語系回傳 Dictionary&lt;Code, DisplayName&gt;，供 Index 列表顯示名稱對照用。
        /// </summary>
        public static async Task<Dictionary<string, string>> ToDisplayDictionaryAsync(
            this IQueryable<CodeEntry> query)
        {
            var options = await query.ToDisplayOptionsAsync();
            return options.ToDictionary(o => o.Code, o => o.DisplayName);
        }
    }
}
