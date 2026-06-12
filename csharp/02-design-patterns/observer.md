# Observer Pattern (Pub/Sub Event System)

## Real Scenario

When an order completes on a payment platform, multiple systems need to know:

1. **Merchant notification** — send callback to merchant's `notify_url`
2. **Dashboard update** — push real-time status via WebSocket
3. **Reconciliation trigger** — schedule end-of-day settlement check
4. **Metrics pipeline** — log structured event for analytics
5. **Fraud detection** — check for anomalous patterns

Coupling `OrderCompletionService` directly to all 5 consumers creates a maintenance nightmare.

## The Pattern

Use a **pub/sub event bus**. The publisher fires an event. Subscribers register independently. Neither knows about the other.

```csharp
// Event: immutable, self-contained
public record OrderCompletedEvent(
    string OrderCode,
    string Utr,
    decimal Amount,
    long PartnerId,
    long MerchantId,
    DateTime CompletedAt);

// Publisher interface
public interface IEventBus
{
    Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : notnull;
}

// Subscriber interface
public interface IEventHandler<in T> where T : notnull
{
    Task HandleAsync(T @event, CancellationToken ct = default);
}

// Redis-backed implementation
public class RedisEventBus : IEventBus
{
    private readonly IConnectionMultiplexer _redis;

    public RedisEventBus(IConnectionMultiplexer redis) => _redis = redis;

    public async Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : notnull
    {
        var channel = typeof(T).Name;  // "OrderCompletedEvent"
        var payload = JsonSerializer.Serialize(@event);
        await _redis.GetSubscriber().PublishAsync(channel, payload);
    }
}

// Subscribers: each handles ONE concern

public class MerchantNotificationHandler : IEventHandler<OrderCompletedEvent>
{
    private readonly HttpClient _http;

    public async Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
    {
        var merchant = await GetMerchantCallbackUrl(e.MerchantId);
        var payload = new { order_code = e.OrderCode, utr = e.Utr, status = "completed" };
        await _http.PostAsJsonAsync(merchant, payload, ct);
    }
}

public class DashboardPushHandler : IEventHandler<OrderCompletedEvent>
{
    private readonly IWebSocketManager _ws;

    public async Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
        => await _ws.SendToMerchantAsync(e.MerchantId, new { e.OrderCode, e.Utr, status = "completed" });
}

public class MetricsHandler : IEventHandler<OrderCompletedEvent>
{
    private readonly ILogger<MetricsHandler> _logger;

    public Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
    {
        _logger.LogInformation(
            "[ORDER_COMPLETED] code={Code} utr={Utr} amount={Amount} partner={Partner} merchant={Merchant}",
            e.OrderCode, e.Utr, e.Amount, e.PartnerId, e.MerchantId);
        return Task.CompletedTask;
    }
}

// Background service: listens and dispatches
public class EventDispatcher : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _sp;
    private static readonly Dictionary<string, Type> _eventTypes = new()
    {
        [nameof(OrderCompletedEvent)] = typeof(OrderCompletedEvent),
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sub = _redis.GetSubscriber();
        foreach (var (channel, _) in _eventTypes)
        {
            await sub.SubscribeAsync(channel, async (_, message) =>
            {
                var eventType = _eventTypes[channel];
                var @event = JsonSerializer.Deserialize(message.ToString(), eventType)!;
                var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
                var handlers = _sp.GetServices(handlerType);

                foreach (var handler in handlers)
                {
                    var method = handlerType.GetMethod("HandleAsync")!;
                    await (Task)method.Invoke(handler, new[] { @event, ct })!;
                }
            });
        }

        await Task.Delay(Timeout.Infinite, ct);
    }
}

// Publisher: fires and forgets
public class OrderCompletionService
{
    private readonly IEventBus _bus;

    public async Task CompleteOrderAsync(string code, string utr)
    {
        // ... business logic ...

        // Fire event — zero knowledge of who listens
        await _bus.PublishAsync(new OrderCompletedEvent(
            code, utr, amount, partnerId, merchantId, DateTime.UtcNow));
    }
}

// DI registration
services.AddSingleton<IEventBus, RedisEventBus>();
services.AddSingleton<IEventHandler<OrderCompletedEvent>, MerchantNotificationHandler>();
services.AddSingleton<IEventHandler<OrderCompletedEvent>, DashboardPushHandler>();
services.AddSingleton<IEventHandler<OrderCompletedEvent>, MetricsHandler>();
services.AddHostedService<EventDispatcher>();
```

## Adding a New Subscriber

```csharp
// 1. Create handler (new file, zero existing code touched)
public class FraudDetectionHandler : IEventHandler<OrderCompletedEvent>
{
    public async Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
    {
        if (e.Amount > 100_000 && DateTime.UtcNow.Hour < 6)
            await AlertAsync($"Large pre-dawn order: {e.OrderCode}");
    }
}

// 2. Register it (one line in DI)
services.AddSingleton<IEventHandler<OrderCompletedEvent>, FraudDetectionHandler>();
```

Publisher is **never touched**. Existing subscribers are **never touched**. That's the Observer Pattern.

## Trade-offs

| Gain | Cost |
|------|------|
| Add/remove subscribers without touching publisher | Eventual consistency: subscriber failures don't roll back publisher |
| Each subscriber testable in isolation | Debugging: can't trace from publisher to subscriber in one stack |
| Publisher complexity stays constant | Ordering across subscribers is not guaranteed |

## Key Takeaway

In the real system, `order_notify` events fan out to 5+ consumers. When we added WebSocket dashboard push, it was a **new file + one DI registration**. The order completion logic didn't change at all. That's Observer Pattern delivering on decoupling.
