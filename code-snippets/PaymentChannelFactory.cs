// code-snippets/PaymentChannelFactory.cs
// Factory Pattern + ISP: Multi-channel payment gateway
// Extracted from a production payment platform handling 30+ channels

namespace PaymentPlatform.Patterns;

// === Segregated Channel Interfaces (ISP) ===
public interface ICollectionChannel
{
    string ChannelCode { get; }
    Task<ChannelOrderResult> CollectAsync(OrderRequest request);
}

public interface IDisbursementChannel
{
    Task<ChannelOrderResult> DisburseAsync(DisburseRequest request);
}

public interface IStatusQueryChannel
{
    Task<ChannelStatusResult> QueryStatusAsync(string channelOrderId);
}

public interface IBalanceChannel
{
    Task<decimal> GetBalanceAsync();
}

// === Request/Response Types ===
public record OrderRequest(
    string OrderId, decimal Amount, string CustomerAccount, string CallbackUrl);

public record DisburseRequest(
    string OrderId, decimal Amount, string BeneficiaryAccount, string IfscCode);

public record ChannelOrderResult(string ChannelOrderId, string Status, string? QrCode);

public record ChannelStatusResult(string Status, string? Utr, string? ErrorCode);

// === Concrete Channels ===

public class PhonePeChannel : ICollectionChannel, IStatusQueryChannel, IBalanceChannel
{
    public string ChannelCode => "phonepe";
    private readonly HttpClient _http;
    private readonly ISigningStrategy _signer;
    private readonly string _secret;

    public PhonePeChannel(HttpClient http, ISigningStrategy signer, string secret)
    {
        _http = http;
        _signer = signer;
        _secret = secret;
    }

    public async Task<ChannelOrderResult> CollectAsync(OrderRequest request)
    {
        var parameters = new Dictionary<string, object>
        {
            ["merchantId"] = "OSP001",
            ["merchantTransactionId"] = request.OrderId,
            ["amount"] = ((int)(request.Amount * 100)).ToString(), // Paise
            ["callbackUrl"] = request.CallbackUrl,
        };
        parameters["sign"] = _signer.Sign(parameters, _secret);

        var response = await _http.PostAsync("/v3/order",
            new FormUrlEncodedContent(parameters.ToDictionary(
                kv => kv.Key, kv => kv.Value?.ToString() ?? "")));

        // Parse response...
        return new ChannelOrderResult(request.OrderId, "PENDING", null);
    }

    public async Task<ChannelStatusResult> QueryStatusAsync(string channelOrderId)
        => new("SUCCESS", "UTR123456", null);

    public async Task<decimal> GetBalanceAsync() => 100_000m;
}

public class FreeChargeChannel : ICollectionChannel  // Collection ONLY — clean ISP
{
    public string ChannelCode => "freecharge";
    private readonly HttpClient _http;
    private readonly ISigningStrategy _signer;
    private readonly string _secret;

    public FreeChargeChannel(HttpClient http, ISigningStrategy signer, string secret)
    {
        _http = http;
        _signer = signer;
        _secret = secret;
    }

    public async Task<ChannelOrderResult> CollectAsync(OrderRequest request)
    {
        var parameters = new Dictionary<string, object>
        {
            ["merchantId"] = "OSP001",
            ["orderId"] = request.OrderId,
            ["amount"] = request.Amount.ToString("F2"),
        };
        parameters["sign"] = _signer.Sign(parameters, _secret);

        var response = await _http.PostAsync("/api/order",
            new FormUrlEncodedContent(parameters.ToDictionary(
                kv => kv.Key, kv => kv.Value?.ToString() ?? "")));

        return new ChannelOrderResult(request.OrderId, "PENDING", null);
    }
}

// === Channel Factory ===
public class PaymentChannelFactory
{
    private readonly IServiceProvider _sp;
    private static readonly Dictionary<string, Type> ChannelTypes = new()
    {
        ["phonepe"]    = typeof(PhonePeChannel),
        ["freecharge"] = typeof(FreeChargeChannel),
        ["paytm"]      = typeof(PaytmChannel),
        ["airtel"]     = typeof(AirtelChannel),
        // ... 26 more channels
    };

    public PaymentChannelFactory(IServiceProvider sp) => _sp = sp;

    public ICollectionChannel CreateCollectionChannel(string channelCode)
    {
        var channel = Resolve(channelCode);
        return channel as ICollectionChannel
            ?? throw new NotSupportedException($"Channel '{channelCode}' does not support collection");
    }

    public T? GetChannelAs<T>(string channelCode) where T : class
    {
        var channel = Resolve(channelCode);
        return channel as T;
    }

    public IEnumerable<string> GetSupportedChannels() => ChannelTypes.Keys;

    private object Resolve(string channelCode)
    {
        if (!ChannelTypes.TryGetValue(channelCode, out var type))
            throw new ArgumentException($"Unknown channel: {channelCode}");
        return _sp.GetRequiredService(type);
    }

    public static void Register(string code, Type type) => ChannelTypes[code] = type;
}

// === Channel Capability Service ===
public class ChannelCapabilityService
{
    private readonly PaymentChannelFactory _factory;

    public ChannelCapabilityService(PaymentChannelFactory factory) => _factory = factory;

    public bool SupportsDisbursement(string channelCode)
        => _factory.GetChannelAs<IDisbursementChannel>(channelCode) is not null;

    public bool SupportsStatusQuery(string channelCode)
        => _factory.GetChannelAs<IStatusQueryChannel>(channelCode) is not null;

    public bool SupportsBalanceCheck(string channelCode)
        => _factory.GetChannelAs<IBalanceChannel>(channelCode) is not null;
}

// === Order Routing Service (uses factory) ===
public class OrderRoutingService
{
    private readonly PaymentChannelFactory _factory;
    private readonly ChannelCapabilityService _capabilities;

    public OrderRoutingService(PaymentChannelFactory factory, ChannelCapabilityService capabilities)
    {
        _factory = factory;
        _capabilities = capabilities;
    }

    public async Task<ChannelOrderResult> RouteCollectionAsync(OrderRequest request, string gateway)
    {
        if (!_capabilities.SupportsCollection(gateway))
            throw new InvalidOperationException($"Channel '{gateway}' does not support collection");

        var channel = _factory.CreateCollectionChannel(gateway);
        return await channel.CollectAsync(request);
    }
}

// Placeholder types for compilation
public class PaytmChannel : ICollectionChannel, IDisbursementChannel
{
    public string ChannelCode => "paytm";
    public Task<ChannelOrderResult> CollectAsync(OrderRequest request) => Task.FromResult(new ChannelOrderResult("", "PENDING", null));
    public Task<ChannelOrderResult> DisburseAsync(DisburseRequest request) => Task.FromResult(new ChannelOrderResult("", "PENDING", null));
}

public class AirtelChannel : ICollectionChannel, IDisbursementChannel, IStatusQueryChannel
{
    public string ChannelCode => "airtel";
    public Task<ChannelOrderResult> CollectAsync(OrderRequest request) => Task.FromResult(new ChannelOrderResult("", "PENDING", null));
    public Task<ChannelOrderResult> DisburseAsync(DisburseRequest request) => Task.FromResult(new ChannelOrderResult("", "PENDING", null));
    public Task<ChannelStatusResult> QueryStatusAsync(string id) => Task.FromResult(new ChannelStatusResult("SUCCESS", "UTR", null));
}

public static class ChannelCapabilityServiceExtensions
{
    public static bool SupportsCollection(this ChannelCapabilityService svc, string code)
        => svc.GetType().GetMethod("SupportsDisbursement") != null; // Simplified
}
