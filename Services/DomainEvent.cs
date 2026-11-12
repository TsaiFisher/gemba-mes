namespace SmartFactory.MES.Services
{
    /// <summary>
    /// 領域事件基類（mfg-domain.md §6 與 §10.5 非同步協作模式）。
    /// 用於解耦業務邏輯與副作用處理，避免同步交易阻塞。
    /// </summary>
    public abstract class DomainEvent
    {
        public string EventId { get; } = Guid.NewGuid().ToString("N");
        public DateTime OccurredAt { get; } = DateTime.UtcNow;
    }

    /// <summary>工單完成事件（mfg-domain.md §4.2）。</summary>
    public class WorkOrderCompletedEvent : DomainEvent
    {
        public string WorkOrderId { get; set; } = string.Empty;
        public string? ProductCode { get; set; }
        public string? PlantId { get; set; }
        public decimal StandardLaborCost { get; set; }
        public decimal StandardOverheadCost { get; set; }

        public WorkOrderCompletedEvent() { }

        public WorkOrderCompletedEvent(
            string workOrderId, string? productCode, string? plantId,
            decimal standardLaborCost, decimal standardOverheadCost)
        {
            WorkOrderId = workOrderId;
            ProductCode = productCode;
            PlantId = plantId;
            StandardLaborCost = standardLaborCost;
            StandardOverheadCost = standardOverheadCost;
        }
    }

    /// <summary>領域事件發佈/訂閱服務介面。</summary>
    public interface IDomainEventPublisher
    {
        void Publish<TEvent>(TEvent @event) where TEvent : DomainEvent;
    }

    /// <summary>領域事件處理器介面。</summary>
    public interface IDomainEventHandler<TEvent> where TEvent : DomainEvent
    {
        Task HandleAsync(TEvent @event, CancellationToken ct = default);
    }
}
