using Moq;
using System.Security;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 以環境變數 <c>SMARTFACTORY_INSTANCE_ID</c> 指定的錨定碼只接受內部授權（SECURITY-OPERATIONS.md §18.11）。
/// </summary>
[Collection(MachineAnchorEnvCollection.Name)]
public class LicenseAnchorSourceTests
{
    [Theory]
    [InlineData("ACME")]
    [InlineData("COMMUNITY")]
    public void AnchorFile_AnyClientCode_IsAllowed(string clientCode)
        => LicenseValidator.EnsureAnchorSourceAllowed(anchorFromEnvironmentVariable: false, clientCode);

    [Theory]
    [InlineData("SMARTFACTORY-INTERNAL")]
    [InlineData("smartfactory-internal")]
    public void EnvironmentVariable_InternalLicense_IsAllowed(string clientCode)
        => LicenseValidator.EnsureAnchorSourceAllowed(anchorFromEnvironmentVariable: true, clientCode);

    [Theory]
    [InlineData("ACME")]
    [InlineData("COMMUNITY")]
    [InlineData("")]
    public void EnvironmentVariable_NonInternalLicense_IsRejected(string clientCode)
    {
        var ex = Assert.Throws<SecurityException>(
            () => LicenseValidator.EnsureAnchorSourceAllowed(anchorFromEnvironmentVariable: true, clientCode));
        Assert.Contains(MachineAnchor.InstanceIdEnvVar, ex.Message);
    }

    [Fact]
    public void FingerprintService_ReportsEnvironmentVariableSource()
    {
        var sut = new EnvironmentFingerprintService();
        try
        {
            TestAnchor.Apply();
            Assert.True(sut.IsAnchorFromEnvironmentVariable);

            Environment.SetEnvironmentVariable(MachineAnchor.InstanceIdEnvVar, "   ");
            Assert.False(sut.IsAnchorFromEnvironmentVariable);   // 與 ResolveAnchor 一致：空白視同未設定
        }
        finally
        {
            TestAnchor.Apply();
        }
    }

    [Fact]
    public void TestDoubles_DefaultToAnchorFile()
    {
        // 介面預設實作為 false：既有的 Mock 與自訂實作不會因此被拒
        var mock = new Mock<IEnvironmentFingerprintService>();
        Assert.False(mock.Object.IsAnchorFromEnvironmentVariable);
    }
}
