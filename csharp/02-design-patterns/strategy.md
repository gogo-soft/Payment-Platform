# Strategy Pattern

## Real Scenario

A payment platform integrates with 30+ payment channels. Each channel signs requests differently — HMAC-SHA256, RSA-SHA1, RSA-SHA256, MD5, with different encodings (hex, base64, uppercase).

The original 200-line `sign.cs` grew to 600 lines as channels were added. Each addition risked breaking existing channels. This is the classic Strategy Pattern use case: **a family of algorithms, encapsulate each one, make them interchangeable.**

> ⚠️ This is the same example from [OCP](../01-solid/ocp.md). That's intentional — **OCP is the goal; Strategy is the mechanism to achieve it.**

## The Pattern

```csharp
// Strategy interface
public interface ISigningStrategy
{
    string Sign(Dictionary<string, object> parameters, string secret);
    bool Verify(Dictionary<string, object> parameters, string signature, string secret);
}

// Concrete strategies
public class HmacSha256Strategy : ISigningStrategy
{
    private readonly HmacOutputFormat _format;

    public HmacSha256Strategy(HmacOutputFormat format = HmacOutputFormat.Base64)
        => _format = format;

    public string Sign(Dictionary<string, object> parameters, string secret)
    {
        var canonical = BuildCanonicalString(parameters);
        var hmac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(canonical));
        return _format switch
        {
            HmacOutputFormat.Hex => Convert.ToHexString(hmac),
            HmacOutputFormat.HexUpper => Convert.ToHexString(hmac).ToUpperInvariant(),
            HmacOutputFormat.Base64 => Convert.ToBase64String(hmac),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public bool Verify(Dictionary<string, object> parameters, string signature, string secret)
    {
        var expected = Sign(parameters, secret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    private static string BuildCanonicalString(Dictionary<string, object> parameters)
        => string.Join("&", parameters
            .Where(kv => kv.Key != "sign" && kv.Value != null)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
}

public class RsaSignatureStrategy : ISigningStrategy
{
    private readonly HashAlgorithmName _hashAlgorithm;

    public RsaSignatureStrategy(HashAlgorithmName? hashAlgorithm = null)
        => _hashAlgorithm = hashAlgorithm ?? HashAlgorithmName.SHA256;

    public string Sign(Dictionary<string, object> parameters, string secret)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(secret);
        var canonical = BuildCanonicalString(parameters);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(canonical),
            _hashAlgorithm,
            RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    public bool Verify(Dictionary<string, object> parameters, string signature, string secret)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(secret);
        var canonical = BuildCanonicalString(parameters);
        return rsa.VerifyData(
            Encoding.UTF8.GetBytes(canonical),
            Convert.FromBase64String(signature),
            _hashAlgorithm,
            RSASignaturePadding.Pkcs1);
    }

    private static string BuildCanonicalString(Dictionary<string, object> parameters)
        => string.Join("&", parameters
            .Where(kv => kv.Key != "sign")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={HttpUtility.UrlEncode(kv.Value?.ToString() ?? "")}"));
}

// Context: uses the strategy
public class PaymentGatewayClient
{
    private readonly ISigningStrategy _signer;
    private readonly HttpClient _http;
    private readonly string _secret;

    public PaymentGatewayClient(
        ISigningStrategy signer, HttpClient http, string secret)
    {
        _signer = signer;
        _http = http;
        _secret = secret;
    }

    public async Task<HttpResponseMessage> SendAsync(
        string endpoint, Dictionary<string, object> parameters)
    {
        parameters["sign"] = _signer.Sign(parameters, _secret);
        var content = new FormUrlEncodedContent(
            parameters.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""));
        return await _http.PostAsync(endpoint, content);
    }
}

// Factory: selects the strategy at runtime
public class SigningStrategyFactory
{
    private static readonly Dictionary<string, ISigningStrategy> _registry = new()
    {
        ["phonepe"]    = new RsaSignatureStrategy(HashAlgorithmName.SHA256),
        ["paytm"]      = new HmacSha256Strategy(HmacOutputFormat.Hex),
        ["freecharge"] = new Md5Strategy(),
        ["mobi"]       = new RsaSignatureStrategy(HashAlgorithmName.SHA1),
        ["airtel"]     = new HmacSha256Strategy(HmacOutputFormat.Base64),
    };

    public static ISigningStrategy GetFor(string channelCode)
        => _registry.TryGetValue(channelCode, out var s)
            ? s : throw new NotSupportedException($"No signing strategy for '{channelCode}'");

    // New channel? One line:
    public static void Register(string channelCode, ISigningStrategy strategy)
        => _registry[channelCode] = strategy;
}
```

## When to Use Strategy (vs simpler alternatives)

| Scenario | Use |
|----------|-----|
| 2-3 algorithms, unlikely to grow | Simple `switch` — don't over-engineer |
| 5+ algorithms, actively adding new ones | Strategy Pattern |
| Algorithms share 90% of their logic | Template Method + Strategy |
| Algorithms selected from config at startup | Strategy + factory (shown above) |

## Key Takeaway

In the real system, `sign.cs` has supported 30+ channels for 18 months. Adding a new channel means: (1) implement `ISigningStrategy`, (2) register it. **Zero existing tests break.** That's the Strategy Pattern delivering on OCP.
