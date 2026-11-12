using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 報表的「每頁筆數」下拉（_PageSizeSelect）放在查詢表單內，而表單在明細表**之前**渲染——
/// 渲染到下拉時 <see cref="ReportPaging.Page{T}"/> 還沒跑，<c>ViewData["CurrentPageSize"]</c>
/// 還是空的。下拉若只認 ViewData 就會顯示預設 20：畫面是 50 筆、下拉卻寫 20，
/// 再按一次查詢就把 pageSize=20 送回去，看起來像「按查詢會被切回 20」。
/// 所以下拉在 ViewData 還沒設時，必須用與 Page 同一套規則從網址解析。
/// </summary>
public class ReportPagingTests
{
    private static HttpContext Ctx(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query);
        return ctx;
    }

    [Theory]
    [InlineData("?pageSize=50", 50)]
    [InlineData("?pageSize=100", 100)]
    [InlineData("?pageSize=10", 10)]
    [InlineData("", 20)]
    [InlineData("?pageSize=31", 20)]   // 不在選項內：夾回預設，否則下拉沒有任何 option 被選中
    [InlineData("?pageSize=abc", 20)]
    [InlineData("?pageSize=0", 20)]
    public void ResolvePageSize_UsesQueryWithSameRulesAsPage(string query, int expected)
    {
        Assert.Equal(expected, ReportPaging.ResolvePageSize(Ctx(query)));
    }

    [Fact]
    public void ResolvePageSize_AgreesWithPage()
    {
        var ctx = Ctx("?pageSize=50");
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());

        // 下拉先渲染（此時只能靠 ResolvePageSize），明細表後渲染（Page）——兩者必須一致。
        var shownInSelect = ReportPaging.ResolvePageSize(ctx);
        var paged = ReportPaging.Page(Enumerable.Range(1, 120), viewData, ctx);

        Assert.Equal(paged.Count, shownInSelect);   // 120 列的第一頁：筆數即每頁筆數
        Assert.Equal(50, viewData["CurrentPageSize"]);
    }
}
