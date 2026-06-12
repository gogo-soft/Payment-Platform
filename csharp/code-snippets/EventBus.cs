// code-snippets/EventBus.cs
// Observer Pattern: Pub/Sub event system
// Extracted from a production payment platform

using System.Collections.Concurrent;
using System.Text.Json;

namespace PaymentPlatform.Patterns;

// === Event Types ===
public record OrderCompletedEvent(
    string OrderCode, string Utr, decimal Amount,
    long PartnerId, long MerchantId, DateTime CompletedAt);

public record OrderFailedEvent(
    string OrderCode, string Reason, DateTime FailedAt);

// === Event Bus ===
public interface IEventBus
{
    Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : notnull;
}

// === Event Handler ===
public interface IEventHandler<in T> where T : notnull
{
    Task HandleAsync(T @event, CancellationToken ct = default);
}

// === In-Memory Implementation (swap with Redis/Kafka in production) ===
public class InMemoryEventBus : IEventBus
{
    private readonly IServiceProvider _sp;

    public InMemoryEventBus(IServiceProvider sp) => _sp = sp;

    public async Task PublishAsync<T>(T @event, CancellationToken ct = default) where T : notnull
    {
        var handlerType = typeof(IEventHandler<>).MakeGenericType(typeof(T));
        var handlers = _sp.GetServices(handlerType);

        foreach (var handler in handlers)
        {
            var method = handlerType.GetMethod("HandleAsync")!;
            await (Task)method.Invoke(handler, new object[] { @event, ct })!;
        }
    }
}

// === Subscribers ===

public class MerchantNotificationHandler : IEventHandler<OrderCompletedEvent>
{
    private readonly HttpClient _http;
    private readonly ILogger<MerchantNotificationHandler> _logger;

    public MerchantNotificationHandler(HttpClient http, ILogger<MerchantNotificationHandler> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
    {
        var payload = new { e.OrderCode, e.Utr, status = "completed", e.Amount };
        _logger.LogInformation("Notifying merchant {MerchantId} for order {OrderCode}",
            e.MerchantId, e.OrderCode);
        // await _http.PostAsJsonAsync(merchantCallbackUrl, payload, ct);
        await Task.CompletedTask;
    }
}

public class DashboardPushHandler : IEventHandler<OrderCompletedEvent>
{
    public Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
    {
        // Push real-time status update via WebSocket
        Console.WriteLine($"[WS] Order {e.OrderCode} completed — pushing to merchant {e.MerchantId}");
        return Task.CompletedTask;
    }
}

public class MetricsHandler : IEventHandler<OrderCompletedEvent>
{
    private readonly ILogger<MetricsHandler> _logger;

    public MetricsHandler(ILogger<MetricsHandler> logger) => _logger = logger;

    public Task HandleAsync(OrderCompletedEvent e, CancellationToken ct)
    {
        _logger.LogInformation(
            "[ORDER_COMPLETED] code={Code} utr={Utr} amount={Amount} partner={Partner} merchant={Merchant}",
            e.OrderCode, e.Utr, e.Amount, e.PartnerId, e.MerchantId);
        return Task.CompletedTask;
    }
}

// === Publisher ===
public class OrderCompletionService
{
    private readonly IEventBus _bus;
    private readonly IBalanceService _balance;
    private readonly ILogger<OrderCompletionService> _logger;

    public OrderCompletionService(IEventBus bus, IBalanceService balance,
        ILogger<OrderCompletionService> logger)
    {
        _bus = bus;
        _balance = balance;
        _logger = logger;
    }

    public async Task<bool> CompleteAsync(string code, string utr, decimal amount,
        long partnerId, long merchantId)
    {
        // Business logic...

        // Fire event — no knowledge of subscribers
        await _bus.PublishAsync(new OrderCompletedEvent(
            code, utr, amount, partnerId, merchantId, DateTime.UtcNow));

        return true;
    }
}

// === DI Registration (in Program.cs) ===
//
// builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
// builder.Services.AddSingleton<IEventHandler<OrderCompletedEvent>, MerchantNotificationHandler>();
// builder.Services.AddSingleton<IEventHandler<OrderCompletedEvent>, DashboardPushHandler>();
// builder.Services.AddSingleton<IEventHandler<OrderCompletedEvent>, MetricsHandler>();

// === Test ===
public static class EventBusDemo
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<IEventHandler<OrderCompletedEvent>, MetricsHandler>();
        var sp = services.BuildServiceProvider();

        var bus = sp.GetRequiredService<IEventBus>();
        await bus.PublishAsync(new OrderCompletedEvent(
            "ORD001", "UTR123", 100.00m, 1, 2, DateTime.UtcNow));

        Console.WriteLine("Event published. Check logs for [ORDER_COMPLETED] metric.");
    }
}

// Placeholder usings
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
