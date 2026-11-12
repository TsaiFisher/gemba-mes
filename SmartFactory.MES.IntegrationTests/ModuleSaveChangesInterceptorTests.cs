using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 部署專案的 SaveChanges 攔截器擴充點：模組只要在 DI 註冊 <see cref="IModuleSaveChangesInterceptor"/>，
    /// Core 建立 DbContext 時就會掛上。驗的是 Program.cs 的實際接線（不經 Program 就驗不到）。
    /// </summary>
    [Collection("Integration")]
    public class ModuleSaveChangesInterceptorTests
    {
        private readonly TestWebApplicationFactory _factory;

        public ModuleSaveChangesInterceptorTests(TestWebApplicationFactory factory) => _factory = factory;

        private sealed class Probe : SaveChangesInterceptor, IModuleSaveChangesInterceptor
        {
            public int Calls;

            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                Calls++;
                return ValueTask.FromResult(result);
            }
        }

        [Fact]
        public async Task 模組註冊的攔截器_存檔時會被呼叫()
        {
            var probe = new Probe();
            using var app = _factory.WithWebHostBuilder(b =>
                b.ConfigureServices(s => s.AddScoped<IModuleSaveChangesInterceptor>(_ => probe)));

            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // 部署專案啟動時可能寫種子資料（ProjectZ 會），那些存檔同樣經過探針——只計本次存檔
            var before = probe.Calls;
            await db.SaveChangesAsync();

            Assert.Equal(before + 1, probe.Calls);
        }

        [Fact]
        public async Task 沒有模組註冊攔截器_DbContext照常可用()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            Assert.Equal(0, await db.SaveChangesAsync());
        }
    }
}
