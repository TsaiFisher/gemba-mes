using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 超量生產／超量收貨的判定（<see cref="OverQuantityPolicy"/>，mfg-domain.md §4.6）。
/// 廠別參數：容許率（%）、超過容許上限時 Block／Supervisor、委外超收部分付不付加工費。
/// 「主管」＝角色有超量核准作業權限（手機模組權限的 OverReportApprove／OverReceiveApprove）。
/// </summary>
public class OverQuantityPolicyTests
{
    private const string Plant = "P1";
    private readonly Dictionary<string, string?> _params = new();
    private readonly HashSet<(string Module, string Action)> _granted = new();

    private OverQuantityPolicy Policy()
    {
        var pp = new Mock<IPlantParamService>();
        pp.Setup(p => p.GetAsync(It.IsAny<string>(), Plant, It.IsAny<CancellationToken>()))
          .ReturnsAsync((string k, string? _, CancellationToken _) => _params.GetValueOrDefault(k));
        var perm = new Mock<IMobilePermissionService>();
        perm.Setup(p => p.CanExecuteAsync("R1", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string m, string a) => _granted.Contains((m, a)));
        return new OverQuantityPolicy(pp.Object, perm.Object);
    }

    [Fact]
    public async Task 沒超過計畫量_一律照收()
        => Assert.True((await Policy().CheckProductionAsync(Plant, "R1", 100, 60, 40)).Allowed);

    [Fact]
    public async Task 未設定參數_廠內一個都不能多_與原本硬擋相同()
    {
        var r = await Policy().CheckProductionAsync(Plant, "R1", 100, 100, 1);
        Assert.False(r.Allowed);
        Assert.NotNull(r.Message);
    }

    [Theory]
    [InlineData(103, true)]    // 容許率 3%：上限 103
    [InlineData(104, false)]
    public async Task 容許率內照收_超過上限依處理方式(decimal total, bool allowed)
    {
        _params[ProjectZSystemParamKeys.MfgOverReportTolerancePct] = "3";
        var r = await Policy().CheckProductionAsync(Plant, "R1", 100, 0, total);
        Assert.Equal(allowed, r.Allowed);
    }

    /// <summary>委外超收容許率的邊界：與廠內同一套算法；設成拒收時超過一點點也擋。</summary>
    [Theory]
    [InlineData("10", 50, 55,        true)]
    [InlineData("10", 50, 55.000001, false)]
    [InlineData("0",  50, 50,        true)]
    [InlineData("0",  50, 50.000001, false)]
    public async Task 委外超收容許上限的邊界值(string pct, decimal ordered, decimal total, bool allowed)
    {
        _params[ProjectZSystemParamKeys.MfgOutsourceOverReceiptTolerancePct] = pct;
        _params[ProjectZSystemParamKeys.MfgOutsourceOverReceiptAction] = "Block";
        var r = await Policy().CheckOutsourceReceiptAsync(Plant, "R1", ordered, 0, total);
        Assert.Equal(allowed, r.Allowed);
    }

    /// <summary>
    /// 邊界值（第 2 節補測）：上限有小數（50×3%＝51.5）時剛好等於上限照收、多一個最小單位就超過；
    /// 整數上限＋0.000001（數量精度 6 位）也必須算超過；極大容許率照算不溢位。
    /// </summary>
    [Theory]
    [InlineData("3",    50,  51.5,       true)]
    [InlineData("3",    50,  51.500001,  false)]
    [InlineData("3",    100, 103.000001, false)]
    [InlineData("1000", 100, 1100,       true)]
    [InlineData("1000", 100, 1100.000001, false)]
    [InlineData(" 3 ",  100, 103,        true)]    // 參數值前後空白
    public async Task 容許上限的邊界值(string pct, decimal planned, decimal total, bool allowed)
    {
        _params[ProjectZSystemParamKeys.MfgOverReportTolerancePct] = pct;
        var r = await Policy().CheckProductionAsync(Plant, "R1", planned, 0, total);
        Assert.Equal(allowed, r.Allowed);
    }

    [Fact]
    public async Task 設成需主管確認_有超量核准權限的角色可收_沒有的擋下並說明()
    {
        _params[ProjectZSystemParamKeys.MfgOverReportAction] = "Supervisor";

        var denied = await Policy().CheckProductionAsync(Plant, "R1", 100, 100, 5);
        Assert.False(denied.Allowed);

        _granted.Add((OverQuantityPolicy.MfgModuleKey, OverQuantityPolicy.ReportApproveKey));
        var ok = await Policy().CheckProductionAsync(Plant, "R1", 100, 100, 5);
        Assert.True(ok.Allowed);
        Assert.True(ok.BySupervisor);
    }

    [Fact]
    public async Task 報工的主管權限不能拿來收委外的貨_兩個模組各自授權()
    {
        _granted.Add((OverQuantityPolicy.MfgModuleKey, OverQuantityPolicy.ReportApproveKey));
        // 委外預設＝需主管確認，但這個角色只有報工的超量核准
        Assert.False((await Policy().CheckOutsourceReceiptAsync(Plant, "R1", 50, 50, 1)).Allowed);
    }

    [Fact]
    public async Task 委外未設定參數_預設需主管確認_最接近原本只確認的行為()
    {
        _granted.Add((OverQuantityPolicy.InvModuleKey, OverQuantityPolicy.ReceiveApproveKey));
        Assert.True((await Policy().CheckOutsourceReceiptAsync(Plant, "R1", 50, 50, 1)).Allowed);
    }

    [Fact]
    public async Task 委外設成拒收_連主管也不行()
    {
        _params[ProjectZSystemParamKeys.MfgOutsourceOverReceiptAction] = "Block";
        _granted.Add((OverQuantityPolicy.InvModuleKey, OverQuantityPolicy.ReceiveApproveKey));
        Assert.False((await Policy().CheckOutsourceReceiptAsync(Plant, "R1", 50, 50, 1)).Allowed);
    }

    [Fact]
    public async Task 容許率不合法_負數或文字_視為0()
    {
        _params[ProjectZSystemParamKeys.MfgOverReportTolerancePct] = "-5";
        Assert.False((await Policy().CheckProductionAsync(Plant, "R1", 100, 100, 1)).Allowed);
        _params[ProjectZSystemParamKeys.MfgOverReportTolerancePct] = "abc";
        Assert.False((await Policy().CheckProductionAsync(Plant, "R1", 100, 100, 1)).Allowed);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Pay", true)]
    [InlineData("NoPay", false)]
    public async Task 委外超收部分的加工費_未設定照付(string? value, bool pay)
    {
        _params[ProjectZSystemParamKeys.MfgOutsourceOverReceiptFee] = value;
        Assert.Equal(pay, await Policy().PayOverReceiptFeeAsync(Plant));
    }
}
