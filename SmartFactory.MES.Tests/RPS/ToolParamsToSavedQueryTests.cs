using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 工具參數 → 常用查詢的翻譯（§十三 Phase 3）。
///
/// <para><b>這一支守的是一個沒有錯誤訊息的失敗</b>：對照表漏一筆，那個條件就靜靜地
/// 不會被存進常用查詢，而查詢照樣存得起來、照樣點得開，只是**範圍比使用者問的那一題寬**，
/// 數字比當初看到的大。所以「有沒有回報丟掉了什麼」與「翻對了沒有」同等重要。</para>
/// </summary>
public class ToolParamsToSavedQueryTests
{
    [Fact]
    public void 對照表的每個目標欄位_都必須在白名單內()
    {
        // ParamMap 指到一個不在 FilterKeys 裡的名字，SaveAsync 會在白名單那一關把它濾掉，
        // **而且不會有任何訊息**——症狀就是「那個條件永遠存不進去」。
        foreach (var (toolKey, binding) in ReportScreenBindings.All)
        {
            foreach (var (param, filter) in binding.ParamMap ?? new Dictionary<string, string>())
            {
                Assert.True(binding.FilterKeys.Contains(filter),
                    $"{toolKey} 的 ParamMap 把 {param} 對到 {filter}，但它不在 FilterKeys 內。");
            }
        }
    }

    [Fact]
    public void 期間以列舉名稱解析_不是序數()
    {
        var t = ToolParamsToSavedQuery.Translate(
            "SalesRanking", "上月排行", """{"period":"LastMonth"}""");

        Assert.Null(t.ErrorMessage);
        Assert.Equal(RelativePeriod.LastMonth, t.Input.Period);
        Assert.Empty(t.Dropped);
    }

    [Fact]
    public void 期間值不是合法列舉_算被丟掉而不是靜默忽略()
    {
        var t = ToolParamsToSavedQuery.Translate(
            "SalesRanking", "x", """{"period":"NextCentury"}""");

        Assert.Null(t.Input.Period);
        Assert.Contains("period", t.Dropped);
    }

    [Theory]
    [InlineData("OrderOverdue", """{"customerCode":"C001"}""", "searchCustomer", "C001")]
    [InlineData("SlowMovingInventory", """{"idleDaysThreshold":120}""", "idleDays", "120")]
    [InlineData("StockShortage", """{"keyword":"ABC"}""", "searchProduct", "ABC")]
    [InlineData("InventoryValuation", """{"warehouseCode":"W1"}""", "searchWarehouse", "W1")]
    public void 工具參數翻成畫面查詢列欄位(string toolKey, string json, string filterKey, string expected)
    {
        var t = ToolParamsToSavedQuery.Translate(toolKey, "x", json);

        Assert.Empty(t.Dropped);
        Assert.Equal(expected, t.Input.Filters[filterKey]);
    }

    [Fact]
    public void 畫面沒有對應欄位的參數_丟掉但必須回報()
    {
        // topN 是工具參數，但沒有任何報表畫面的查詢列吃它。
        var t = ToolParamsToSavedQuery.Translate(
            "SalesRanking", "x", """{"period":"LastMonth","topN":5}""");

        Assert.Equal(RelativePeriod.LastMonth, t.Input.Period);
        Assert.Equal(["topN"], t.Dropped);
    }

    [Fact]
    public void 空值不算條件_也不算被丟掉的條件()
    {
        // 它本來就沒有限縮任何東西，報成「丟掉了」只會讓真正的遺漏被雜訊蓋掉。
        var t = ToolParamsToSavedQuery.Translate(
            "StockShortage", "x", """{"keyword":"","topN":null}""");

        Assert.Empty(t.Dropped);
        Assert.Empty(t.Input.Filters);
    }

    [Fact]
    public void 未登錄畫面繫結的工具_回錯誤而不是存出一張點不開的查詢()
    {
        var t = ToolParamsToSavedQuery.Translate("NoSuchTool", "x", "{}");

        Assert.Equal("SavedQuery_UnknownTool", t.ErrorMessage);
    }

    [Fact]
    public void 參數不是合法JSON_回錯誤()
    {
        var t = ToolParamsToSavedQuery.Translate("SalesRanking", "x", "{壞掉");

        Assert.Equal("SavedQuery_ParamsInvalid", t.ErrorMessage);
    }

    [Fact]
    public void 空參數_是一張只有預設條件的查詢_不是錯誤()
    {
        var t = ToolParamsToSavedQuery.Translate("StockShortage", "x", "");

        Assert.Null(t.ErrorMessage);
        Assert.Null(t.Input.Period);
        Assert.Empty(t.Input.Filters);
    }
}
