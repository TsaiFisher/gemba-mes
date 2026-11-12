using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace SmartFactory.MES.Modules.QA.Data;

/// <summary>
/// 設計期工廠，供 <c>dotnet ef migrations add … --context QaDbContext</c> 使用。
///
/// 需要獨立一支的原因：<see cref="QaDbContext"/> 只在 <c>Features:QaAssistant:Enabled</c> 為真時
/// 才會註冊進 DI，而 <c>dotnet ef</c> 是在**不啟動應用程式**的情況下建立上下文的。
/// 沒有這支工廠，功能關閉的環境連 migration 都產不出來。
///
/// <para><b>這裡的 <c>MigrationsHistoryTable</c> 必須與執行期註冊完全一致。</b>
/// 兩邊寫不同名字的話：migration 生得出來、應用程式也啟動得起來，
/// 但 <c>Migrate()</c> 會在另一張歷史表裡找不到紀錄，於是**每次啟動都重跑一次全部 migration**，
/// 第二次就撞 42P07（表已存在）。</para>
/// </summary>
public class QaDbContextDesignTimeFactory : IDesignTimeDbContextFactory<QaDbContext>
{
    public QaDbContext CreateDbContext(string[] args)
    {
        // 與 Data/DesignTimeDbContextFactory 同樣的取法：只讀 appsettings.json 的明文連線字串
        // （Development 覆寫可能為空字串，且設計期不經過 Security DLL 解密）。
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var connectionString = configuration["Features:QaAssistant:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "找不到有效的連線字串。請確認 appsettings.json 有 DefaultConnection（僅供 dotnet ef 使用）。");

        var options = new DbContextOptionsBuilder<QaDbContext>()
            .UseNpgsql(connectionString,
                o => o.MigrationsHistoryTable(QaDbContext.MigrationsHistoryTable))
            .Options;

        return new QaDbContext(options);
    }
}
