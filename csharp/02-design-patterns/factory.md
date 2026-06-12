# Factory Pattern

## Real Scenario

A payment platform supports 30+ external payment channels. Each channel has its own:
- API endpoint
- Authentication method
- Request format
- Response parser
- Error codes

When a merchant places an order with `gateway=phonepe`, the system must instantiate the correct channel handler. Hard-coding 30 `if (gateway == "phonepe") new PhonePeChannel()` statements is unmaintainable.

## The Pattern

Centralize object creation. The factory encapsulates the mapping from `gateway` string → channel instance.

```csharp
// Product interface
public interface IPaymentChannel
{
    string ChannelCode { get; }
    Task<ChannelOrderResult> SubmitOrderAsync(OrderRequest request);
    Task<ChannelStatusResult> QueryStatusAsync(string channelOrderId);
}

// Concrete products
public class PhonePeChannel : IPaymentChannel
{
    public string ChannelCode => "phonepe";
    private readonly HttpClient _http;
    private readonly PhonePeConfig _config;

    public PhonePeChannel(HttpClient http, IOptions<PhonePeConfig> config)
    {
        _http = http;
        _config = config.Value;
    }

    public async Task<ChannelOrderResult> SubmitOrderAsync(OrderRequest request) { ... }
    public async Task<ChannelStatusResult> QueryStatusAsync(string channelOrderId) { ... }
}

public class PaytmChannel : IPaymentChannel
{
    public string ChannelCode => "paytm";
    // Different config, different HTTP client, different response format
}

// Factory: the single place that knows how to create each channel
public class PaymentChannelFactory
{
    private readonly IServiceProvider _sp;
    private static readonly Dictionary<string, Type> _channelTypes = new()
    {
        ["phonepe"]    = typeof(PhonePeChannel),
        ["paytm"]      = typeof(PaytmChannel),
        ["freecharge"] = typeof(FreeChargeChannel),
        ["airtel"]     = typeof(AirtelChannel),
        ["mobi"]       = typeof(MobiChannel),
        ["induspay"]   = typeof(IndusPayChannel),
        // ... 25 more channels
    };

    public PaymentChannelFactory(IServiceProvider sp) => _sp = sp;

    public IPaymentChannel Create(string channelCode)
    {
        if (!_channelTypes.TryGetValue(channelCode, out var type))
            throw new ArgumentException($"Unknown channel: {channelCode}");

        return (IPaymentChannel)_sp.GetRequiredService(type);
    }

    public IEnumerable<string> GetSupportedChannels() => _channelTypes.Keys;

    // New channel? Register once:
    public static void RegisterChannel(string code, Type channelType)
        => _channelTypes[code] = channelType;
}

// Usage in order routing
public class OrderRoutingService
{
    private readonly PaymentChannelFactory _factory;

    public async Task<RouteResult> RouteAsync(OrderRequest request)
    {
        var channel = _factory.Create(request.Gateway);
        var result = await channel.SubmitOrderAsync(request);
        return new RouteResult(request.OrderId, result.ChannelOrderId, result.Status);
    }
}

// Hot-swap channels at runtime via config
public class ChannelConfigWatcher : BackgroundService
{
    private readonly PaymentChannelFactory _factory;
    private readonly IConfiguration _config;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var disabledChannels = _config.GetSection("Channels:Disabled").Get<string[]>() ?? [];
            var allChannels = _factory.GetSupportedChannels();

            foreach (var channel in allChannels)
            {
                var isActive = !disabledChannels.Contains(channel);
                // Update channel status in Redis for routing engine
                await SetChannelStatus(channel, isActive);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}
```

## Abstract Factory Extension

When a channel needs a **family** of related objects (HTTP client + signer + config), use Abstract Factory:

```csharp
public interface IChannelComponentFactory
{
    HttpClient CreateHttpClient();
    ISigningStrategy CreateSigner();
    IResponseParser CreateParser();
}

public class PhonePeComponentFactory : IChannelComponentFactory
{
    public HttpClient CreateHttpClient() => new() { BaseAddress = new("https://api.phonepe.com") };
    public ISigningStrategy CreateSigner() => new RsaSignatureStrategy(HashAlgorithmName.SHA256);
    public IResponseParser CreateParser() => new PhonePeResponseParser();
}
```

## Trade-offs

| Gain | Cost |
|------|------|
| Channel creation logic centralized in ONE place | Factory must know about all channel types (but that's its job) |
| New channel = new class + one registration | Abstract Factory adds complexity for simple channels |
| Hot-swap: disable channels without deployment | Factory becomes a dependency magnet |

## Key Takeaway

In the real system, the channel factory is a ~50-line registry. Adding a new channel (e.g., NaviPay) means: (1) implement `IPaymentChannel`, (2) add one entry to the factory dictionary. The routing engine, order service, and status poller — all untouched. Factory Pattern at work.
