using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.License;

/// <summary>
/// 主機識別碼（SECURITY-OPERATIONS.md §18.15.2）。以假的 <see cref="IHostProbe"/> 模擬各平台，
/// 讓 Windows／Linux／WSL／容器的判斷都能在任何一台機器上測。
/// </summary>
public class HostIdentityTests
{
    private const string Uuid = "FEC9F576-9B18-C343-A542-8FD7D12A7B7E";   // 桌機實測值的格式
    private static readonly string Hash = HostIdentity.HashUuid(Uuid.ToLowerInvariant());

    private sealed class FakeProbe : IHostProbe
    {
        public bool IsWindows { get; init; }
        public bool IsLinux { get; init; }
        public Dictionary<string, string> Files { get; } = new();
        public Dictionary<(string, string), string> Registry { get; } = new();
        public Dictionary<string, string?> Commands { get; } = new();
        public List<string> Ran { get; } = new();

        public string? ReadFile(string path) => Files.TryGetValue(path, out var v) ? v : null;
        public bool FileExists(string path) => Files.ContainsKey(path);
        public string? ReadRegistry(string subKey, string valueName)
            => Registry.TryGetValue((subKey, valueName), out var v) ? v : null;
        public string? Run(string fileName, string arguments)
        {
            Ran.Add(fileName);
            return Commands.TryGetValue(arguments, out var v) ? v : null;
        }
    }

    private const string RegCfgOut = "\nHKEY_LOCAL_MACHINE\\SYSTEM\\HardwareConfig\n    LastConfig    REG_SZ    {" + "fec9f576-9b18-c343-a542-8fd7d12a7b7e" + "}\n";
    private const string RegBiosAsus = "\nHKEY_LOCAL_MACHINE\\HARDWARE\\DESCRIPTION\\System\\BIOS\n    SystemManufacturer    REG_SZ    ASUSTeK COMPUTER INC.\n    SystemProductName    REG_SZ    ASUS EXPERTCENTER D900MDR_M900MDR\n";
    private const string RegBiosHyperV = "\n    SystemManufacturer    REG_SZ    Microsoft Corporation\n    SystemProductName    REG_SZ    Virtual Machine\n";

