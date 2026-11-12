/// SmartFactory.MES — EncryptConnectionString 工具
/// 將明文連線字串加密為 DefaultEncrypted 的值（演算法見 Services/ConnectionStringCipher.cs，本工具以 Link 共用）。
///
/// 格式：
///   ENC:v3（預設）：金鑰＝指紋＋授權編號＋主機板 UUID 原值。授權有列主機識別碼（hostIds）時系統只接受 v3
///                   ——複製出來的環境推不出金鑰（SECURITY-OPERATIONS.md §18.15.9）。
///   ENC:v2        ：金鑰＝指紋＋授權編號。只限授權沒有列 hostIds（例如 CI 授權）。
///
/// 用法（必須三選一指定主機：--this-host／--host-uuid／--no-host-binding）：
///   dotnet run -- --license-id <ID> --plaintext "Host=..." --this-host          （--fingerprint 省略＝本機指紋）
///   dotnet run -- --fingerprint <FP> --license-id <ID> --plaintext "Host=..." --this-host
///   dotnet run -- --fingerprint <FP> --license-id <ID> --plaintext "Host=..." --host-uuid <UUID> --verify

using SmartFactory.MES.Services;

string? fingerprint = null, licenseId = null, plaintext = null, hostUuidArg = null;
bool verify = args.Contains("--verify"), thisHost = args.Contains("--this-host"), noHost = args.Contains("--no-host-binding");

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--fingerprint":  fingerprint = args[++i]; break;
        case "--license-id":   licenseId   = args[++i]; break;
        case "--plaintext":    plaintext   = args[++i]; break;
        case "--host-uuid":    hostUuidArg = args[++i]; break;
    }
}

var missing = new List<string>();
if (string.IsNullOrEmpty(licenseId))   missing.Add("--license-id");
if (string.IsNullOrEmpty(plaintext))   missing.Add("--plaintext");
if (new[] { thisHost, hostUuidArg is not null, noHost }.Count(x => x) != 1)
    missing.Add("--this-host／--host-uuid／--no-host-binding 三選一");

if (missing.Count > 0)
{
    Console.Error.WriteLine($"缺少必要參數：{string.Join(", ", missing)}");
    PrintHelp();
    Environment.Exit(1);
}

// --fingerprint 省略＝在目標機器上執行，取本機指紋；先驗完其他參數，不加參數執行時才會看到用法（錨定檔讀不到時明確失敗，不猜）
if (string.IsNullOrEmpty(fingerprint))
{
    try { fingerprint = MachineAnchor.GetFingerprint(); }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"取不到本機指紋：{ex.Message}（或以 --fingerprint 指定）");
        Environment.Exit(1);
    }
}

string? hostUuid = null;
if (hostUuidArg is not null)
{
    hostUuid = HostIdentity.NormalizeUuid(hostUuidArg);
    if (hostUuid is null)
    {
        Console.Error.WriteLine($"--host-uuid 格式無效或為佔位值：{hostUuidArg}");
        Environment.Exit(1);
    }
}
else if (thisHost)
{
    var (host, uuid) = HostIdentity.DetectWithUuid();
    if (uuid is null)
    {
        Console.Error.WriteLine($"讀不到本機主機識別碼：{host.Problem}");
        Environment.Exit(1);
    }
    hostUuid = uuid;
}

var encrypted = ConnectionStringCipher.Encrypt(plaintext!, fingerprint!, licenseId!, hostUuid);

// 與 INSTALL §5 一致：填進執行環境對應的設定檔，不是共用的 appsettings.json（2026-11-12 rc3 裝機回報）
Console.WriteLine("加密後的連線字串（填入 ConnectionStrings:DefaultEncrypted；開發環境 appsettings.Development.json、正式環境 appsettings.Production.json）：");
Console.WriteLine(encrypted);
if (hostUuid is not null)
    Console.WriteLine($"主機識別碼：{HostIdentity.HashUuid(hostUuid)}（須列在授權的 hostIds 內）");

if (verify)
{
    var decrypted = ConnectionStringCipher.Decrypt(encrypted, fingerprint!, licenseId!, hostUuid, hostBound: hostUuid is not null);
    // 不印解密結果：那是含密碼的明文，會留在終端機畫面與捲動紀錄（2026-11-09 裝機測試指出）。
    Console.WriteLine();
    Console.WriteLine(decrypted == plaintext ? "✅ 驗證通過：本機可解密且與輸入一致" : "❌ 驗證失敗：解密結果與輸入不一致");
}

static void PrintHelp()
{
    Console.WriteLine("""
        EncryptConnectionString — 連線字串加密工具（AES-256-GCM）

        用法：dotnet run -- [選項]

          --fingerprint  <fp>         目標機器的環境指紋（省略＝本機；替其他機器加密時由 get-fingerprint.sh 取得）
          --license-id   <id>         License ID（與核發 License 時一致）
          --plaintext    <connstr>    明文連線字串
          主機（三選一）：
          --this-host                 讀本機主機板 UUID（在目標機器上執行時用這個）→ ENC:v3
          --host-uuid    <uuid>       目標機器的主機板 UUID 原值 → ENC:v3
          --no-host-binding           不綁主機 → ENC:v2（只限授權沒有列 hostIds，例如 CI 授權）
          --verify                    加密後立即驗證解密是否正確（選填）

        輸出格式：ENC:v3:{salt}:{nonce}:{ciphertext}:{tag}（v2 同格式、前綴不同）
          - salt  (16 bytes)：每次加密隨機產生，使同一密碼衍生不同金鑰
          - nonce (12 bytes)：GCM 隨機 Nonce
          - tag   (16 bytes)：GCM 驗證標籤，密文遭竄改時解密立即失敗

        範例（在目標機器上執行）：
          FINGERPRINT=$(bash tools/get-fingerprint.sh | grep -oP '[0-9A-F]{64}')
          dotnet run --project tools/EncryptConnectionString -- \
            --fingerprint "$FINGERPRINT" \
            --license-id LIC-ACME-2025-001 \
            --plaintext "Host=db.acme.local;Database=MES;Username=app;Password=Sec!" \
            --this-host --verify
        """);
}
