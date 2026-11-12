using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.Tests.Validation;

/// <summary>
/// 生效區間倒置守門（Phase 4-2 邏輯衝突類）。
///
/// 三張帶「生效起訖」的主檔都被同一種區間查詢消費：
///   <c>From &lt;= today &amp;&amp; (To == null || To &gt;= today)</c>
/// 起訖倒置時存檔完全正常、列表也看得到，只有查價／查途程永遠落空——
/// 症狀是「主檔明明建好了卻不生效」，畫面上沒有任何地方看得出問題。
///
/// 訖日留空＝無期限，是合法的；起訖同日＝當天有效，也是合法的。
/// 只有「訖日早於起日」這一格才是無效輸入。
/// </summary>
public class EffectiveDateRangeTests
{
    private static bool IsValid(object model, out string message)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results,
                                    validateAllProperties: true);
        message = string.Join(" / ", results.Select(r => r.ErrorMessage));
        return results.Count == 0;
    }

    // ── 等價劃分：訖日 早於／等於／晚於 起日、以及訖日留空 ────────────────

    public static TheoryData<int, bool> RangeCases() => new()
    {
        { -1, false },  // 訖日早於起日：唯一的無效格
        {  0, true  },  // 同日：當天有效
        {  1, true  },  // 正常區間
        { 365, true },
    };

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void VendorPriceList_ValidToBeforeValidFrom_IsRejected(int dayOffset, bool expectValid)
    {
        var from = new DateOnly(2026, 6, 1);
        var model = new VendorPriceList
        {
            SupplierCode = "S1",
            ProductCode = "P1",
            CurrencyCode = "TWD",
            ValidFrom = from,
            ValidTo = from.AddDays(dayOffset)
        };

        Assert.Equal(expectValid, IsValid(model, out var msg));
        if (!expectValid) PzAssert.Message("Pur_PriceListValidToBeforeFrom", msg);
    }

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void OutsourceQuotation_EffectiveToBeforeEffectiveFrom_IsRejected(int dayOffset, bool expectValid)
    {
        var from = new DateOnly(2026, 6, 1);
        var model = new OutsourceQuotation
        {
            SupplierCode = "S1",
            ProductCode = "P1",
            CurrencyCode = "TWD",
            UomCode = "PCS",
            EffectiveFrom = from,
            EffectiveTo = from.AddDays(dayOffset)
        };

        Assert.Equal(expectValid, IsValid(model, out _));
    }

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void Routing_EffectiveToBeforeEffectiveFrom_IsRejected(int dayOffset, bool expectValid)
    {
        var from = new DateOnly(2026, 6, 1);
        var model = new Routing
        {
            RoutingId = "R1",
            EffectiveFrom = from,
            EffectiveTo = from.AddDays(dayOffset)
        };

        Assert.Equal(expectValid, IsValid(model, out _));
    }

    // ── 訖日留空＝無期限，任何起日都合法 ──────────────────────────────────

    [Fact]
    public void NullEndDate_MeansOpenEnded_AndIsAlwaysValid()
    {
        Assert.True(IsValid(new VendorPriceList
        {
            SupplierCode = "S1", ProductCode = "P1", CurrencyCode = "TWD",
            ValidFrom = new DateOnly(2026, 6, 1), ValidTo = null
        }, out _));

        Assert.True(IsValid(new OutsourceQuotation
        {
            SupplierCode = "S1", ProductCode = "P1", CurrencyCode = "TWD", UomCode = "PCS",
            EffectiveFrom = new DateOnly(2026, 6, 1), EffectiveTo = null
        }, out _));

        // 途程的起日本身也可留空（通用途程），兩邊皆空同樣合法
        Assert.True(IsValid(new Routing
        {
            RoutingId = "R1",
            EffectiveFrom = null, EffectiveTo = null
        }, out _));
    }

    /// <summary>
    /// 途程的起日可留空而訖日有值——此時沒有「起日」可比，不該被擋。
    /// 另外兩張的起日是必填，不會有這一格。
    /// </summary>
    [Fact]
    public void Routing_NullStartWithEndDate_IsValid()
    {
        Assert.True(IsValid(new Routing
        {
            RoutingId = "R1",
            EffectiveFrom = null, EffectiveTo = new DateOnly(2026, 6, 1)
        }, out _));
    }
}
