# Service Layer Pattern

## The Problem

New developers on a payment platform often ask: "Where do I put my business logic?"

The wrong answers:
- **In the controller** → can't reuse across HTTP, WebSocket, and cron jobs
- **In stored procedures** → business logic trapped in the database
- **In static utility classes** → can't inject dependencies, can't test

## The Pattern

A **Service Layer** sits between the presentation (controllers, WebSocket handlers, cron jobs) and the data access layer. It contains pure business logic — no HTTP concerns, no SQL.

```csharp
// ❌ Controller with business logic
public class OrderController : ControllerBase
{
    [HttpPost("collection")]
    public async Task<IActionResult> CreateCollection([FromForm] OrderForm form)
    {
        // Business logic mixed with HTTP concerns
        var ip = HttpContext.Connection.RemoteIpAddress!.ToString();
        if (await _redis.ExistsAsync($"ratelimit:{ip}"))
            return StatusCode(429);

        var merchant = await _db.Merchants.FindAsync(form.MerchantId);
        if (merchant.Status != "active")
            return BadRequest("Merchant inactive");

        var channel = await _db.Channels.FirstOrDefaultAsync(c => c.Code == form.Gateway);
        if (channel == null || channel.Status != "active")
            return BadRequest("Channel unavailable");

        var fee = form.Amount * channel.Rate;
        var order = new Order { /* ... 20 lines of mapping ... */ };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await _redis.PublishAsync("order:created", JsonSerializer.Serialize(order));
        return Ok(order);
    }
}
```

This controller has at least 5 responsibilities. Can't test without HTTP. Can't reuse from cron jobs.

```csharp
// ✅ Service Layer: pure business logic, zero HTTP/DB knowledge
public class CollectionOrderService
{
    private readonly IMerchantRepository _merchants;
    private readonly IChannelRepository _channels;
    private readonly IOrderRepository _orders;
    private readonly IRateCalculator _rates;
    private readonly IEventBus _events;
    private readonly ILogger<CollectionOrderService> _logger;

    public CollectionOrderService(
        IMerchantRepository merchants,
        IChannelRepository channels,
        IOrderRepository orders,
        IRateCalculator rates,
        IEventBus events,
        ILogger<CollectionOrderService> logger)
    {
        _merchants = merchants;
        _channels = channels;
        _orders = orders;
        _rates = rates;
        _events = events;
        _logger = logger;
    }

    public async Task<CreateOrderResult> CreateAsync(CreateOrderCommand command)
    {
        // 1. Validate merchant
        var merchant = await _merchants.GetByIdAsync(command.MerchantId);
        if (merchant is null)
            return CreateOrderResult.Fail("Merchant not found");
        if (!merchant.IsActive)
            return CreateOrderResult.Fail("Merchant is inactive");

        // 2. Validate channel
        var channel = await _channels.GetByCodeAsync(command.Gateway);
        if (channel is null || !channel.IsActive)
            return CreateOrderResult.Fail("Channel unavailable");

        // 3. Calculate fees
        var feeCalculation = _rates.Calculate(command.Amount, channel.Rate);

        // 4. Create order domain object
        var order = Order.Create(
            command.MerchantId,
            command.Gateway,
            command.Amount,
            feeCalculation);

        // 5. Persist
        await _orders.AddAsync(order);

        // 6. Publish event
        await _events.PublishAsync(new OrderCreatedEvent(
            order.Code, order.Amount, order.MerchantId, order.Gateway));

        _logger.LogInformation("Order {Code} created: amount={Amount} gateway={Gateway}",
            order.Code, order.Amount, order.Gateway);

        return CreateOrderResult.Success(order);
    }
}

// ✅ Controller is now THIN — just wires HTTP ↔ Service
public class OrderController : ControllerBase
{
    private readonly CollectionOrderService _service;

    [HttpPost("collection")]
    public async Task<IActionResult> CreateCollection([FromForm] OrderForm form)
    {
        var command = new CreateOrderCommand(
            form.MerchantId, form.Gateway, form.Amount, form.OrderId);

        var result = await _service.CreateAsync(command);

        return result.IsSuccess
            ? Ok(new { code = 20000, data = result.Order })
            : BadRequest(new { code = 10014, message = result.Error });
    }
}

// ✅ Cron job reuses the SAME service
public class ExpiredOrderCleanupJob : BackgroundService
{
    private readonly CollectionOrderService _service;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Same business logic, no HTTP
            await _service.CancelExpiredOrdersAsync();
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}

// ✅ WebSocket handler reuses the SAME service
public class OrderWebSocketHandler
{
    private readonly CollectionOrderService _service;

    public async Task HandleMessageAsync(string message)
    {
        var cmd = JsonSerializer.Deserialize<CreateOrderCommand>(message)!;
        var result = await _service.CreateAsync(cmd);
        // Push result via WebSocket
    }
}

// ✅ Test: no HTTP, no database
[Fact]
public async Task CreateOrder_InactiveMerchant_ReturnsFail()
{
    var mockMerchants = new Mock<IMerchantRepository>();
    mockMerchants.Setup(m => m.GetByIdAsync(1))
        .ReturnsAsync(new Merchant { IsActive = false });

    var service = new CollectionOrderService(
        mockMerchants.Object, Mock.Of<IChannelRepository>(),
        Mock.Of<IOrderRepository>(), Mock.Of<IRateCalculator>(),
        Mock.Of<IEventBus>(), Mock.Of<ILogger<CollectionOrderService>>());

    var result = await service.CreateAsync(new CreateOrderCommand(1, "phonepe", 100, "ORD001"));

    Assert.False(result.IsSuccess);
    Assert.Equal("Merchant is inactive", result.Error);
}
```

## Service Layer Structure in the Real System

```
application/
├── services/
│   ├── balance_service.cs          # Fund movement (debit/credit/arrears)
│   ├── commission_service.cs       # Multi-tier commission distribution
│   ├── order_completion_service.cs # Order lifecycle orchestration
│   ├── reconciliation_service.cs   # Post-commit account reconciliation
│   ├── rate_service.cs             # Dynamic fee calculation
│   └── external_channel_service.cs # 30+ channel abstraction
├── pay/                            # HTTP handlers (controllers)
├── jobs/                           # Background cron jobs
└── websocket/                      # Real-time push handlers
```

All three consumers (pay/, jobs/, websocket/) call the same services.

## Trade-offs

| Gain | Cost |
|------|------|
| Business logic reusable across HTTP, cron, WebSocket | More files, more DI registrations |
| Full unit testability without infrastructure | Anemic controllers feel like unnecessary indirection |
| New consumer type: just call the service | Must enforce discipline: no business logic in controllers |

## Key Takeaway

In the real system, `BalanceService` is called by the HTTP API, cron jobs, admin panel, and merchant portal — **four different entry points, one business logic source**. When we fixed a rounding bug in balance calculation, we changed one file and all four entry points were fixed simultaneously.
