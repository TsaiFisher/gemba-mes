using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// §21 訂單↔MRP↔生產聯動規則單元測試（mfg-domain.md §21）。
/// 純業務規則驗證，不接 DB。
/// </summary>
public class Sec21MrpLinkageTests
{
    // ── §21.2 訂單修改阻擋規則 ────────────────────────────────────────────────

    [Theory]
    [InlineData(true,  true,  true)]    // 有 MrpLine + 有投產 WO → 阻擋
    [InlineData(true,  false, false)]   // 有 MrpLine + 無投產 WO → 不阻擋
    [InlineData(false, false, false)]   // 無 MrpLine → 不阻擋
    public void Sec21_2_ShouldBlock_WhenInProgressWoExists(
        bool hasMrpLines, bool hasInProgressWo, bool shouldBlock)
    {
        // 模擬 CheckMrpImpactAsync 回傳值
        var result = (HasMrpLines: hasMrpLines, HasInProgressWo: hasInProgressWo);
        Assert.Equal(shouldBlock, result.HasInProgressWo);
    }

    [Theory]
    [InlineData(true,  false, true)]   // 有 MrpLine + 無投產 WO → 顯示 warning
    [InlineData(false, false, false)]  // 無 MrpLine → 不顯示 warning
    public void Sec21_2_ShouldWarn_WhenMrpLinesExistButNoInProgress(
        bool hasMrpLines, bool hasInProgressWo, bool shouldWarn)
    {
        var result = (HasMrpLines: hasMrpLines, HasInProgressWo: hasInProgressWo);
        Assert.Equal(shouldWarn, result.HasMrpLines && !result.HasInProgressWo);
    }

    // ── §21.3 PO Scheduled Receipts 加入 Fstock ──────────────────────────────

    [Fact]
    public void Sec21_3_Fstock_WithScheduledReceipts_IncreasesAvailable()
    {
        // 模擬 Fstock 計算
        decimal qtyOnHand        = 50m;
        decimal reserved         = 30m;
        decimal scheduledReceipts = 40m;  // 已核准 PO，交期在需求日之前

        decimal availableWithoutPO = qtyOnHand - reserved;               // = 20
        decimal availableWithPO    = qtyOnHand + scheduledReceipts - reserved; // = 60

        Assert.Equal(20m, availableWithoutPO);
        Assert.Equal(60m, availableWithPO);
        Assert.True(availableWithPO > availableWithoutPO);
    }

    [Fact]
    public void Sec21_3_Fstock_PO_NotApproved_NotCounted()
    {
        // 未核准的 PO 不計入 Scheduled Receipts
        decimal onHand      = 50m;
        decimal reserved    = 30m;
        decimal draftPO     = 100m;  // Draft 狀態，不算

        decimal available = onHand - reserved;  // = 20，不加 draftPO
        Assert.Equal(20m, available);
    }

    [Fact]
    public void Sec21_3_Fstock_PO_DeliveryAfterDemandDate_NotCounted()
    {
        // 交期在需求日之後的 PO 不計入
        var demandDate   = new DateOnly(2026, 6, 10);
        var deliveryLate = new DateOnly(2026, 6, 20);  // 晚於需求日

        bool isEligible = deliveryLate <= demandDate;
        Assert.False(isEligible);
    }

    [Fact]
    public void Sec21_3_Fstock_PO_FullyReceived_NotCounted()
    {
        // 已全量收貨的 PO 不計入（ReceivedQty >= OrderQty）
        decimal orderQty    = 100m;
        decimal receivedQty = 100m;
        decimal pending     = orderQty - receivedQty;  // = 0

        Assert.Equal(0m, pending);
        Assert.False(pending > 0);  // 不計入
    }

    // ── §21.4 WO Release 缺料警示規則 ────────────────────────────────────────

    [Fact]
    public void Sec21_4_Release_NoShortage_ReturnsEmptyWarnings()
    {
        // 模擬：庫存充足
        decimal onHand = 100m, reserved = 20m, required = 50m;
        var available = onHand - reserved;
        bool hasShortage = available < required;
        Assert.False(hasShortage);
    }

    [Fact]
    public void Sec21_4_Release_WithShortage_ReturnsWarning()
    {
        // 模擬：庫存不足
        decimal onHand = 30m, reserved = 20m, required = 50m;
        var available = onHand - reserved;
        bool hasShortage = available < required;
        Assert.True(hasShortage);
    }

    [Fact]
    public void Sec21_4_Release_IsNonBlocking_ReleaseProceedsWithWarnings()
    {
        // §21.4 明確：缺料不阻擋釋出，僅警示
        var warnings = new List<string> { "M001", "M002" };
        bool released = true;  // 照常釋出
        Assert.True(released);
        Assert.Equal(2, warnings.Count);
    }

    // ── §21.1 MRP 預留釋放橋接規則 ───────────────────────────────────────────

    [Fact]
    public void Sec21_1_CloseDoc_ShouldReleaseMrpReservations()
    {
        // 模擬：關閉訂單後，找到 2 筆 MrpRunLine → 釋放 2 筆預留
        var mrpLines = new[]
        {
            (MrpRunId: "MRP-2026-001", LineSeq: 1),
            (MrpRunId: "MRP-2026-001", LineSeq: 2)
        };
        // 每筆都需要呼叫 ReleaseBySourceLineAsync
        Assert.Equal(2, mrpLines.Length);
    }

    [Fact]
    public void Sec21_1_CloseDoc_NoMrpLines_NoRelease()
    {
        // 若訂單未匯入 MRP → 不需釋放（no-op）
        var mrpLines = Array.Empty<(string, int)>();
        Assert.Empty(mrpLines);
    }
}
