// code-snippets/EncryptionInterceptor.cs
// Decorator Pattern: Transparent field-level encryption for HTTP calls
// Extracted from a production payment platform

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaymentPlatform.Patterns;

// === Core Abstraction ===
public interface IHttpClient
{
    Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default);
}

public record HttpRequest(HttpMethod Method, string Url, string Body,
    Dictionary<string, string>? Headers = null);

public record HttpResponse(int StatusCode, string Body, bool IsSuccessStatusCode)
{
    public HttpResponse WithBody(string newBody) => this with { Body = newBody };
}

// === Concrete: Raw HTTP ===
public class StandardHttpClient : IHttpClient
{
    private readonly System.Net.Http.HttpClient _http;

    public StandardHttpClient(System.Net.Http.HttpClient http) => _http = http;

    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        var msg = new System.Net.Http.HttpRequestMessage(request.Method, request.Url)
        {
            Content = new StringContent(request.Body, Encoding.UTF8, "application/json")
        };

        if (request.Headers is not null)
            foreach (var (key, value) in request.Headers)
                msg.Headers.TryAddWithoutValidation(key, value);

        var response = await _http.SendAsync(msg, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        return new HttpResponse((int)response.StatusCode, body, response.IsSuccessStatusCode);
    }
}

// === Decorator: Transparent Encryption ===
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

    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        // 1. Derive encryption key
        var key = _keyProvider.GetKey();
        _logger.LogDebug("Encrypting request with derived key");

        // 2. Encrypt sensitive request fields
        var body = JsonDocument.Parse(request.Body).RootElement;
        var encryptedBody = new Dictionary<string, object?>();

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.Name is "data1" or "data2" or "uniquerequestId")
                encryptedBody[prop.Name] = Encrypt(prop.Value.GetString()!, key);
            else
                encryptedBody[prop.Name] = prop.Value.GetString();
        }

        // 3. Forward with encrypted body
        var encryptedRequest = request with
        {
            Body = JsonSerializer.Serialize(encryptedBody)
        };
        var response = await _inner.SendAsync(encryptedRequest, ct);

        // 4. Decrypt response
        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(response.Body))
            return response;

        var responseBody = JsonDocument.Parse(response.Body).RootElement;
        if (responseBody.TryGetProperty("data", out var dataProp) &&
            dataProp.ValueKind == JsonValueKind.String)
        {
            var decrypted = Decrypt(dataProp.GetString()!, key);
            _logger.LogDebug("Decrypted response: {Data}", decrypted);
            return response.WithBody(decrypted);
        }

        return response;
    }

    private static string Encrypt(string plaintext, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string ciphertext, byte[] key)
    {
        var fullCipher = Convert.FromBase64String(ciphertext);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;

        // Extract IV (first 16 bytes)
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

// === Key Provider ===
public interface IEncryptionKeyProvider
{
    byte[] GetKey();
}

public class SessionKeyProvider : IEncryptionKeyProvider
{
    private readonly byte[] _key;

    public SessionKeyProvider(string base64Key)
        => _key = Convert.FromBase64String(base64Key);

    public byte[] GetKey() => _key;
}

// === DI Composition (in Program.cs) ===
//
// builder.Services.AddHttpClient("maha", c => c.BaseAddress = new Uri("https://api.maha.com"));
// builder.Services.AddSingleton<IEncryptionKeyProvider>(new SessionKeyProvider("..."));
// builder.Services.AddSingleton<IHttpClient>(sp =>
// {
//     var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("maha");
//     var standard = new StandardHttpClient(http);
//     var keyProvider = sp.GetRequiredService<IEncryptionKeyProvider>();
//     var logger = sp.GetRequiredService<ILogger<EncryptionHttpClientDecorator>>();
//     return new EncryptionHttpClientDecorator(standard, keyProvider, logger);
// });

// === Usage ===
public class MahaChannel
{
    private readonly IHttpClient _http; // Could be decorated or not — doesn't know

    public MahaChannel(IHttpClient http) => _http = http;

    public async Task<string> SubmitOrderAsync(string data1, string data2, string requestId)
    {
        var request = new HttpRequest(
            HttpMethod.Post,
            "/api/v2/payment",
            JsonSerializer.Serialize(new { data1, data2, uniquerequestId = requestId }));

        var response = await _http.SendAsync(request);
        return response.Body; // Already decrypted by decorator
    }
}

// Placeholder usings
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
