using System.Security.Cryptography;
using System.Text;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 錨定檔的 Windows DPAPI 封裝 —— 儲存格式測試。
///
/// <para>
/// 這層封裝要守的**唯一不可破**的性質是：它只改「錨定碼怎麼存在磁碟上」，
/// 不改 anchorId、因此不改指紋。指紋一旦變動，所有既有授權與已加密的
/// 連線字串會同時失效（見 SECURITY-OPERATIONS.md §17.5）。
/// 因此每一支測試都順帶斷言指紋，而不只是斷言讀得回來。
/// </para>
///
/// <para>
/// 真正的加解密只在 Windows 上跑得到，CI（Linux）對那段是零證據力 ——
/// 那些測試以 <c>OperatingSystem.IsWindows()</c> 提前 return，與
/// <see cref="LicenseCrlAuthorityConsistencyTests"/> 的條件式作法一致，
/// 驗收改由實機執行（§17.6）。在 Linux 上仍可驗到的有三件事：
/// 舊格式相容、封裝檔在非 Windows 讀到時明確報錯、以及旗標不生效時不會誤寫格式。
/// </para>
/// </summary>
[Collection(MachineAnchorEnvCollection.Name)]
public class MachineAnchorDpapiTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sf-anchor-" + Guid.NewGuid().ToString("N"));

    private string AnchorPath => Path.Combine(_dir, "instance-id");

    public MachineAnchorDpapiTests()
    {
        Directory.CreateDirectory(_dir);
        // 錨定檔要被讀到，就必須先讓開第一順位的環境變數
        Environment.SetEnvironmentVariable("SMARTFACTORY_INSTANCE_ID", null);
        Environment.SetEnvironmentVariable("SMARTFACTORY_ANCHOR_PATH", AnchorPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SMARTFACTORY_ANCHOR_PATH", null);
        TestAnchor.Apply();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清不掉不影響判定 */ }
        GC.SuppressFinalize(this);
    }

    private static string ExpectedFingerprint(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v2:" + id)));

    // ── 相容性：升級前寫下的純文字錨定檔必須照常運作 ──────────────────────────

    [Fact]
    public void PlaintextAnchor_WrittenBeforeUpgrade_StillReadable()
    {
        File.WriteAllText(AnchorPath, TestAnchor.Id + Environment.NewLine);

        Assert.Equal(TestAnchor.ExpectedFingerprint, MachineAnchor.GetFingerprint());
    }

    [Fact]
    public void PlaintextAnchor_ProducesSameFingerprintAsEnvironmentVariable()
    {
        // 儲存格式不得影響指紋 —— 這是整個機制的地基，用黃金向量釘住
        File.WriteAllText(AnchorPath, TestAnchor.Id + Environment.NewLine);
        var fromFile = MachineAnchor.GetFingerprint();

        Environment.SetEnvironmentVariable("SMARTFACTORY_INSTANCE_ID", TestAnchor.Id);
        try
        {
            Assert.Equal(fromFile, MachineAnchor.GetFingerprint());
            Assert.Equal(TestAnchor.ExpectedFingerprint, fromFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMARTFACTORY_INSTANCE_ID", null);
        }
    }

    // ── 建立 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_RoundTrips_AndFingerprintMatchesTheAlgorithm()
    {
        var (id, path, isProtected) = MachineAnchor.Create(AnchorPath, force: false);

        Assert.Equal(AnchorPath, path);
        Assert.Equal(OperatingSystem.IsWindows(), isProtected);
        Assert.Equal(ExpectedFingerprint(id), MachineAnchor.GetFingerprint());
    }

    [Fact]
    public void Create_LeavesNoTemporaryFiles()
    {
        MachineAnchor.Create(AnchorPath, force: false);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Create_WithoutProtection_WritesPlaintext_OnEveryPlatform()
    {
        var (id, _, isProtected) = MachineAnchor.Create(AnchorPath, force: false, protect: false);

        Assert.False(isProtected);
        Assert.Equal(id, File.ReadAllText(AnchorPath).Trim());
        Assert.Equal(ExpectedFingerprint(id), MachineAnchor.GetFingerprint());
    }

    [Fact]
    public void Create_WithoutExplicitPath_WritesWhereTheAppWillRead()
    {
        // SMARTFACTORY_ANCHOR_PATH 設定後，應用程式**只**讀該路徑。
        // --init 若仍寫進預設位置，使用者會得到「產生成功卻還是啟動不了」。
        var (id, path, _) = MachineAnchor.Create(path: null, force: false);

        Assert.Equal(AnchorPath, path);
        Assert.True(File.Exists(AnchorPath));
        Assert.Equal(ExpectedFingerprint(id), MachineAnchor.GetFingerprint());
    }

    // ── 非 Windows：封裝不該悄悄發生，遇到封裝檔要明確報錯 ────────────────────

    [Fact]
    public void Create_OnNonWindows_IgnoresProtectFlagInsteadOfFailing()
    {
        if (OperatingSystem.IsWindows()) return;   // 驗收改由實機執行，見 §17.6

        var (id, _, isProtected) = MachineAnchor.Create(AnchorPath, force: false, protect: true);

        Assert.False(isProtected);
        Assert.Equal(id, File.ReadAllText(AnchorPath).Trim());
    }

    [Fact]
    public void ProtectedAnchor_ReadOnNonWindows_ThrowsActionableError()
    {
        if (OperatingSystem.IsWindows()) return;

        // 他機（Windows）封裝過的錨定檔被複製到 Linux 上的情形
        File.WriteAllText(AnchorPath,
            MachineAnchor.ProtectedPrefix + Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));

        var ex = Assert.Throws<InvalidOperationException>(() => MachineAnchor.GetFingerprint());

        // 關鍵：不可偽裝成「找不到錨定碼」——那則訊息會引導使用者 --init，等於換掉身分
        Assert.DoesNotContain("找不到機器錨定碼", ex.Message);
        Assert.Contains("DPAPI", ex.Message);
        Assert.Contains(AnchorPath, ex.Message);
    }

    [Fact]
    public void Reprotect_ToProtected_OnNonWindows_Throws()
    {
        if (OperatingSystem.IsWindows()) return;

        File.WriteAllText(AnchorPath, TestAnchor.Id + Environment.NewLine);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MachineAnchor.Reprotect(AnchorPath, protect: true));
        Assert.Contains("Windows", ex.Message);
    }

    [Fact]
    public void Reprotect_AlreadyPlaintext_ReportsNoChangeAndKeepsFingerprint()
    {
        File.WriteAllText(AnchorPath, TestAnchor.Id + Environment.NewLine);
        var before = MachineAnchor.GetFingerprint();

        var (id, path, already) = MachineAnchor.Reprotect(AnchorPath, protect: false);

        Assert.True(already);
        Assert.Equal(TestAnchor.Id, id);
        Assert.Equal(AnchorPath, path);
        Assert.Equal(before, MachineAnchor.GetFingerprint());
    }

    // ── Windows 專屬：實際的封裝／解封 ────────────────────────────────────────

    [Fact]
    public void Create_OnWindows_WritesProtectedBlobAndKeepsFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux CI 對這段零證據力，見 §17.6

        var (id, _, isProtected) = MachineAnchor.Create(AnchorPath, force: false, protect: true);
        var stored = File.ReadAllText(AnchorPath).Trim();

        Assert.True(isProtected);
        Assert.StartsWith(MachineAnchor.ProtectedPrefix, stored);
        Assert.DoesNotContain(id, stored);                       // 錨定碼不得以明文留在檔案裡
        Assert.Equal(ExpectedFingerprint(id), MachineAnchor.GetFingerprint());
    }

    [Fact]
    public void Reprotect_OnWindows_ConvertsBothWaysWithoutChangingFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return;

        MachineAnchor.Create(AnchorPath, force: false, protect: false);
        var fingerprint = MachineAnchor.GetFingerprint();
        var id = File.ReadAllText(AnchorPath).Trim();

        var (protectedId, _, already) = MachineAnchor.Reprotect(AnchorPath, protect: true);
        Assert.False(already);
        Assert.Equal(id, protectedId);
        Assert.StartsWith(MachineAnchor.ProtectedPrefix, File.ReadAllText(AnchorPath).Trim());
        Assert.Equal(fingerprint, MachineAnchor.GetFingerprint());

        var (plainId, _, _) = MachineAnchor.Reprotect(AnchorPath, protect: false);
        Assert.Equal(id, plainId);
        Assert.Equal(id, File.ReadAllText(AnchorPath).Trim());
        Assert.Equal(fingerprint, MachineAnchor.GetFingerprint());
    }

    [Fact]
    public void CorruptProtectedBlob_OnWindows_ThrowsActionableError()
    {
        if (!OperatingSystem.IsWindows()) return;

        File.WriteAllText(AnchorPath, MachineAnchor.ProtectedPrefix + "not-valid-base64!!");

        var ex = Assert.Throws<InvalidOperationException>(() => MachineAnchor.GetFingerprint());
        Assert.DoesNotContain("找不到機器錨定碼", ex.Message);
        Assert.Contains(AnchorPath, ex.Message);
    }

    [Fact]
    public void ForeignProtectedBlob_OnWindows_ExplainsItCameFromAnotherMachine()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 格式正確的 Base64，但不是本機 DPAPI 產生的密文 —— 等同他機複製過來
        File.WriteAllText(AnchorPath,
            MachineAnchor.ProtectedPrefix + Convert.ToBase64String(new byte[64]));

        var ex = Assert.Throws<InvalidOperationException>(() => MachineAnchor.GetFingerprint());
        Assert.Contains("其他電腦", ex.Message);
        Assert.DoesNotContain("找不到機器錨定碼", ex.Message);
    }
}
