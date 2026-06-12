# DIP — Dependency Inversion Principle

## Real Scenario

A payment service needs to log, cache, and notify. The naive approach: instantiate dependencies directly.

```csharp
// ❌ High-level module depends on low-level concrete classes
public class OrderService
{
    private readonly SqlConnection _db = new("connection string...");
    private readonly FileLogger _logger = new("/var/log/orders.log");
    private readonly RedisCache _cache = new("localhost:6379");

    public async Task ProcessAsync()
    {
        _logger.Log("Processing...");
        var data = _cache.Get("key");
        // ...
    }
}
```

Problems:
1. Can't unit-test without a real database, file system, and Redis
2. Changing the logger (file → Elasticsearch) requires changing `OrderService`
3. Every service hard-codes connection strings

## The Pattern

**Depend on abstractions, not concretions.** High-level modules (business logic) and low-level modules (infrastructure) both depend on interfaces.

```csharp
// ✅ Both depend on abstractions
public interface ILogger
{
    void LogInformation(string message, params object[] args);
    void LogError(Exception ex, string message, params object[] args);
}

public interface IDistributedCache
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value, TimeSpan expiry);
    Task<bool> LockAsync(string key, TimeSpan timeout);
}

public interface IEventPublisher
{
    Task PublishAsync<T>(string channel, T message);
}

// High-level module: depends ONLY on abstractions
public class OrderCompletionService
{
    private readonly ILogger _logger;
    private readonly IDistributedCache _cache;
    private readonly IEventPublisher _events;
    private readonly IBalanceService _balance;

    // ✅ Dependencies injected — OrderService doesn't know or care
    //    what concrete implementations exist
    public OrderCompletionService(
        ILogger logger,
        IDistributedCache cache,
        IEventPublisher events,
        IBalanceService balance)
    {
        _logger = logger;
        _cache = cache;
        _events = events;
        _balance = balance;
    }

    public async Task<bool> CompleteOrderAsync(string code, string utr)
    {
        _logger.LogInformation("Completing order {Code} with UTR {Utr}", code, utr);

        // Use distributed lock to prevent concurrent processing
        var lockKey = $"order:lock:{code}";
        if (!await _cache.LockAsync(lockKey, TimeSpan.FromSeconds(10)))
        {
            _logger.LogInformation("Order {Code} is being processed by another instance", code);
            return false;
        }

        // Business logic here...

        // Publish event — don't care if it's Redis, Kafka, or RabbitMQ
        await _events.PublishAsync("order:completed", new { code, utr });

        return true;
    }
}

// Low-level modules implement the abstractions

public class RedisCache : IDistributedCache
{
    private readonly IConnectionMultiplexer _redis;
    public RedisCache(IOptions<RedisOptions> options)
        => _redis = ConnectionMultiplexer.Connect(options.Value.ConnectionString);

    public async Task<T?> GetAsync<T>(string key)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(key);
        return value.HasValue ? JsonSerializer.Deserialize<T>(value!) : default;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiry)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(key, JsonSerializer.Serialize(value), expiry);
    }

    public async Task<bool> LockAsync(string key, TimeSpan timeout)
    {
        var db = _redis.GetDatabase();
        return await db.LockTakeAsync(key, Environment.MachineName, timeout);
    }
}

public class RedisEventPublisher : IEventPublisher
{
    private readonly ISubscriber _sub;
    public RedisEventPublisher(IConnectionMultiplexer redis) => _sub = redis.GetSubscriber();

    public async Task PublishAsync<T>(string channel, T message)
        => await _sub.PublishAsync(channel, JsonSerializer.Serialize(message));
}

// ✅ DI registration: swap implementations without touching business logic
services.AddSingleton<IDistributedCache, RedisCache>();       // Production
// services.AddSingleton<IDistributedCache, MemoryCache>();   // Testing
services.AddSingleton<IEventPublisher, RedisEventPublisher>(); // Production
// services.AddSingleton<IEventPublisher, KafkaPublisher>();   // If we switch to Kafka
```

## The Unit Test Payoff

```csharp
[Fact]
public async Task CompleteOrder_Locked_ReturnsFalse()
{
    // ✅ Mock abstractions — no Redis, no DB, no filesystem
    var cache = new Mock<IDistributedCache>();
    cache.Setup(c => c.LockAsync("order:lock:TEST001", It.IsAny<TimeSpan>()))
         .ReturnsAsync(false);  // Simulate lock held by another instance

    var service = new OrderCompletionService(
        Mock.Of<ILogger>(),
        cache.Object,
        Mock.Of<IEventPublisher>(),
        Mock.Of<IBalanceService>());

    var result = await service.CompleteOrderAsync("TEST001", "UTR123");

    Assert.False(result);
    // Test runs in < 10ms with zero infrastructure
}
```

## Trade-offs

| Gain | Cost |
|------|------|
| Full unit testability without infrastructure | Interface explosion: every dependency needs an interface |
| Swap Redis → Kafka without touching business logic | Debugging: can't follow concrete implementation from call site |
| New dev can understand OrderService without knowing Redis internals | Over-abstraction risk: don't make `IStringFormatter` |

## Key Takeaway

In the real system, we switched from file-based logging to structured JSON logging (Elasticsearch) by changing **one line** in DI registration. `OrderService`, `BalanceService`, and 20+ other services were completely untouched. That's DIP paying its rent.