    private static FakeProbe Wsl(string? bios = RegBiosAsus, string? cfg = RegCfgOut)
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.WslInteropFile] = "enabled";
        p.Files[HostIdentity.WslDefaultReg] = "";
        p.Commands[@"query HKLM\SYSTEM\HardwareConfig /v LastConfig"] = cfg;
        p.Commands[@"query HKLM\HARDWARE\DESCRIPTION\System\BIOS"] = bios;
        return p;
    }

    // ── 正規化與雜湊 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("{FEC9F576-9B18-C343-A542-8FD7D12A7B7E}")]
    [InlineData("  fec9f576-9b18-c343-a542-8fd7d12a7b7e\n")]
    [InlineData("FEC9F576-9B18-C343-A542-8FD7D12A7B7E")]
    public void Normalize_IgnoresBracesCaseAndWhitespace(string raw)
        => Assert.Equal("fec9f576-9b18-c343-a542-8fd7d12a7b7e", HostIdentity.NormalizeUuid(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    [InlineData("03000200-0400-0500-0006-000700080009")]   // 廉價主機板常見預設值：同型號全部相同
    public void Normalize_RejectsInvalidAndPlaceholderValues(string? raw)
        => Assert.Null(HostIdentity.NormalizeUuid(raw));

    [Fact]
    public void Hash_IsDomainSeparatedSha256_UpperHex_NotTheRawUuid()
    {
        var h = HostIdentity.HashUuid("fec9f576-9b18-c343-a542-8fd7d12a7b7e");
        Assert.Matches("^[0-9A-F]{64}$", h);
        Assert.DoesNotContain("FEC9F576", h);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("host:v1:fec9f576-9b18-c343-a542-8fd7d12a7b7e"))), h);
    }

    // ── VM 判斷 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("VMware, Inc.", "VMware Virtual Platform")]
    [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)")]
    [InlineData("innotek GmbH", "VirtualBox")]
    [InlineData("Microsoft Corporation", "Virtual Machine")]
    [InlineData("Amazon EC2", "m5.large")]
    [InlineData("Xen", "HVM domU")]
    public void IsVmVendor_KnownHypervisors(string m, string p) => Assert.True(HostIdentity.IsVmVendor(m, p));

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS EXPERTCENTER D900MDR_M900MDR")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5")]   // 微軟的實體機不是 VM
    [InlineData("Dell Inc.", "OptiPlex 7090")]
    [InlineData(null, null)]
    public void IsVmVendor_PhysicalMachines(string? m, string? p) => Assert.False(HostIdentity.IsVmVendor(m, p));

    // ── Windows ──────────────────────────────────────────────────────────

    [Fact]
    public void Windows_ReadsHardwareConfig_Physical_SuggestsOffline()
    {
        var p = new FakeProbe { IsWindows = true };
        p.Registry[(HostIdentity.WinHardwareConfigKey, "LastConfig")] = "{" + Uuid.ToLowerInvariant() + "}";
        p.Registry[(HostIdentity.WinBiosKey, "SystemManufacturer")] = "ASUSTeK COMPUTER INC.";
        var r = HostIdentity.Detect(p);
        Assert.Equal((Hash, DeploymentKind.Physical, (string?)null), (r.HostId, r.Kind, r.Problem));
        Assert.Equal(LicenseBindingModes.Offline, r.SuggestedBindingMode);
        Assert.Equal("physical", r.DeployCode);
    }

    [Fact]
    public void Windows_InVm_SuggestsLease_EvenThoughUuidIsReadable()
    {
        var p = new FakeProbe { IsWindows = true };
        p.Registry[(HostIdentity.WinHardwareConfigKey, "LastConfig")] = Uuid;
        p.Registry[(HostIdentity.WinBiosKey, "SystemManufacturer")] = "VMware, Inc.";
        var r = HostIdentity.Detect(p);
        Assert.Equal(DeploymentKind.Vm, r.Kind);
        Assert.NotNull(r.HostId);
        Assert.Equal(LicenseBindingModes.Lease, r.SuggestedBindingMode);   // VM 的 UUID 在設定檔裡，改得掉
    }

    [Fact]
    public void Windows_Unreadable_ReportsProblem_NoHostId()
    {
        var r = HostIdentity.Detect(new FakeProbe { IsWindows = true });
        Assert.Null(r.HostId);
        Assert.NotNull(r.Problem);
        Assert.Equal(LicenseBindingModes.Lease, r.SuggestedBindingMode);
    }

    // ── WSL ──────────────────────────────────────────────────────────────

    [Fact]
    public void Wsl_ReadsHostViaAbsolutePathRegExe()
    {
        var p = Wsl();
        var r = HostIdentity.Detect(p);
        Assert.Equal((Hash, DeploymentKind.Wsl, (string?)null), (r.HostId, r.Kind, r.Problem));
        Assert.All(p.Ran, f => Assert.Equal(HostIdentity.WslDefaultReg, f));   // 不經 PATH
        Assert.Equal(LicenseBindingModes.Offline, r.SuggestedBindingMode);
    }

    [Fact]
    public void Wsl_InsideAVmHost_IsVm()
        => Assert.Equal(DeploymentKind.Vm, HostIdentity.Detect(Wsl(bios: RegBiosHyperV)).Kind);

    [Fact]
    public void Wsl_InteropBroken_ProblemSaysHowToFix_NoHostId()
    {
        var r = HostIdentity.Detect(Wsl(cfg: null));
        Assert.Null(r.HostId);
        Assert.Contains("systemd-binfmt", r.Problem);
        Assert.Equal(DeploymentKind.Wsl, r.Kind);
    }

    [Fact]
    public void Wsl_CustomAutomountRoot_FoundViaProcMounts()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.WslInteropFile] = "enabled";
        p.Files["/proc/mounts"] = "none /usr/lib/wsl/drivers 9p ro 0 0\nC:\\ /win/c 9p rw,relatime,aname=drvfs;path=C:\\;uid=1000 0 0\n";
        p.Files["/win/c/Windows/System32/reg.exe"] = "";
        Assert.Equal("/win/c/Windows/System32/reg.exe", HostIdentity.FindWslRegExe(p));
    }

    [Fact]
    public void Wsl_NoCDrive_Problem()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files["/proc/version"] = "Linux version 5.15.167.4-microsoft-standard-WSL2";
        var r = HostIdentity.Detect(p);
        Assert.Equal(DeploymentKind.Wsl, r.Kind);
        Assert.Contains("reg.exe", r.Problem);
    }

    // ── Linux ────────────────────────────────────────────────────────────

    [Fact]
    public void Linux_PrefersRunFileWrittenByRoot()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.LinuxRunFile] = Uuid + "\n";
        p.Files[HostIdentity.LinuxDmiVendor] = "Dell Inc.";
        var r = HostIdentity.Detect(p);
        Assert.Equal((Hash, DeploymentKind.Physical, "run-file"), (r.HostId, r.Kind, r.Source));
    }

    private const string CpuInfoPhysical = "processor\t: 0\nvendor_id\t: GenuineIntel\nflags\t\t: fpu vme de pse tsc msr pae vmx smx est\n";
    private const string CpuInfoGuest = "processor\t: 0\nvendor_id\t: GenuineIntel\nflags\t\t: fpu vme de pse tsc msr pae hypervisor lahf_lm\n";

    [Fact]
    public void Linux_VendorSpoofedAsPhysical_ButHypervisorFlag_IsVm()
    {
        // 10/14 實測：QEMU 以 -smbios 把廠牌／型號／UUID 全偽裝成宿主，只看廠牌會判成實體機
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.LinuxRunFile] = Uuid;
        p.Files[HostIdentity.LinuxDmiVendor] = "ASUSTeK COMPUTER INC.";
        p.Files[HostIdentity.LinuxDmiProduct] = "ASUS EXPERTCENTER D900MDR_M900MDR";
        p.Files[HostIdentity.LinuxCpuInfo] = CpuInfoGuest;
        var r = HostIdentity.Detect(p);
        Assert.Equal((Hash, DeploymentKind.Vm), (r.HostId, r.Kind));
    }

    [Fact]
    public void Linux_PhysicalCpuFlags_StayPhysical()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.LinuxRunFile] = Uuid;
        p.Files[HostIdentity.LinuxDmiVendor] = "Dell Inc.";
        p.Files[HostIdentity.LinuxCpuInfo] = CpuInfoPhysical;   // vmx（可做虛擬化）≠ hypervisor（本身是客體）
        Assert.Equal(DeploymentKind.Physical, HostIdentity.Detect(p).Kind);
    }

    [Fact]
    public void Wsl_HypervisorFlag_DoesNotTurnItIntoVm()
    {
        var p = Wsl();
        p.Files[HostIdentity.LinuxCpuInfo] = CpuInfoGuest;     // WSL2 核心本來就跑在 Hyper-V 上
        Assert.Equal(DeploymentKind.Wsl, HostIdentity.Detect(p).Kind);
    }

    [Theory]
    [InlineData("flags\t\t: fpu hypervisor sse", true)]
    [InlineData("flags\t\t: fpu hypervisorx sse", false)]
    [InlineData("vmx flags\t: hypervisor", false)]      // 不是 flags 欄（例如 "vmx flags"）
    [InlineData(null, false)]
    public void HypervisorFlag_Parsing(string? cpuinfo, bool expected)
        => Assert.Equal(expected, HostIdentity.HasHypervisorFlag(cpuinfo));

    [Fact]
    public void Linux_FallsBackToDmiWhenRunningAsRoot()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.LinuxDmiUuid] = Uuid;
        Assert.Equal((Hash, "dmi"), (HostIdentity.Detect(p).HostId, HostIdentity.Detect(p).Source));
    }

    [Fact]
    public void Linux_NotReadable_ProblemTellsTheSystemdLine()
    {
        var r = HostIdentity.Detect(new FakeProbe { IsLinux = true });
        Assert.Null(r.HostId);
        Assert.Contains("ExecStartPre=+", r.Problem);
        Assert.Contains(HostIdentity.LinuxRunFile, r.Problem);
    }

    [Fact]
    public void Linux_VmVendor_IsVm()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files[HostIdentity.LinuxRunFile] = Uuid;
        p.Files[HostIdentity.LinuxDmiVendor] = "QEMU";
        Assert.Equal(DeploymentKind.Vm, HostIdentity.Detect(p).Kind);
    }

    [Fact]
    public void Container_WithoutMountedHostId_SuggestsLease_AndSaysHowToMount()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files["/.dockerenv"] = "";
        var r = HostIdentity.Detect(p);
        Assert.Equal(DeploymentKind.Container, r.Kind);
        Assert.Null(r.HostId);
        Assert.Contains("每 90 天", r.Problem);
        Assert.Equal(LicenseBindingModes.Lease, r.SuggestedBindingMode);
    }

    /// <summary>
    /// WSL 上的 Docker（Docker Desktop／WSL 內的 dockerd）：容器的 /proc/version 同樣帶 microsoft。
    /// 2026-10-19 實測曾被判成 WSL、去找 reg.exe——容器必須優先判定；掛載進來的宿主 host-id 也不讀（容器一律定期確認）。
    /// </summary>
    [Fact]
    public void Container_OnWslKernel_IsContainer_NotWsl()
    {
        var p = new FakeProbe { IsLinux = true };
        p.Files["/.dockerenv"] = "";
        p.Files["/proc/version"] = "Linux version 5.15.167.4-microsoft-standard-WSL2";
        var r = HostIdentity.Detect(p);
        Assert.Equal(DeploymentKind.Container, r.Kind);
        Assert.Null(r.HostId);

        // 掛載了宿主的 host-id 也不讀：容器一律定期確認（2026-10-19 裁決），不能靠掛載拿到綁主機授權
        p.Files[HostIdentity.LinuxRunFile] = Uuid;
        r = HostIdentity.Detect(p);
        Assert.Equal(DeploymentKind.Container, r.Kind);
        Assert.Null(r.HostId);
        Assert.Equal(LicenseBindingModes.Lease, r.SuggestedBindingMode);
    }

    [Fact]
    public void RegValue_ParsesRegExeOutput()
    {
        Assert.Equal("ASUSTeK COMPUTER INC.", HostIdentity.RegValue(RegBiosAsus, "SystemManufacturer"));
        Assert.Null(HostIdentity.RegValue(RegBiosAsus, "Nope"));
    }

    /// <summary>真機冒煙：這台是 WSL 就實際讀一次（不比對值，只確認讀得到且穩定）。CI 不是 WSL 則略過。</summary>
    [Fact]
    public void RealMachine_IfWsl_ReadsStableHostId()
    {
        var first = HostIdentity.Detect();
        if (first.Kind != DeploymentKind.Wsl) return;
        Assert.NotNull(first.HostId);
        Assert.Equal(first.HostId, HostIdentity.Detect().HostId);
    }
}
