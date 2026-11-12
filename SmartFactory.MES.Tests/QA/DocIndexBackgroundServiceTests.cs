using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="DocIndexBackgroundService"/> 的行為守門。
    ///
    /// 這裡真正要守的只有一件事，而它不是功能面的：
    /// <b>.NET 的 <c>BackgroundServiceExceptionBehavior</c> 預設是 <c>StopHost</c></b>——
    /// <c>ExecuteAsync</c> 逸出任何例外會讓**整個 ERP 關閉**。
    /// 手冊索引是選用功能的附屬品，它失敗絕不該有這種後果。
    /// </summary>
    public class DocIndexBackgroundServiceTests
    {
        private sealed class StubBuilder : IDocIndexBuilder
        {
            private readonly Func<DocIndexResult> _behaviour;
            private readonly Func<(int, int)>? _embedBehaviour;

            public int Calls { get; private set; }
            public int EmbedCalls { get; private set; }

            public StubBuilder(Func<DocIndexResult> behaviour, Func<(int, int)>? embedBehaviour = null)
            {
                _behaviour = behaviour;
                _embedBehaviour = embedBehaviour;
            }

            public Task<DocIndexResult> RebuildAsync(CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult(_behaviour());
            }

            public Task<(int Embedded, int Pending)> EmbedPendingAsync(CancellationToken ct = default)
            {
                EmbedCalls++;
                return Task.FromResult(_embedBehaviour?.Invoke() ?? (0, 0));
            }
        }

        private static DocIndexBackgroundService Build(
            IDocIndexBuilder builder, params (string Key, string Value)[] settings)
        {
            var services = new ServiceCollection();
            services.AddSingleton(builder);
            var provider = services.BuildServiceProvider();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
                .Build();

            return new DocIndexBackgroundService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<DocIndexBackgroundService>.Instance);
        }

        [Fact]
        public async Task 啟動時會重建索引()
        {
            var builder = new StubBuilder(() => new DocIndexResult(134, 134, 0, 563, 0, 13));
            var service = Build(builder);

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;   // .NET 10 起 ExecuteAsync 整段在背景執行緒，StartAsync 回來時還沒開始做事
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, builder.Calls);
        }

        [Fact]
        public async Task 重建失敗不得讓應用程式關閉()
        {
            // 最可能的成因：Features:QaAssistant:Enabled=true 但 migration 未套用，
            // DocChunks 資料表根本不存在。那時整個 ERP 不該跟著開不起來。
            var builder = new StubBuilder(() => throw new InvalidOperationException("relation \"DocChunks\" does not exist"));
            var service = Build(builder);

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;   // .NET 10 起 ExecuteAsync 整段在背景執行緒，StartAsync 回來時還沒開始做事
            await service.StopAsync(CancellationToken.None);

            // ExecuteTask 走完且未帶著例外——這正是 StopHost 不會被觸發的條件。
            Assert.Equal(1, builder.Calls);
            Assert.NotNull(service.ExecuteTask);
            Assert.True(service.ExecuteTask!.IsCompletedSuccessfully,
                "ExecuteAsync 帶著例外結束，.NET 會據此關閉整個 Host");
        }

        [Fact]
        public async Task 重建後會補嵌入()
        {
            var builder = new StubBuilder(() => new DocIndexResult(134, 1, 0, 5, 5, 0), () => (5, 0));
            var service = Build(builder);

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;   // .NET 10 起 ExecuteAsync 整段在背景執行緒，StartAsync 回來時還沒開始做事
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, builder.EmbedCalls);
        }

        [Fact]
        public async Task 嵌入失敗不得讓詞彙索引跟著失敗()
        {
            // 詞彙通道不需要任何模型。推論服務沒開時它必須照常運作——
            // 把嵌入失敗升級成整個索引失敗，等於讓選用相依決定必要功能的存亡。
            var builder = new StubBuilder(
                () => new DocIndexResult(134, 134, 0, 563, 0, 13),
                () => throw new OllamaException("無法連線至推論服務"));
            var service = Build(builder);

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;   // .NET 10 起 ExecuteAsync 整段在背景執行緒，StartAsync 回來時還沒開始做事
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, builder.Calls);          // 詞彙索引照樣建好
            Assert.Equal(1, builder.EmbedCalls);
            Assert.True(service.ExecuteTask!.IsCompletedSuccessfully,
                "ExecuteAsync 帶著例外結束，.NET 會據此關閉整個 Host");
        }

        [Fact]
        public async Task 開關關閉時不重建()
        {
            var builder = new StubBuilder(() => default);
            var service = Build(builder,
                (DocIndexBackgroundService.RebuildOnStartupKey, "false"));

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;   // .NET 10 起 ExecuteAsync 整段在背景執行緒，StartAsync 回來時還沒開始做事
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, builder.Calls);
        }

        [Fact]
        public async Task 未設定時預設會重建()
        {
            // 預設值弄反的話，症狀是「手冊更新了但 Q&A 還在講舊版」——
            // 沒有任何錯誤，只是答案過期。
            var builder = new StubBuilder(() => default);
            var service = Build(builder);

            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!;   // .NET 10 起 ExecuteAsync 整段在背景執行緒，StartAsync 回來時還沒開始做事
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, builder.Calls);
        }
    }
}
