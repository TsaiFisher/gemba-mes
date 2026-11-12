using Moq;
using SmartFactory.MES.Modules.BAS.Services;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 報工會先依單位捨入數量（DispatchController.RoundReportQtyAsync）。寬鬆的 <c>Mock&lt;IUomService&gt;</c>
/// 對 RoundQtyAsync 回傳 0，所有報工都會變成「數量皆為 0」被擋下；不測捨入的測試一律用這個原值不變的版本。
/// </summary>
internal static class PassThroughUom
{
    public static IUomService Create()
    {
        var mock = new Mock<IUomService>();
        mock.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns<string?, decimal, CancellationToken>((_, qty, _) => Task.FromResult(qty));
        return mock.Object;
    }
}
