using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 內存領域事件發佈器（簡單實現）。
    /// 事件發佈時，同步呼叫所有已註冊的處理器。
    /// 用於應用服務層與背景服務的解耦通信。
    /// </summary>
    public class InMemoryDomainEventPublisher : IDomainEventPublisher
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<InMemoryDomainEventPublisher> _logger;

        // 使用反射動態查找 IDomainEventHandler<TEvent> 實現
        private static readonly ConcurrentDictionary<Type, Type> HandlerTypeCache = new();

        public InMemoryDomainEventPublisher(
            IServiceProvider serviceProvider,
            ILogger<InMemoryDomainEventPublisher> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public void Publish<TEvent>(TEvent @event) where TEvent : DomainEvent
        {
            _ = PublishAsync(@event, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : DomainEvent
        {
            var eventType = @event.GetType();
            var handlerType = GetOrCacheHandlerType(eventType);

            if (handlerType == null)
            {
                _logger.LogInformation("未找到事件 {EventType} 的處理器", eventType.Name);
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var handler = scope.ServiceProvider.GetService(handlerType);

                if (handler == null)
                {
                    _logger.LogWarning("無法解析事件處理器 {HandlerType}", handlerType.Name);
                    return;
                }

                var handleMethod = handlerType.GetMethod(
                    nameof(IDomainEventHandler<DomainEvent>.HandleAsync),
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (handleMethod != null)
                {
                    var task = (Task?)handleMethod.Invoke(handler, new object[] { @event, ct });
                    if (task != null)
                    {
                        await task.ConfigureAwait(false);
                    }
                }

                _logger.LogDebug("事件 {EventType} ({EventId}) 已發佈至處理器", eventType.Name, @event.EventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "處理事件 {EventType} ({EventId}) 時發生錯誤", eventType.Name, @event.EventId);
                // 不拋例外，避免阻塞發佈者
            }
        }

        private Type? GetOrCacheHandlerType(Type eventType)
        {
            return HandlerTypeCache.GetOrAdd(eventType, et =>
            {
                var handlerGenericType = typeof(IDomainEventHandler<>).MakeGenericType(et);
                return handlerGenericType;
            });
        }
    }
}
