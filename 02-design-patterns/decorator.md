# Decorator Pattern

## Real Scenario

Some payment channels require **field-level encryption** on every request and response. The encryption logic is cross-cutting — it's not part of the channel's business logic, but it must wrap every HTTP call.

For example, MahaBank requires:
- Request body fields `data1`, `data2`, `uniquerequestId` encrypted with a derived key
- Response body field `data` decrypted before parsing

Adding encryption directly into `MahaChannel.SubmitOrderAsync()` mixes concerns and makes testing hard.

## The Pattern

Wrap the HTTP client with a decorator that transparently encrypts/decrypts. The channel code stays clean.

```csharp
// Core abstraction
public interface IHttpClient
{
    Task<HttpResponse> SendAsync(HttpRequest request);
}

// Concrete: raw HTTP client
public class StandardHttpClient : IHttpClient
{
    private readonly HttpClient _http;

    public StandardHttpClient(HttpClient http) => _http = http;

    public async Task<HttpResponse> SendAsync(HttpRequest request)
    {
        var msg = new HttpRequestMessage(request.Method, request.Url)
        {
            Content = new StringContent(request.Body, Encoding.UTF8, "application/json")
        };
        var response = await _http.SendAsync(msg);
        return new HttpResponse(
            (int)response.StatusCode,
            await response.Content.ReadAsStringAsync());
    }
}

// Decorator: encrypts request, decrypts response — transparent to caller
public class EncryptionHttpClientDecorator : IHttpClient
{
    private readonly IHttpClient _inner;
    private readonly IEncryptionKeyProvider _keyProvider;
    private readonly ILogger<EncryptionHttpClientDecorator> _logger;

    public EncryptionHttpClientDecorator(
        IHttpClient inner,
        IEncryptionKeyProvider keyProvider,
        ILogger<EncryptionHttpClientDecorator> logger)
    {
        _inner = inner;
        _keyProvider = keyProvider;
        _logger = logger;
    }

    public async Task<HttpResponse> SendAsync(HttpRequest request)
    {
        // 1. Derive encryption key from session context
        var key = _keyProvider.GetKey(request.SessionId, request.DeviceId);
        _logger.LogDebug("Encrypting request with key derived from session={SessionId}", request.SessionId);

        // 2. Encrypt sensitive fields in the request body
        var body = JsonDocument.Parse(request.Body).RootElement.Clone();
        var encryptedBody = new Dictionary<string, object>();

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is "data1" or "data2" or "uniquerequestId")
                encryptedBody[prop.Name] = AesEncrypt(prop.Value.GetString()!, key);
            else
                encryptedBody[prop.Name] = prop.Value.GetString()!;
        }

        // 3. Forward to inner client with encrypted body
        var encryptedRequest = request.WithBody(
            JsonSerializer.Serialize(encryptedBody));
        var response = await _inner.SendAsync(encryptedRequest);

        // 4. Decrypt response
        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(response.Body))
            return response;

        var responseBody = JsonDocument.Parse(response.Body).RootElement;
        if (responseBody.TryGetProperty("data", out var dataProp))
        {
            var decrypted = AesDecrypt(dataProp.GetString()!, key);
            _logger.LogDebug("Decrypted response data: {Data}", decrypted);
            return response.WithBody(decrypted);
        }

        return response;
    }

    private static string AesEncrypt(string plaintext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    private static string AesDecrypt(string ciphertext, byte[] key)
    {
        var fullCipher = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        var iv = new byte[16];
        var cipher = new byte[fullCipher.Length - 16];
        Buffer.BlockCopy(fullCipher, 0, iv, 0, 16);
        Buffer.BlockCopy(fullCipher, 16, cipher, 0, cipher.Length);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }
}

// Channel code: completely unaware of encryption
public class MahaChannel : IPaymentChannel
{
    private readonly IHttpClient _http;  // Could be decorated or not — doesn't know

    public MahaChannel(IHttpClient http) => _http = http;

    public async Task<ChannelOrderResult> SubmitOrderAsync(OrderRequest request)
    {
        var httpRequest = new HttpRequest(
            HttpMethod.Post,
            "/api/v2/payment",
            JsonSerializer.Serialize(new { data1 = "...", data2 = "...", uniquerequestId = "..." }));

        var response = await _http.SendAsync(httpRequest);
        return ParseResponse(response);  // Already decrypted by decorator
    }
}

// DI: compose decorators
services.AddHttpClient<MahaChannel>(c => c.BaseAddress = new Uri("https://api.maha.com"));

// Register IHttpClient with decorator wrapping
services.AddSingleton<IHttpClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("MahaChannel");
    var standard = new StandardHttpClient(http);
    var keyProvider = sp.GetRequiredService<IEncryptionKeyProvider>();
    var logger = sp.GetRequiredService<ILogger<EncryptionHttpClientDecorator>>();
    return new EncryptionHttpClientDecorator(standard, keyProvider, logger);
});

// For non-encrypted channels: no decorator
services.AddSingleton<IHttpClient>(sp =>
    new StandardHttpClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("PhonePeChannel")));
```

## Multiple Decorators

Stack decorators for multiple cross-cutting concerns:

```csharp
IHttpClient client = new StandardHttpClient(http);
client = new EncryptionHttpClientDecorator(client, keyProvider, logger);    // Encrypt
client = new RetryHttpClientDecorator(client, maxRetries: 3);              // Retry
client = new LoggingHttpClientDecorator(client, logger);                   // Log
client = new CircuitBreakerHttpClientDecorator(client, failures: 5);       // Circuit break
// MahaChannel.SendAsync() → Encrypt → Retry → Log → CircuitBreak → HTTP
```

## Trade-offs

| Gain | Cost |
|------|------|
| Encryption logic lives in ONE place, not scattered across channels | Debugging: stack trace goes through multiple wrappers |
| Compose behaviors: encrypt + retry + log, in any order | Order matters: retry-before-encrypt vs encrypt-before-retry |
| Channel code tested without encryption (inject bare IHttpClient) | Decorator must exactly match IHttpClient interface |

## Key Takeaway

In the real system, `EncryptionInterceptor` wraps HTTP calls for MahaBank, transparently encrypting `data1`/`data2`/`uniquerequestId`. The channel's business logic (`Maha.cs`, 300 lines) has **zero encryption code**. When we needed to change from AES-128 to AES-256, we changed the decorator — the channel was untouched.
