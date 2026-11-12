using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFactory.MES.Services;

/// <summary>部署類型（SECURITY-OPERATIONS.md §18.15.2）。只用於申請時建議綁定方式，驗證時不看。</summary>
public enum DeploymentKind { Unknown = 0, Physical = 1, Vm = 2, Wsl = 3, Container = 4 }

/// <summary>主機識別結果。<see cref="HostId"/> 為雜湊（不是 UUID 原值）；取不到時為 null，<see cref="Problem"/> 說明原因。</summary>
public sealed record HostIdentityResult(string? HostId, DeploymentKind Kind, string Source, string? Problem)
{
    /// <summary>申請碼用的部署類型字串。</summary>
    public string DeployCode => Kind switch
    {
        DeploymentKind.Physical => "physical",
        DeploymentKind.Vm => "vm",
        DeploymentKind.Wsl => "wsl",
        DeploymentKind.Container => "container",
        _ => "unknown",
    };

    /// <summary>此部署建議的綁定方式：取得到主機識別碼的實體機／WSL 走 offline，其餘走 lease（定期確認）。</summary>
    public string SuggestedBindingMode =>
        HostId is not null && Kind is DeploymentKind.Physical or DeploymentKind.Wsl
            ? LicenseBindingModes.Offline : LicenseBindingModes.Lease;
}

/// <summary>讀取主機資訊的來源；抽出來是為了讓各平台的判斷能在任何一台機器上測。</summary>
public interface IHostProbe
{
    bool IsWindows { get; }
    bool IsLinux { get; }
    /// <summary>讀檔；不存在或沒有權限回 null。</summary>
    string? ReadFile(string path);
    bool FileExists(string path);
    /// <summary>讀 Windows 登錄（HKLM）；非 Windows 或讀不到回 null。</summary>
    string? ReadRegistry(string subKey, string valueName);
    /// <summary>執行外部程式取 stdout；失敗或逾時回 null。</summary>
    string? Run(string fileName, string arguments);
}

/// <summary>
/// 主機識別碼（SECURITY-OPERATIONS.md §18.15.2）：授權綁定「錨定碼指紋＋主機板 SMBIOS UUID」，
/// 讓「整顆磁碟／整個 WSL 複製到別台電腦」時對不上——UUID 在主機板韌體裡，不在被複製的磁碟上。
///
/// <para><b>只存雜湊</b>：<c>SHA256("host:v1:" + 小寫 UUID)</c>。授權檔、申請碼都不帶原值。</para>
///
/// <para><b>讀不到不拋例外</b>：回傳 <see cref="HostIdentityResult.Problem"/>（含修復方式），由 <see cref="LicenseHostGate"/>
/// 決定。2026-10-17 起讀不到也擋（§18.15.9）——只提醒的話，停用讀取管道（例如關掉 WSL interop）就能繞過。
/// 代價是讀取管道故障時正版也起不來（2026-09 binfmt flush 的情境），所以 Problem 必須寫出怎麼修。</para>
/// </summary>
public static class HostIdentity
{
    internal const string HashPrefix = "host:v1:";

    /// <summary>Linux：systemd <c>ExecStartPre=+</c> 以 root 寫入的 UUID（/run 是 tmpfs，不隨磁碟複製）。</summary>
    public const string LinuxRunFile = "/run/smartfactory/host-id";
    internal const string LinuxDmiUuid = "/sys/class/dmi/id/product_uuid";
    internal const string LinuxDmiVendor = "/sys/class/dmi/id/sys_vendor";
    internal const string LinuxDmiProduct = "/sys/class/dmi/id/product_name";
    internal const string LinuxCpuInfo = "/proc/cpuinfo";
    internal const string WslInteropFile = "/proc/sys/fs/binfmt_misc/WSLInterop";
    internal const string WslDefaultReg = "/mnt/c/Windows/System32/reg.exe";

