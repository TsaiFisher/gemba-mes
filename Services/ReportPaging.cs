using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 報表畫面的分頁切片。
    ///
    /// 為什麼不直接改成 controller 端的 <see cref="PaginatedList{T}"/>：這些報表的統計卡與圖表
    /// 是**對整個結果集**算出來的，資料也早就在 controller 裡 materialize 成 in-memory 清單了。
    /// 若把查詢改成資料庫端分頁，那些合計會變成「只算當頁」——一個比沒有分頁更難發現的錯。
    /// 所以這裡只切明細表要顯示的那一段，合計仍用完整清單。
    ///
    /// 順手把 <c>ViewData["CurrentPageSize"]</c> 設好，讓 <c>_PageSizeSelect</c> 能正確選中目前值；
    /// 報表 controller 沒有 pageSize 參數，不設的話下拉會固定顯示預設值而與實際不符
    /// （BAS/SpecialDay 就是這樣顯示過假值）。
    /// </summary>
    public static class ReportPaging
    {
        /// <summary>與 _PageSizeSelect.cshtml 的 pageSizeOptions 同一組；不在清單內的值一律夾回預設。</summary>
        private static readonly int[] AllowedPageSizes = { 10, 20, 50, 100 };
        private const int DefaultPageSize = 20;

        public static PaginatedList<T> Page<T>(
            IEnumerable<T>? all, ViewDataDictionary viewData, HttpContext context)
        {
            var rows = all as IReadOnlyList<T> ?? all?.ToList() ?? (IReadOnlyList<T>)Array.Empty<T>();

            var pageSize = ResolvePageSize(context);

            var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize));
            var pageIndex  = Math.Clamp(Parse(context, "pageNumber", 1), 1, totalPages);

            viewData["CurrentPageSize"] = pageSize;

            var slice = rows.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();
            return new PaginatedList<T>(slice, rows.Count, pageIndex, pageSize);
        }

        /// <summary>
        /// 由網址解析每頁筆數，不在選項內一律夾回預設。
        /// <c>_PageSizeSelect</c> 在查詢表單內、比明細表**先**渲染，那時 <see cref="Page{T}"/>
        /// 還沒設 <c>ViewData["CurrentPageSize"]</c>——下拉必須靠這支取值，否則顯示預設 20，
        /// 使用者一按查詢就把 20 送回去（39 支報表都是這個順序）。
        /// </summary>
        public static int ResolvePageSize(HttpContext context)
        {
            var pageSize = Parse(context, "pageSize", DefaultPageSize);
            return AllowedPageSizes.Contains(pageSize) ? pageSize : DefaultPageSize;
        }

        private static int Parse(HttpContext context, string key, int fallback) =>
            int.TryParse(context.Request.Query[key], out var v) && v > 0 ? v : fallback;
    }
}
