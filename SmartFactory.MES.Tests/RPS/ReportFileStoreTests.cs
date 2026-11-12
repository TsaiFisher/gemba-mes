using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 報表檔落地（§7.5）。
///
/// <para>最重要的一條是 <see cref="解析路徑時不得跳出儲存根目錄"/>：
/// <c>ReportRun.FilePath</c> 是資料庫內容，而下載端點會拿它去讀檔。
/// 少了根目錄檢查，一筆被竄改成 <c>../../appsettings.json</c> 的紀錄
/// 就能讓下載端點讀出連線字串。</para>
/// </summary>
public class ReportFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ReportFileStore _store;

    public ReportFileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rps-filestore-" + Guid.NewGuid().ToString("N"));
        _store = new ReportFileStore(Config(_root));
    }

    private static IConfiguration Config(string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Reports:StoragePath"] = path })
            .Build();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 存檔後讀得回同樣內容()
    {
        var content = new byte[] { 1, 2, 3, 4 };

        var relative = await _store.SaveAsync(content, ".xlsx");
        var read = await _store.ReadAsync(relative);

        Assert.Equal(content, read);
    }

    [Fact]
    public async Task 檔名不可猜測且依日期分目錄()
    {
        var relative = await _store.SaveAsync([1], ".xlsx");

        // 形如 20260812/{32 位 hex}.xlsx
        var parts = relative.Split('/');
        Assert.Equal(2, parts.Length);
        Assert.Equal(8, parts[0].Length);
        Assert.EndsWith(".xlsx", parts[1]);
        Assert.Equal(32 + ".xlsx".Length, parts[1].Length);
    }

    [Fact]
    public async Task 兩次存檔不會撞名()
    {
        var a = await _store.SaveAsync([1], ".xlsx");
        var b = await _store.SaveAsync([2], ".xlsx");

        Assert.NotEqual(a, b);
        Assert.Equal(new byte[] { 1 }, await _store.ReadAsync(a));
        Assert.Equal(new byte[] { 2 }, await _store.ReadAsync(b));
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("../../etc/passwd")]
    [InlineData("20260812/../../../secrets.txt")]
    public void 解析路徑時不得跳出儲存根目錄(string malicious)
    {
        Assert.Null(_store.ResolveFullPath(malicious));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空路徑一律解析失敗(string? path)
    {
        Assert.Null(_store.ResolveFullPath(path!));
    }

    [Fact]
    public async Task 讀取不存在的檔回null而不是拋例外()
    {
        // 過了保留期被清掉是正常狀態，下載端點要能顯示「檔案已過期」而不是 500。
        Assert.Null(await _store.ReadAsync("20260101/deadbeef.xlsx"));
    }

    [Fact]
    public async Task 刪除後讀不到且重複刪除不拋例外()
    {
        var relative = await _store.SaveAsync([1], ".xlsx");

        _store.Delete(relative);
        Assert.Null(await _store.ReadAsync(relative));

        // 保留期清理會重跑，第二次刪同一筆必須是安全的。
        _store.Delete(relative);
    }

    [Fact]
    public async Task 惡意副檔名不得帶出路徑分隔字元()
    {
        var relative = await _store.SaveAsync([1], "../../evil");

        Assert.EndsWith(".bin", relative);
        Assert.NotNull(_store.ResolveFullPath(relative));
    }

    [Fact]
    public async Task 沒有前導點的副檔名會自動補上()
    {
        var relative = await _store.SaveAsync([1], "csv");

        Assert.EndsWith(".csv", relative);
    }
}
