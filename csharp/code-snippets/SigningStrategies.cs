// code-snippets/SigningStrategies.cs
// Strategy Pattern + OCP: Multi-algorithm signing engine
// Extracted from a production payment platform handling 30+ channels

using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace PaymentPlatform.Patterns;

// === Strategy Interface ===
public interface ISigningStrategy
{
    string Sign(Dictionary<string, object> parameters, string secret);
    bool Verify(Dictionary<string, object> parameters, string signature, string secret);
}

// === Concrete Strategies ===

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

public class Md5Strategy : ISigningStrategy
{
    public string Sign(Dictionary<string, object> parameters, string secret)
    {
        var signStr = string.Join("&", parameters
            .Where(kv => kv.Key != "sign")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"))
            + $"&key={secret}";

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStr));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    public bool Verify(Dictionary<string, object> parameters, string signature, string secret)
        => Sign(parameters, secret) == signature;
}

public enum HmacOutputFormat { Hex, HexUpper, Base64 }

// === Strategy Registry (Factory) ===
public static class SigningStrategyFactory
{
    private static readonly Dictionary<string, ISigningStrategy> Registry = new()
    {
        ["phonepe"]    = new RsaSignatureStrategy(HashAlgorithmName.SHA256),
        ["paytm"]      = new HmacSha256Strategy(HmacOutputFormat.Hex),
        ["freecharge"] = new Md5Strategy(),
        ["mobi"]       = new RsaSignatureStrategy(HashAlgorithmName.SHA1),
        ["airtel"]     = new HmacSha256Strategy(HmacOutputFormat.Base64),
    };

    public static ISigningStrategy GetFor(string channelCode)
        => Registry.TryGetValue(channelCode, out var strategy)
            ? strategy
            : throw new NotSupportedException($"No signing strategy for channel '{channelCode}'");

    // Adding a new channel: one line, zero existing code changed.
    public static void Register(string channelCode, ISigningStrategy strategy)
        => Registry[channelCode] = strategy;
}