    internal const string WinHardwareConfigKey = @"SYSTEM\HardwareConfig";
    internal const string WinBiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    /// <summary>廉價主機板常見的預設值與佔位值：當作取不到，否則同型號主機全部「相同」。</summary>
    private static readonly HashSet<string> JunkUuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "00000000-0000-0000-0000-000000000000",
        "ffffffff-ffff-ffff-ffff-ffffffffffff",
        "03000200-0400-0500-0006-000700080009",
        "12345678-1234-5678-90ab-cddeefaabbcc",
    };

    /// <summary>廠牌／型號比對（小寫子字串）。⚠️ 不可用 HypervisorPresent：實體機開了 WSL／Hyper-V 也回 True。</summary>
    private static readonly string[] VmMarkers =
    [
        "vmware", "qemu", "kvm", "xen", "innotek", "virtualbox", "parallels", "bochs",
        "virtual machine", "amazon ec2", "google compute engine", "openstack", "ovirt", "proxmox",
        "nutanix", "cloud hypervisor", "bhyve", "alibaba cloud ecs", "droplet",
    ];

    private static readonly Regex GuidLike = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled);

    /// <summary>正規化 UUID：去空白與大括號、轉小寫；格式不對或是佔位值回 null。</summary>
    public static string? NormalizeUuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('{', '}').Trim().ToLowerInvariant();
        return GuidLike.IsMatch(s) && !JunkUuids.Contains(s) ? s : null;
    }

    /// <summary>主機識別碼雜湊（大寫十六進位 64 字元）。</summary>
    public static string HashUuid(string normalizedUuid)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HashPrefix + normalizedUuid)));

    /// <summary>廠牌或型號看起來是虛擬機。</summary>
    public static bool IsVmVendor(string? manufacturer, string? product)
    {
        var s = $"{manufacturer} {product}".ToLowerInvariant();
        if (s.Contains("microsoft corporation") && s.Contains("virtual")) return true;
        return VmMarkers.Any(s.Contains);
    }

    /// <summary>
    /// Linux：CPU 旗標有 <c>hypervisor</c>（虛擬機的 CPUID 位元，KVM／VMware／Hyper-V 客體預設都會設）。
    /// 廠牌／型號可在 VM 設定檔偽裝成實體機，這個旗標要另外關才藏得住（SECURITY-OPERATIONS.md §18.15.8 實測）。
    /// ⚠️ 只用於 Linux 實體機分支：WSL 與 Windows（開了 VBS／Hyper-V）的實體機也會有這個旗標，那兩條路徑先判斷掉了。
    /// </summary>
    internal static bool HasHypervisorFlag(string? cpuinfo)
    {
        foreach (var line in (cpuinfo ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !line[..colon].Trim().Equals("flags", StringComparison.OrdinalIgnoreCase)) continue;
            if (line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("hypervisor")) return true;
        }
        return false;
    }

    /// <summary>以實際系統讀取（結果在行程內不會變，呼叫端自行快取）。</summary>
    public static HostIdentityResult Detect() => Detect(new SystemHostProbe());

    public static HostIdentityResult Detect(IHostProbe p) => DetectWithUuid(p).Host;

    /// <summary>
    /// 連同正規化後的 UUID <b>原值</b>一起回傳，只供連線字串金鑰衍生（ENC:v3，SECURITY-OPERATIONS.md §18.15.9）。
    /// 授權檔與申請碼只有雜湊，所以複製品就算改掉程式裡的檢查，也推不出解密金鑰。
    /// <para>⚠️ 原值不得記錄、顯示或送出；取不到時為 null。</para>
    /// </summary>
    public static (HostIdentityResult Host, string? Uuid) DetectWithUuid() => DetectWithUuid(new SystemHostProbe());

    public static (HostIdentityResult Host, string? Uuid) DetectWithUuid(IHostProbe p)
    {
        if (p.IsWindows) return DetectWindows(p);
        // 容器先判：Docker Desktop／WSL 上的 Docker 跑的是 WSL 核心，/proc/version 也帶 microsoft，
        // 但容器內沒有 reg.exe、讀的也不該是開發機 Windows——一律走容器（不綁主機、定期確認）。
        if (p.IsLinux) return !IsContainer(p) && IsWsl(p) ? DetectWsl(p) : DetectLinux(p);
        return Missing(DeploymentKind.Unknown, "unsupported-os", "不支援的作業系統，無法讀取主機識別碼。");
    }

    private static (HostIdentityResult, string?) Found(string uuid, DeploymentKind kind, string source)
        => (new(HashUuid(uuid), kind, source, null), uuid);

    private static (HostIdentityResult, string?) Missing(DeploymentKind kind, string source, string problem)
        => (new(null, kind, source, problem), null);

    private static (HostIdentityResult, string?) DetectWindows(IHostProbe p)
    {
        var vm = IsVmVendor(p.ReadRegistry(WinBiosKey, "SystemManufacturer"), p.ReadRegistry(WinBiosKey, "SystemProductName"));
        var kind = vm ? DeploymentKind.Vm : DeploymentKind.Physical;
        var uuid = NormalizeUuid(p.ReadRegistry(WinHardwareConfigKey, "LastConfig"));
        return uuid is null
            ? Missing(kind, "registry", @"無法讀取 HKLM\SYSTEM\HardwareConfig\LastConfig（主機板 UUID）。")
            : Found(uuid, kind, "registry");
    }

    private static bool IsContainer(IHostProbe p)
        => p.FileExists("/.dockerenv") || p.FileExists("/run/.containerenv")
        || Regex.IsMatch(p.ReadFile("/proc/1/cgroup") ?? "", @"docker|kubepods|containerd|libpod");

    private static bool IsWsl(IHostProbe p)
        => p.FileExists(WslInteropFile)
        || (p.ReadFile("/proc/version") ?? "").Contains("microsoft", StringComparison.OrdinalIgnoreCase);

    private static (HostIdentityResult, string?) DetectWsl(IHostProbe p)
    {
        var reg = FindWslRegExe(p);
        if (reg is null)
            return Missing(DeploymentKind.Wsl, "wsl-reg",
                "WSL 內找不到 Windows 的 reg.exe（C: 磁碟未掛載？），無法讀取宿主主機識別碼。");

        var cfg = p.Run(reg, @"query HKLM\SYSTEM\HardwareConfig /v LastConfig");
        if (cfg is null)
            return Missing(DeploymentKind.Wsl, "wsl-reg",
                "無法執行 Windows 的 reg.exe（WSL 與 Windows 的互通被停用？可執行 `sudo systemctl restart systemd-binfmt` 或重開 WSL）。");

        var bios = p.Run(reg, @"query HKLM\HARDWARE\DESCRIPTION\System\BIOS");
        var vm = bios is not null && IsVmVendor(RegValue(bios, "SystemManufacturer"), RegValue(bios, "SystemProductName"));
        var kind = vm ? DeploymentKind.Vm : DeploymentKind.Wsl;   // WSL 跑在 VM 裡的 Windows：宿主本身可被複製
        var uuid = NormalizeUuid(RegValue(cfg, "LastConfig"));
        return uuid is null
            ? Missing(kind, "wsl-reg", "宿主 Windows 的主機板 UUID 格式無法辨識。")
            : Found(uuid, kind, "wsl-reg");
    }

    /// <summary>預設 /mnt/c；自訂掛載點（wsl.conf automount.root）時由 /proc/mounts 找 C: 的 drvfs。</summary>
    internal static string? FindWslRegExe(IHostProbe p)
    {
        if (p.FileExists(WslDefaultReg)) return WslDefaultReg;
        foreach (var line in (p.ReadFile("/proc/mounts") ?? "").Split('\n'))
        {
            var f = line.Split(' ');
            if (f.Length < 4) continue;
            var isC = f[0].StartsWith("C:", StringComparison.OrdinalIgnoreCase)
                   || (f[2] is "9p" or "drvfs" && f[3].Contains(@"path=C:", StringComparison.OrdinalIgnoreCase));
            if (!isC) continue;
            var candidate = f[1].TrimEnd('/') + "/Windows/System32/reg.exe";
            if (p.FileExists(candidate)) return candidate;
        }
        return null;
    }

    private static (HostIdentityResult, string?) DetectLinux(IHostProbe p)
    {
        // 容器一律不綁主機、每 90 天確認（2026-10-19 使用者裁決）：容器裡讀到的 host-id 只能是宿主掛載進來的，
        // 宿主若是 VM，UUID 可改；不讀它，就不會出現「掛載就能拿到免確認授權」的入口。
        if (IsContainer(p))
            return Missing(DeploymentKind.Container, "container",
                "容器部署不綁定主機，授權為每 90 天由管理員確認一次（瀏覽器代為確認，系統不連外）。");

        var vm = IsVmVendor(p.ReadFile(LinuxDmiVendor), p.ReadFile(LinuxDmiProduct))
                 || HasHypervisorFlag(p.ReadFile(LinuxCpuInfo));
        var kind = vm ? DeploymentKind.Vm : DeploymentKind.Physical;

        var fromRun = NormalizeUuid(p.ReadFile(LinuxRunFile));
        if (fromRun is not null) return Found(fromRun, kind, "run-file");

        var direct = NormalizeUuid(p.ReadFile(LinuxDmiUuid));   // 以 root 執行時讀得到
        if (direct is not null) return Found(direct, kind, "dmi");

        return Missing(kind, "run-file",
            $"讀不到主機識別碼：{LinuxRunFile} 不存在。請在 systemd 服務檔加入 " +
            $"ExecStartPre=+/bin/sh -c 'mkdir -p /run/smartfactory && cat {LinuxDmiUuid} > {LinuxRunFile}'。");
    }

    /// <summary>從 reg.exe query 的輸出取某個值（格式：<c>    名稱    REG_SZ    值</c>）。</summary>
    internal static string? RegValue(string regOutput, string name)
    {
        foreach (var raw in regOutput.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("REG_"))
                return parts[2].Trim();
        }
        return null;
    }
}

/// <summary>正式環境的 <see cref="IHostProbe"/>。</summary>
internal sealed class SystemHostProbe : IHostProbe
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsLinux => OperatingSystem.IsLinux();

    public string? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool FileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    public string? ReadRegistry(string subKey, string valueName)
        => OperatingSystem.IsWindows() ? ReadRegistryWindows(subKey, valueName) : null;

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryWindows(string subKey, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>以絕對路徑執行（不經 PATH 搜尋），5 秒逾時。</summary>
    public string? Run(string fileName, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(5000)) { try { proc.Kill(true); } catch { } return null; }
            return proc.ExitCode == 0 ? stdout.Result.Replace("\r", "") : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
