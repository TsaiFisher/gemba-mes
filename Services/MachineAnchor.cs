using System.Security.Cryptography;
using System.Text;

namespace SmartFactory.MES.Services;

/// <summary>
/// 機器錨定碼 — v2 環境指紋的唯一來源。
///
/// <para>
/// 指紋演算法：<c>SHA256(UTF8("v2:" + anchorId))</c>，輸出 64 字元大寫 Hex。
/// anchorId 為一次性產生的 GUID（128 位元隨機），由 <c>SmartFactory.MES.MachineId --init</c> 建立。
/// </para>
///
/// <para><b>為什麼不再偵測環境</b>：v1 依環境選擇因子（machine-id / MAC / Windows UUID），
/// 導致三個已證實的缺口 —— (1) Windows 原生無 <c>/etc/machine-id</c>，指紋僅由電腦名與 MAC
/// 組成，兩者區網可得，等同無祕密；(2) WSL 分支的 Windows UUID 可用 PATH 劫持
/// <c>powershell.exe</c> 任意偽造；(3) 指紋穩定性與 WSL interop 強耦合，2026-09-14 曾因
/// apt 升級連帶清除 binfmt_misc 的 WSLInterop 而整組失效。
/// 詳見 SECURITY-OPERATIONS.md §14。
/// </para>
///
/// <para><b>⚠️ anchorId 等同金鑰材料</b>：它同時是連線字串加密的 KDF 輸入
/// （<c>PBKDF2(指紋 + licenseId)</c>，而指紋是 anchorId 的確定性函數），
/// 必須比照密碼保護（正式環境權限 0640，owner root、group 為服務帳號）。
/// </para>
///
/// <para><b>此類別不會自動建立錨定檔</b>。找不到時一律拋出例外並提示建立指令；
/// 自動產生會讓「授權驗證失敗」偽裝成「悄悄換了一個身分」，且容器每次重建都會生出新值。
/// </para>
///
/// <para><b>Windows DPAPI 封裝（選用，僅影響儲存格式）</b>：Windows 上錨定檔可封裝為
/// <c>DPAPI:v1:{base64}</c>，以 <see cref="DataProtectionScope.LocalMachine"/> 保護，
/// 複製到他機即解不開。<b>封裝不改變 anchorId 本身，因此指紋與既有授權完全不受影響</b>
/// —— 它只改「錨定碼怎麼存在磁碟上」。非 Windows 平台一律純文字，行為與封裝前相同。
/// 詳見 SECURITY-OPERATIONS.md §17。
/// </para>
/// </summary>
internal static class MachineAnchor
{
    /// <summary>指紋演算法版本前綴。變更此值會使所有既有授權失效。</summary>
    internal const string AlgoVersion = "v2";

    /// <summary>
    /// 直接指定錨定碼（CI／測試用）。優先於任何檔案。
    /// <para>只有內部授權（SMARTFACTORY-INTERNAL，限 Development）接受以此來源算出的指紋；
    /// 其他授權一律拒絕，見 <c>LicenseValidator.EnsureAnchorSourceAllowed</c>。
    /// 正式部署（含容器）請改用錨定檔（容器以 volume 掛載）。SECURITY-OPERATIONS.md §18.11。</para>
    /// </summary>
    internal const string InstanceIdEnvVar = "SMARTFACTORY_INSTANCE_ID";

    /// <summary>指定錨定檔位置。設定後**只**讀此路徑，不再回退預設位置。</summary>
    internal const string AnchorPathEnvVar = "SMARTFACTORY_ANCHOR_PATH";

    /// <summary>
    /// DPAPI 封裝格式的前綴。錨定碼是 GUID（僅 Hex 與連字號），不可能以此開頭，
    /// 因此前綴即可無歧義地辨識儲存格式，不需要額外的中繼資料檔。
    /// </summary>
    internal const string ProtectedPrefix = "DPAPI:v1:";

