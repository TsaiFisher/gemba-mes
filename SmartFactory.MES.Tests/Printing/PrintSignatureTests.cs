using SmartFactory.MES.Modules._Shared.Printing;
using Xunit;

namespace SmartFactory.MES.Tests.Printing;

/// <summary>
/// 列印簽核區分格規則（2026-10-28 使用者裁決）：一位核准者印「核准」；
/// 多位時最後一位印「核准」、其餘依序印「審核」；簽核中只印「審核」。
/// </summary>
public class PrintSignatureTests
{
    [Fact]
    public void NoApprovals_OnlyCreator()
    {
        var s = PrintSignatureService.Compose("amy", Array.Empty<string>(), finished: false, "、");
        Assert.Equal(new PrintSignature("amy", "", ""), s);
    }

    [Fact]
    public void SingleApprover_GoesToApproved_NotReviewed()
    {
        var s = PrintSignatureService.Compose("amy", new[] { "bob" }, finished: true, "、");
        Assert.Equal(new PrintSignature("amy", "", "bob"), s);
    }

    [Fact]
    public void ManyApprovers_LastIsApproved_RestReviewedInOrder()
    {
        var s = PrintSignatureService.Compose("amy", new[] { "bob", "cat", "dan" }, finished: true, "、");
        Assert.Equal(new PrintSignature("amy", "bob、cat", "dan"), s);
    }

    [Fact]
    public void StillPending_ApprovedSoFarAreReviewed_ApprovedLeftBlank()
    {
        var s = PrintSignatureService.Compose("amy", new[] { "bob" }, finished: false, "、");
        Assert.Equal(new PrintSignature("amy", "bob", ""), s);
    }
}
