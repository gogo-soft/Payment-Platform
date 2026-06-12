# OCP — Open/Closed Principle

## Real Scenario

A payment platform integrates with 30+ external channels. Each channel requires a **different request signing algorithm**:

| Channel | Algorithm | Key Format |
|---------|-----------|------------|
| PhonePe | SHA256+RSA | PKCS#8 |
| Paytm | HMAC-SHA256 | Hex |
| FreeCharge | MD5 | Uppercase hex |
| MobiKwik | SHA1+RSA | PKCS#1 |
| Airtel | HMAC-SHA256 | Base64 |
| ... 25 more | ... | ... |

**The naive approach:**

```csharp
// ❌ Adding a new channel means MODIFYING this switch statement
public string Sign(Dictionary<string, string> data, string channel, string key)
{
    switch (channel)
    {
        case "phonepe":
            return Sha256RsaSign(data, key, KeyFormat.Pkcs8);
        case "paytm":
            return HmacSha256Sign(data, key).ToHex();
        case "freecharge":
            return Md5Sign(data, key);
        // ... 30+ cases, growing every sprint
    }
}
```

Every new channel changes `Sign()` — violating OCP. One typo in this switch and **all channels break**.

## The Pattern

**Open for extension, closed for modification.** Define an abstraction; each algorithm is an extension.

```csharp
// ✅ Abstraction: closed for modification
public interface ISigningStrategy
{
    string Sign(Dictionary<string, object> data, string secret);
    bool Verify(Dictionary<string, object> data, string signature, string secret);
}

// ✅ Extensions: each is a new class — no existing code changes
public class HmacSha256HexStrategy : ISigningStrategy
{
    public string Sign(Dictionary<string, object> data, string secret)
    {
        var sortedParams = data.Where(kv => kv.Key != "sign")
                               .Where(kv => kv.Value != null)
                               .OrderBy(kv => kv.Key)
                               .Select(kv => $"{kv.Key}={kv.Value}");
        var signStr = string.Join("&", sortedParams);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signStr));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    public bool Verify(Dictionary<string, object> data, string signature, string secret)
        => Sign(data, secret) == signature;
}

public class Sha256RsaStrategy : ISigningStrategy
{
    public string Sign(Dictionary<string, object> data, string secret)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(secret);
        var signStr = BuildSignString(data);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signStr),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    public bool Verify(Dictionary<string, object> data, string signature, string secret)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(secret);
        var signStr = BuildSignString(data);
        return rsa.VerifyData(
            Encoding.UTF8.GetBytes(signStr),
            Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    private static string BuildSignString(Dictionary<string, object> data)
        => string.Join("&", data.Where(kv => kv.Key != "sign")
                                 .OrderBy(kv => kv.Key)
                                 .Select(kv => $"{kv.Key}={kv.Value}"));
}

public class Md5Strategy : ISigningStrategy
{
    public string Sign(Dictionary<string, object> data, string secret)
    {
        var signStr = string.Join("&", data.Where(kv => kv.Key != "sign")
                                            .OrderBy(kv => kv.Key)
                                            .Select(kv => $"{kv.Key}={kv.Value}"))
                     + $"&key={secret}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStr));
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    public bool Verify(Dictionary<string, object> data, string signature, string secret)
        => Sign(data, secret) == signature;
}

// ✅ Registry: maps channel → strategy (configured once, never modified)
public class SigningStrategyRegistry
{
    private readonly Dictionary<string, ISigningStrategy> _strategies = new()
    {
        ["phonepe"] = new Sha256RsaStrategy(),
        ["paytm"]   = new HmacSha256HexStrategy(),
        ["freecharge"] = new Md5Strategy(),
        // New channel? Add one line. Nothing else changes.
    };

    public ISigningStrategy Get(string channelCode)
        => _strategies.TryGetValue(channelCode, out var s)
            ? s
            : throw new NotSupportedException($"Channel '{channelCode}' has no signing strategy");
}
```

## What Happens When a New Channel Arrives

```csharp
// 1. Create new strategy class (new file, zero existing code touched)
public class JioHmacStrategy : ISigningStrategy { ... }

// 2. Register it (one line)
_strategies["jio"] = new JioHmacStrategy();

// That's it. Sign(), Verify(), and all downstream code: untouched.
```

## Trade-offs

| Gain | Cost |
|------|------|
| New channel = new class, zero risk to existing | Boilerplate: 30 small strategy classes |
| Each algorithm tested in isolation | Strategy must be chosen at runtime (not compile-time) |
| sign.cs went from 200 → 600 lines without refactoring the core | Need the registry indirection |

## Key Takeaway

In the real system, `sign.cs` grew from 200 to 600 lines over 18 months — but **the core switch/registry has never been refactored**. That's OCP in action: the core is closed for modification, but the system is open for extension via new strategy classes.
