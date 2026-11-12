using SmartFactory.MES.Services;

namespace SmartFactory.MES.MachineId;

/// <summary>
/// 機器錨定碼工具 — 交付客戶用於取得環境指紋。
///
/// <para>
/// <b>此工具不含任何私鑰或簽發功能</b>，可安全交付給客戶／顧問。
/// 授權簽發請使用 SmartFactory.MES.LicenseAuthority（內部保管）。
/// </para>
///
/// <para>
/// 指紋演算法與應用程式共用 <see cref="MachineAnchor"/> 同一份原始檔，
/// 因此此處輸出的指紋與 app 執行期算出的必然相同。
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return 0;
            }

            var path = GetOption(args, "--path");

            if (HasFlag(args, "--init"))
                return RunInit(path, force: HasFlag(args, "--force"),
                               protect: !HasFlag(args, "--no-protect"));

            if (HasFlag(args, "--protect"))
                return RunReprotect(path, protect: true);

            if (HasFlag(args, "--unprotect"))
                return RunReprotect(path, protect: false);

            return RunShow(showRaw: HasFlag(args, "--raw"), serverUrl: GetOption(args, "--server"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("❌ " + ex.Message);
            return 1;
        }
    }

    // ── 顯示目前指紋 ─────────────────────────────────────────────────────────
    private static int RunShow(bool showRaw, string? serverUrl)
    {
        var (id, source) = MachineAnchor.ResolveAnchor();
        var fingerprint = MachineAnchor.GetFingerprint();

        Console.WriteLine("環境指紋：");
        Console.WriteLine(fingerprint);
        Console.WriteLine();
        Console.WriteLine($"錨定碼來源：{source}");
        Console.WriteLine($"演算法：{MachineAnchor.AlgoVersion}");
        Console.WriteLine($"儲存格式：{DescribeStorage(source)}");

        // 錨定碼原文預設不印。指紋是 SHA256(raw)，它存在的意義就是讓客戶能回報身分
        // 而不必交出金鑰材料——把 raw 印在同一個畫面上，等於要客戶截圖回傳時一併外流，
        // 那層雜湊就白做了。get-fingerprint.sh 一向把它藏在 --raw 後面，這裡對齊。
        if (showRaw)
            Console.WriteLine($"（raw：{MachineAnchor.AlgoVersion}:{id}）");
        // 主機識別碼與申請碼（§18.15.4）：全新安裝還沒有授權時系統起不來、進不了「授權狀態」頁，
        // 申請碼只能從這裡取得。兩者都是雜湊，與指紋一樣可以安全外傳。
        var host = HostIdentity.Detect();
        Console.WriteLine();
        Console.WriteLine($"部署類型：{DescribeDeploy(host.Kind)}");
        Console.WriteLine(host.HostId is not null ? $"主機識別碼：{host.HostId}"
            : host.Kind == DeploymentKind.Container ? $"主機識別碼：不適用（{host.Problem}）"
            : $"主機識別碼：讀不到（{host.Problem}）");
        if (host.HostId is null && host.Kind is DeploymentKind.Physical or DeploymentKind.Wsl or DeploymentKind.Unknown)
        {
            Console.WriteLine("  ⚠️ 讀不到主機識別碼時，只能核發每 90 天確認的授權。實體 Linux 請先完成 systemd 的 ExecStartPre 設定");
            Console.WriteLine("     （使用手冊「授權與防複製」第 4 節）並以服務帳號重新執行本工具，或直接以 root 執行。");
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "";
        var code = LicenseRequestCode.Build(fingerprint, hasTpm: false, $"MachineId {version}", host);
        Console.WriteLine();
        Console.WriteLine("申請碼（全部複製，交給授權提供者或貼到登記網址）：");
        Console.WriteLine(code);
        Console.WriteLine();
        var server = serverUrl ?? LicenseRequestCode.DefaultServerUrl;
        Console.WriteLine("登記網址（第一次申請；可在能上網的電腦開啟）：");
        Console.WriteLine(LicenseRequestCode.RegisterUrl(server, code));
        Console.WriteLine();
        Console.WriteLine("換發網址（已有授權、換電腦或主機板時）：");
        Console.WriteLine(LicenseRequestCode.RenewUrl(server, code));
        Console.WriteLine();
        Console.WriteLine(SmartFactory.MES.Services.LicenseSupport.HowToObtain);
        return 0;
    }

    // ── 產生錨定碼 ───────────────────────────────────────────────────────────
    private static int RunInit(string? path, bool force, bool protect)
    {
        // 必須在 Create 之前判斷，否則新檔已寫入、看不出是否為覆寫
        TryDescribeExisting(out var existingPath);
        var overwritten = force && existingPath.Length > 0;

        var (_, writtenPath, isProtected) = MachineAnchor.Create(path, force, protect);
        var fingerprint = MachineAnchor.GetFingerprint();

        if (overwritten)
        {
            Console.WriteLine($"⚠️  已覆寫既有錨定檔：{existingPath}");
            Console.WriteLine("   原有授權與已加密的連線字串都將失效，兩者都需重做。");
            Console.WriteLine();
        }

        Console.WriteLine("✅ 已產生機器錨定碼");
        Console.WriteLine($"   位置：{writtenPath}");
        Console.WriteLine($"   儲存格式：{(isProtected ? "Windows DPAPI 封裝（LocalMachine）" : "純文字")}");
        if (isProtected)
        {
            Console.WriteLine("   ⚠️ 此檔只能在本機解開；複製到其他電腦將無法使用（這正是封裝的目的）。");
            Console.WriteLine("      作業系統重灌後亦無法還原，屆時需重新產生並換發授權。");
        }
        Console.WriteLine();
        Console.WriteLine("環境指紋：");
        Console.WriteLine(fingerprint);
        Console.WriteLine();
        Console.WriteLine("申請授權：不加參數再執行一次本工具，會印出申請碼與登記網址。");

        // 明確指定了錨定檔路徑（例如容器以 SMARTFACTORY_ANCHOR_PATH 指向 volume）就是刻意的位置，不提示搬到 /etc
        var explicitPath = path is not null
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MachineAnchor.AnchorPathEnvVar));
        if (!OperatingSystem.IsWindows() && !writtenPath.StartsWith("/etc/") && !explicitPath)
        {
            Console.WriteLine();
            Console.WriteLine("ℹ️  目前寫入的是使用者層位置（無 root 權限時的開發用路徑）。");
            Console.WriteLine("   正式部署請改用系統位置並收緊權限：");
            Console.WriteLine($"     sudo install -d -m 0755 -o root -g root {Path.GetDirectoryName(MachineAnchor.SystemAnchorPath)}");
            Console.WriteLine($"     sudo cp {writtenPath} {MachineAnchor.SystemAnchorPath}");
            Console.WriteLine($"     sudo chown root:<服務帳號> {MachineAnchor.SystemAnchorPath}");
            Console.WriteLine($"     sudo chmod 0640 {MachineAnchor.SystemAnchorPath}");
        }

        return 0;
    }

    // ── 就地切換儲存格式（不改變錨定碼，指紋不變）─────────────────────────────
    private static int RunReprotect(string? path, bool protect)
    {
        if (protect && !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("❌ DPAPI 封裝僅適用於 Windows。");
            Console.Error.WriteLine("   此平台請以檔案權限保護錨定檔（0640，owner root、group 為服務帳號）。");
            return 1;
        }

        var before = MachineAnchor.GetFingerprint();
        var (_, actualPath, already) = MachineAnchor.Reprotect(path, protect);
        var after = MachineAnchor.GetFingerprint();

        // 這個機制唯一不可接受的失敗就是「指紋被改掉」——那會讓既有授權與
        // 已加密的連線字串一起失效。這裡實際比對，不只是相信設計。
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("❌ 指紋在轉換後改變了，這是不該發生的。請勿繼續使用此錨定檔。");
            return 1;
        }

        var target = protect ? "Windows DPAPI 封裝（LocalMachine）" : "純文字";
        Console.WriteLine(already
            ? $"ℹ️  錨定檔已經是「{target}」，未做變更：{actualPath}"
            : $"✅ 錨定檔已轉為「{target}」：{actualPath}");
        Console.WriteLine();
        Console.WriteLine("環境指紋（未變更，既有授權繼續適用）：");
        Console.WriteLine(after);

        if (protect && !already)
        {
            Console.WriteLine();
            Console.WriteLine("⚠️ 此檔自此只能在本機解開。請確認已有這台機器的災難復原程序：");
            Console.WriteLine("   作業系統重灌後 DPAPI 主金鑰即不同，錨定碼無法還原，需換發授權。");
        }

        return 0;
    }

    private static string DescribeStorage(string source)
    {
        if (!File.Exists(source)) return "不適用（錨定碼來自環境變數）";
        try
        {
            return File.ReadAllText(source).TrimStart()
                .StartsWith(MachineAnchor.ProtectedPrefix, StringComparison.Ordinal)
                ? "Windows DPAPI 封裝（LocalMachine）"
                : "純文字";
        }
        catch
        {
            return "無法判讀";
        }
    }

    private static bool TryDescribeExisting(out string path)
    {
        foreach (var candidate in MachineAnchor.CandidatePaths())
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }
        path = string.Empty;
        return false;
    }

    // ── 參數解析 ─────────────────────────────────────────────────────────────
    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string DescribeDeploy(DeploymentKind kind) => kind switch
    {
        DeploymentKind.Physical  => "實體機（授權綁主機板，不需連線）",
        DeploymentKind.Wsl       => "WSL（授權綁 Windows 主機的主機板，不需連線）",
        DeploymentKind.Vm        => "虛擬機（授權每 90 天確認一次）",
        DeploymentKind.Container => "容器（授權每 90 天確認一次）",
        _                        => "無法判斷（依主機識別碼有無決定授權方式）",
    };

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            SmartFactory MES — 機器錨定碼工具（指紋演算法 {MachineAnchor.AlgoVersion}）

            用法：
              SmartFactory.MES.MachineId                    顯示環境指紋、主機識別碼與授權申請碼
              SmartFactory.MES.MachineId --server <網址>    登記網址改用指定的授權伺服器
              SmartFactory.MES.MachineId --raw              同時顯示錨定碼原文（診斷用）
              SmartFactory.MES.MachineId --init             產生錨定碼（已存在則拒絕）
              SmartFactory.MES.MachineId --init --force     覆寫既有錨定碼
              SmartFactory.MES.MachineId --init --path <檔> 指定錨定檔位置
              SmartFactory.MES.MachineId --init --no-protect
                                                            不做 DPAPI 封裝（僅 Windows 有差別）
              SmartFactory.MES.MachineId --protect          就地封裝既有錨定檔（Windows）
              SmartFactory.MES.MachineId --unprotect        還原為純文字
              SmartFactory.MES.MachineId --help             顯示此說明

            錨定碼取得順序：
              1. 環境變數 {MachineAnchor.InstanceIdEnvVar}（僅 CI／開發；正式授權不接受）
              2. 環境變數 {MachineAnchor.AnchorPathEnvVar} 指定的檔案
                 （設定後只讀此路徑，不再回退預設位置）
              3. 系統位置：{MachineAnchor.SystemAnchorPath}
              4. 使用者位置：{MachineAnchor.UserAnchorPath}

            ⚠️ 錨定碼等同金鑰材料：它同時是連線字串加密的金鑰來源。
               請比照密碼保護，正式環境權限設為 0640（owner root、group 為服務帳號）。
               正因如此，錨定碼原文預設不顯示——申請授權只需要「申請碼」（內含指紋與主機識別碼，皆為雜湊）。

            ⚠️ 重新產生錨定碼會得到新的指紋，既有授權與已加密的連線字串都會失效。

            Windows DPAPI 封裝（--protect / --init 預設開啟）：
               錨定檔以本機金鑰加密存放，複製到其他電腦即無法解開。
               封裝**不改變錨定碼本身**，因此指紋不變、既有授權繼續適用。
               非 Windows 平台一律純文字，行為與未封裝時相同。
               ⚠️ 作業系統重灌後 DPAPI 主金鑰即不同，封裝過的錨定檔無法還原，需換發授權。
            """);
    }
}
