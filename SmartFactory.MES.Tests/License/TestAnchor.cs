namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 測試用的機器錨定碼。
///
/// <para>
/// 指紋計算需要錨定碼，而 CI runner 與乾淨的開發機上都沒有錨定檔。
/// 這裡以環境變數 <c>SMARTFACTORY_INSTANCE_ID</c> 提供固定值 —— 它是
/// <c>MachineAnchor</c> 取值順序的第一順位，會蓋過任何錨定檔，
/// 因此測試結果不受執行環境影響。
/// </para>
///
/// <para>
/// 子程序（<c>dotnet run</c>、<c>bash</c>）會繼承本程序的環境變數，
/// 所以跨實作一致性測試也會拿到同一個錨定碼。
/// </para>
/// </summary>
/// <summary>
/// 把所有會改動 <c>SMARTFACTORY_INSTANCE_ID</c> 的測試類別收進同一個 collection。
///
/// <para>
/// 環境變數是**行程全域**的，而 xUnit 預設讓不同測試類別平行執行。
/// <c>GetFingerprint_NoAnchorAnywhere_ThrowsWithActionableMessage</c> 會暫時把錨定碼
/// 清成 null，若此時 <c>FingerprintConsistencyTests</c> 正在另一條執行緒上算指紋，
/// 它就會拿到「沒有錨定碼」而爆掉 —— 症狀是**間歇性**假紅，重跑就綠。
/// </para>
/// </summary>
[CollectionDefinition(MachineAnchorEnvCollection.Name, DisableParallelization = true)]
public class MachineAnchorEnvCollection
{
    public const string Name = "MachineAnchorEnv";
}

internal static class TestAnchor
{
    internal const string Id = "SMARTFACTORY-TEST-ANCHOR-0001";

    /// <summary>
    /// 對應的指紋 —— 演算法的黃金向量。
    /// <c>SHA256(UTF8("v2:SMARTFACTORY-TEST-ANCHOR-0001"))</c>
    /// </summary>
    internal const string ExpectedFingerprint =
        "47E67630BECD69EDC071A572653B1695C56D1D0680D76F9F8E9254C2B6EDE030";

    /// <summary>
    /// 設定環境變數。可重複呼叫（值固定，因此平行執行的測試類別之間不會互相干擾）。
    /// </summary>
    internal static void Apply()
        => Environment.SetEnvironmentVariable("SMARTFACTORY_INSTANCE_ID", Id);
}
