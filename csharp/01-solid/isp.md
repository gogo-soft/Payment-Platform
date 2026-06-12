# ISP — Interface Segregation Principle

## Real Scenario

A payment platform deals with 30+ external channels. Not all channels support the same operations:

| Channel | Collection (收款) | Disbursement (代付) | Status Query | Balance Check |
|---------|:---:|:---:|:---:|:---:|
| PhonePe | ✓ | ✗ | ✓ | ✓ |
| Paytm | ✓ | ✓ | ✓ | ✗ |
| FreeCharge | ✓ | ✗ | ✗ | ✗ |
| Airtel | ✓ | ✓ | ✓ | ✓ |
| ... | | | | |

**The naive approach** — one fat interface:

```csharp
// ❌ Every channel MUST implement EVERY method, even unsupported ones
public interface IPaymentChannel
{
    Task<OrderResult> CollectAsync(OrderRequest request);          // All
    Task<OrderResult> DisburseAsync(DisburseRequest request);      // Most
    Task<OrderStatus> QueryStatusAsync(string orderId);            // Most
    Task<decimal> GetBalanceAsync();                               // Few
}
```

FreeCharge (collection-only) is forced to implement `DisburseAsync()` with `throw new NotSupportedException()`. That's a runtime time bomb — callers don't know which methods are safe until they crash.

## The Pattern

Split the fat interface into **role-based interfaces**. Clients depend only on what they need.

```csharp
// ✅ Segregated interfaces: each role is explicit
public interface ICollectionChannel
{
    Task<OrderResult> CollectAsync(OrderRequest request);
}

public interface IDisbursementChannel
{
    Task<OrderResult> DisburseAsync(DisburseRequest request);
}

public interface IStatusQueryChannel
{
    Task<OrderStatus> QueryStatusAsync(string orderId);
}

public interface IBalanceChannel
{
    Task<decimal> GetBalanceAsync();
}

// ✅ Channels implement ONLY what they support
public class FreeChargeChannel : ICollectionChannel  // That's it. Clean.
{
    public async Task<OrderResult> CollectAsync(OrderRequest request) { ... }
}

public class AirtelChannel : ICollectionChannel, IDisbursementChannel, IStatusQueryChannel, IBalanceChannel
{
    // All four — but explicitly. Callers know what they're getting.
}

// ✅ Consumer: depends only on what it needs
public class OrderStatusPoller
{
    private readonly IStatusQueryChannel _channel;  // Only needs status query

    public OrderStatusPoller(IStatusQueryChannel channel) => _channel = channel;

    public async Task PollAsync(string orderId)
    {
        var status = await _channel.QueryStatusAsync(orderId);
        // ...
    }
}

// ✅ DI registration: explicit about capabilities
services.AddSingleton<ICollectionChannel, FreeChargeChannel>(sp =>
    sp.GetRequiredService<FreeChargeChannel>());
// FreeChargeChannel is NOT registered as IDisbursementChannel — can't even inject it
```

## How the Real System Detects Capabilities

```csharp
public class ChannelCapabilityService
{
    private readonly IServiceProvider _sp;

    // Returns only channels that support a given operation
    public IEnumerable<T> GetChannelsSupporting<T>() where T : class
        => _sp.GetServices<T>();

    public bool SupportsDisbursement(string channelCode)
    {
        var channel = _sp.GetKeyedService<IPaymentChannel>(channelCode);
        return channel is IDisbursementChannel;
    }
}

// Usage: routing engine picks only eligible channels
var eligibleChannels = _capabilities
    .GetChannelsSupporting<IDisbursementChannel>()
    .ToList();
```

## Trade-offs

| Gain | Cost |
|------|------|
| Compile-time safety: can't call unsupported method | More interfaces to register in DI |
| Mocking in tests is trivial (one-method interfaces) | Need a capability registry for runtime discovery |
| New channel: implement only what you need | Code navigation: small interfaces scattered across files |

## Key Takeaway

"No client should be forced to depend on methods it does not use." In the real system, we had a bug where `throw new NotSupportedException()` in FreeCharge's unused `DisburseAsync` was accidentally triggered by a batch job. ISP would have prevented that at **compile time** — the batch job couldn't even inject `IDisbursementChannel` for FreeCharge.