    /// <summary>
    /// 計算目前環境的指紋。找不到錨定碼時拋出 <see cref="InvalidOperationException"/>。
    /// </summary>
    internal static string GetFingerprint()
    {
        var raw = $"{AlgoVersion}:{ResolveAnchor().Id}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>目前的錨定碼是否來自 <see cref="InstanceIdEnvVar"/>（與 <see cref="ResolveAnchor"/> 的第一順位判斷一致）。</summary>
    internal static bool IsEnvironmentOverrideActive()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InstanceIdEnvVar));

    /// <summary>
    /// 解析錨定碼，回傳值與來源描述（來源僅供診斷輸出，不參與指紋計算）。
    /// </summary>
    internal static (string Id, string Source) ResolveAnchor()
    {
        if (IsEnvironmentOverrideActive())
            return (Environment.GetEnvironmentVariable(InstanceIdEnvVar)!.Trim(), $"環境變數 {InstanceIdEnvVar}");

        foreach (var path in CandidatePaths())
        {
            var id = TryReadAnchorFile(path);
            if (id != null)
                return (id, path);
        }

        throw new InvalidOperationException(BuildMissingAnchorMessage());
    }

    /// <summary>
    /// 依序回傳要嘗試的錨定檔路徑。
    /// 若設定了 <see cref="AnchorPathEnvVar"/> 則只回傳該路徑（明確指定就不該靜默回退）。
    /// </summary>
    internal static IEnumerable<string> CandidatePaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable(AnchorPathEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath.Trim();
            yield break;
        }

        yield return SystemAnchorPath;
        yield return UserAnchorPath;
    }

    /// <summary>正式部署位置。權限應為 0640（owner root、group 為服務帳號）。</summary>
    internal static string SystemAnchorPath => OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SmartFactory", "instance-id")
        : "/etc/smartfactory/instance-id";

    /// <summary>
    /// 開發位置（無 root 權限時使用）。系統位置存在時一律優先，因此正式機上的
    /// 使用者層殘留檔不會被採用。
    /// </summary>
    internal static string UserAnchorPath => OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartFactory", "instance-id")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "smartfactory", "instance-id");

    private static string? TryReadAnchorFile(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path).Trim();
        }
        catch
        {
            // 權限不足等同不存在，繼續嘗試下一個候選路徑
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        // 舊格式（純文字）與封裝格式並存：先試封裝前綴，沒有就當純文字。
        // 順序不可顛倒——把密文當明文用會算出一個「看起來正常」的錯誤指紋。
        return text.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
            ? Unprotect(text, path)
            : text;
    }

    /// <summary>
    /// 解開 DPAPI 封裝的錨定檔。
    ///
    /// <para>
    /// 這裡刻意**拋例外而不是回退下一個候選路徑**。解不開最典型的成因就是
    /// 「錨定檔被複製到別台機器」，那正是這層封裝要擋的事；若靜默跳過，
    /// 症狀會偽裝成「找不到錨定碼」，而那則訊息會引導使用者執行 <c>--init</c>
    /// —— 等於把「授權不適用」變成「悄悄換了一個身分」。
    /// </para>
    /// </summary>
    private static string Unprotect(string text, string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                $"錨定檔為 Windows DPAPI 封裝格式，無法在此平台讀取：{path}\n" +
                "此格式只能在產生它的那台 Windows 電腦上解開。\n" +
                $"（若此環境應使用自己的錨定碼，請執行 SmartFactory.MES.MachineId --init，" +
                "並注意新的指紋需要重新申請授權）");

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(text[ProtectedPrefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"錨定檔內容毀損（Base64 無法解析）：{path}\n" +
                "請以備份還原，或重新產生錨定碼並換發授權。", ex);
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"錨定檔無法在本機解開：{path}\n" +
                "此檔以 Windows DPAPI（LocalMachine）封裝，只能在產生它的那台電腦上解開。\n" +
                "常見原因：\n" +
                "  1. 錨定檔是從其他電腦複製過來的 —— 請於本機申請專屬授權，不要沿用他機的錨定檔。\n" +
                "  2. 作業系統重新安裝過 —— DPAPI 主金鑰已不同，需重新產生錨定碼並換發授權。\n" +
                $"原始錯誤：{ex.Message}", ex);
        }

        var id = Encoding.UTF8.GetString(plain).Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException(
                $"錨定檔解開後內容為空：{path}\n請以備份還原，或重新產生錨定碼並換發授權。");

        return id;
    }

    private static string BuildMissingAnchorMessage()
    {
        var tried = string.Join("\n", CandidatePaths().Select(p => $"    {p}"));
        return
            "找不到機器錨定碼，無法計算環境指紋。\n" +
            "已嘗試的位置：\n" + tried + "\n" +
            $"（環境變數 {InstanceIdEnvVar} 僅供 CI／開發，正式授權不接受）\n\n" +
            "請執行下列指令產生後重試：\n" +
            "    SmartFactory.MES.MachineId --init\n\n" +
            "⚠️ 產生新的錨定碼會得到新的指紋，既有授權將不適用，需重新申請。";
    }

    // ── 建立錨定檔（僅 MachineId 工具呼叫，應用程式執行期不建立）────────────────

    /// <summary>
    /// 建立錨定檔並回傳新的錨定碼。
    /// </summary>
    /// <param name="path">目標路徑；null 表示自動選擇（優先系統位置，無權限時退至使用者位置）。</param>
    /// <param name="force">目標已存在時是否覆寫。覆寫會使既有授權失效。</param>
    /// <param name="protect">Windows 上是否以 DPAPI 封裝儲存。非 Windows 平台一律忽略。</param>
    internal static (string Id, string Path, bool Protected) Create(string? path, bool force, bool protect = true)
    {
        // 若已明確指定讀取位置，就寫到同一個地方。否則 --init 會寫進預設位置，
        // 而應用程式只讀 AnchorPathEnvVar 指的檔案 —— 產生了錨定碼卻還是啟動不了。
        var explicitPath = Environment.GetEnvironmentVariable(AnchorPathEnvVar);
        if (path is null && !string.IsNullOrWhiteSpace(explicitPath))
            path = explicitPath.Trim();

        path ??= ChooseWritablePath();

        if (File.Exists(path) && !force)
            throw new InvalidOperationException(
                $"錨定檔已存在：{path}\n" +
                "若確定要重新產生，請加上 --force。\n" +
                "⚠️ 重新產生會變更指紋，既有授權與已加密的連線字串都將失效。");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var wrote = WriteAnchorFile(path, id, protect);

        return (id, path, wrote);
    }

    /// <summary>
    /// 就地切換既有錨定檔的儲存格式。<b>不改變 anchorId，因此指紋不變、既有授權續用。</b>
    /// </summary>
    /// <param name="path">錨定檔路徑；null 表示取用第一個存在的候選位置。</param>
    /// <param name="protect">true 封裝、false 還原為純文字。</param>
    /// <returns>錨定碼、實際處理的路徑，以及是否原本就已是目標格式。</returns>
    internal static (string Id, string Path, bool AlreadyInTargetFormat) Reprotect(string? path, bool protect)
    {
        path ??= CandidatePaths().FirstOrDefault(File.Exists)
                 ?? throw new InvalidOperationException(BuildMissingAnchorMessage());

        if (!File.Exists(path))
            throw new InvalidOperationException($"找不到錨定檔：{path}");

        if (protect && !OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "DPAPI 封裝僅適用於 Windows。此平台的錨定檔請以檔案權限保護（0640，見 §14.5）。");

        // 走與應用程式相同的讀取路徑：此時若是他機複製來的封裝檔，會在這裡就報錯
        var id = TryReadAnchorFile(path)
                 ?? throw new InvalidOperationException($"錨定檔內容為空或無法讀取：{path}");

        var isProtected = File.ReadAllText(path).Trim()
            .StartsWith(ProtectedPrefix, StringComparison.Ordinal);

        if (isProtected == protect)
            return (id, path, true);

        WriteAnchorFile(path, id, protect);
        return (id, path, false);
    }

    /// <summary>
    /// 寫入錨定檔：先寫暫存檔、回讀驗證、才覆蓋正本。
    ///
    /// <para>
    /// 直接覆寫的風險是：若封裝寫得出來卻解不開（DPAPI 設定異常），
    /// 正本已被換掉而錨定碼已無從還原 —— 等同授權與已加密連線字串一起失效。
    /// 因此一律「寫→驗→換」。
    /// </para>
    /// </summary>
    /// <returns>實際是否以封裝格式寫入。</returns>
    private static bool WriteAnchorFile(string path, string id, bool protect)
    {
        // 展開成 if 區塊而非三元式：CA1416 的平台分析追得到 if 守衛，
        // 追不到「先存進區域變數再用」的寫法。
        var content = id;
        var useDpapi = false;
        if (protect && OperatingSystem.IsWindows())
        {
            content = ProtectedPrefix + Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(id), optionalEntropy: null, DataProtectionScope.LocalMachine));
            useDpapi = true;
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content + Environment.NewLine);
            ApplyRestrictivePermissions(temp);

            var roundTripped = TryReadAnchorFile(temp);
            if (!string.Equals(roundTripped, id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "寫入後回讀驗證失敗，錨定檔未被更動。" +
                    (useDpapi ? "請確認本機 DPAPI 可用，或改用 --no-protect。" : string.Empty));

            File.Move(temp, path, overwrite: true);
            ApplyRestrictivePermissions(path);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* 暫存檔殘留不影響正確性 */ }
            }
        }

        return useDpapi;
    }

    /// <summary>
    /// 選擇可寫入的路徑：優先系統位置（正式部署），無權限時退至使用者位置（開發機）。
    /// </summary>
    private static string ChooseWritablePath()
    {
        var system = SystemAnchorPath;
        try
        {
            var dir = Path.GetDirectoryName(system)!;
            Directory.CreateDirectory(dir);
            // 實際寫入測試：CreateDirectory 成功不代表檔案可建立
            var probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return system;
        }
        catch
        {
            return UserAnchorPath;
        }
    }

    private static void ApplyRestrictivePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            // 0640：owner 讀寫、group 唯讀、others 無權限。
            // 正式部署另需 chown root:<服務帳號>，見 SECURITY-OPERATIONS.md §14.5。
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        catch
        {
            // 檔案系統不支援 POSIX 權限（例如 Windows 掛載點）時略過，不視為失敗
        }
    }
}
