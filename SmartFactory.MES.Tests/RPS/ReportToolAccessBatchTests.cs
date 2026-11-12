using System.Security.Claims;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// <see cref="ReportToolAccessBatch"/> 的守門測試（§8.4 L1、§17.9 缺口 G1）。
///
/// <para><b>本檔存在的唯一理由是「兩條路徑必須同答案」。</b>批次版為了把 N 次 DB 往返
/// 收成一次，複製了 <see cref="ReportToolCatalog"/> 的動詞判定；複製就會漂移，
/// 而漂移的症狀是**沒有錯誤訊息**——只是看得到畫面的人與 LLM 跑得動的工具變成兩群人。
/// 所以逐支、逐動詞比對是這裡的主測試，不是附帶測試。</para>
/// </summary>
public class ReportToolAccessBatchTests : TestBase
{
    private const string RoleId = "role-1";

    private sealed class FakeTool : IReportToolDescriptor
    {
        public FakeTool(string key, ReportToolPermission permission)
        {
            ToolKey = key;
            Permission = permission;
        }

        public string ToolKey { get; }
        public string Description => $"fake:{ToolKey}";
        public ReportToolPermission Permission { get; }
        public bool ContainsMonetaryValues => false;   // 本測試不涉及機密閘門
        public bool RequiresPeriod => false;          // 本測試不涉及期間解算
        public Type RequestType => typeof(ReportRequestBase);
        public Type ResponseType => typeof(object);

        // 本測試不涉及匯出路徑；契約要求每支工具自己回答「資料列在哪」，故給空集合。
        public Task<IReadOnlyList<object>> GetExportRowsAsync(
            ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<object>>([]);

        // 假工具不匯出，欄位清單為空即可——本批測試看的是目錄、權限與 schema。
        public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) => [];
    }

    private IUserContext UserContext(string? roleId = RoleId)
    {
        var m = new Mock<IUserContext>();
        m.SetupGet(u => u.ActiveRoleId).Returns(roleId);
        return m.Object;
    }

    private void Grant(string menuKey, bool read = false, bool create = false, bool print = false)
    {
        _context.MenuPermissions.Add(new MenuPermission
        {
            RoleId = RoleId,
            MenuKey = menuKey,
            CanRead = read,
            CanCreate = create,
            CanPrint = print,
        });
        _context.SaveChanges();
    }

    private ReportToolAccessBatch Batch(IUserContext ctx, params IReportToolDescriptor[] tools)
        => new(new ReportToolCatalog(tools, Mock.Of<IPermissionService>()), _context, ctx);

    // ────────────────────────────────────────────────────────────────
    // 與 catalog 逐支比對：本檔的主測試
    // ────────────────────────────────────────────────────────────────

    [Theory]
    // 動詞 × 授權旗標的完整組合。CanRead 恆為 CanCreate 的真超集，
    // 所以「只有 CanRead 的人不該跑得動 CanCreate 工具」這一格特別重要。
    [InlineData(ReportToolPermission.CanRead,   true,  false, false, true)]
    [InlineData(ReportToolPermission.CanRead,   false, true,  true,  false)]
    [InlineData(ReportToolPermission.CanCreate, true,  false, false, false)]
    [InlineData(ReportToolPermission.CanCreate, false, true,  false, true)]
    [InlineData(ReportToolPermission.CanPrint,  true,  true,  false, false)]
    [InlineData(ReportToolPermission.CanPrint,  false, false, true,  true)]
    public async Task 批次版與逐支版_同答案(
        ReportToolPermission declared, bool read, bool create, bool print, bool expected)
    {
        Grant("Tool", read: read, create: create, print: print);
        var tool = new FakeTool("Tool", declared);

        var batch = await Batch(UserContext(), tool).GetAccessAsync();

        // 逐支版（Phase 0-B 上線的那條路徑）用真的 PermissionService 走同一個 DB。
        var perm = new PermissionService(_context, UserContext(), Mock.Of<ISecurityContext>());
        var single = await new ReportToolCatalog([tool], perm).GetAllWithAccessAsync();

        Assert.Equal(expected, batch.Single().IsAuthorized);
        Assert.Equal(single.Single().IsAuthorized, batch.Single().IsAuthorized);
    }

    [Fact]
    public async Task 沒有MenuPermission列_不放行()
    {
        var batch = await Batch(UserContext(), new FakeTool("Tool", ReportToolPermission.CanRead))
            .GetAccessAsync();

        Assert.False(batch.Single().IsAuthorized);
    }

    [Fact]
    public async Task 沒有作用中角色_全部不放行且不查DB()
    {
        // 即使 DB 裡有一列全開的授權，沒有 activeRoleId 就不該放行——
        // 與 PermissionService 每一支 CanXxx 的前兩行同語意。
        Grant("Tool", read: true, create: true, print: true);

        var batch = await Batch(UserContext(roleId: null),
            new FakeTool("Tool", ReportToolPermission.CanRead)).GetAccessAsync();

        Assert.False(batch.Single().IsAuthorized);
    }

    [Fact]
    public async Task 回傳涵蓋全部工具_未授權的也在清單裡()
    {
        // 呼叫端要能分辨「工具不存在」與「工具存在但沒權限」——後者是 §9.2 誠實拒答的依據。
        Grant("B", read: true);

        var access = await Batch(UserContext(),
            new FakeTool("A", ReportToolPermission.CanRead),
            new FakeTool("B", ReportToolPermission.CanRead)).GetAccessAsync();

        Assert.Equal(["A", "B"], access.Select(a => a.Tool.ToolKey));
        Assert.False(access[0].IsAuthorized);
        Assert.True(access[1].IsAuthorized);
    }

    [Fact]
    public void 不得依賴IPermissionService_否則就是退回逐支查詢()
    {
        // G1 的守門：批次化的重點是「不逐支呼叫 IPermissionService」。
        // 有人改回注入它，就是把 N 次 DB 往返加回來了，而那不會有任何測試失敗——
        // 除非在這裡把它釘住。
        var deps = typeof(ReportToolAccessBatch)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType);

        Assert.DoesNotContain(typeof(IPermissionService), deps);
    }
}
