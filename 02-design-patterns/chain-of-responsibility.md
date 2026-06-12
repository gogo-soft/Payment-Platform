# Chain of Responsibility Pattern

## Real Scenario

Every API request to a payment platform must pass through multiple security checks before reaching business logic:

1. **XSS filtering** — strip HTML/script tags from all inputs
2. **IP blacklist** — reject known malicious IPs
3. **Rate limiting** — prevent abuse (max 10 req/s per IP)
4. **Signature verification** — ensure request hasn't been tampered with
5. **Parameter validation** — required fields present, amounts positive

The naive approach chains these in the controller:

```csharp
// ❌ Controller knows about ALL middleware concerns
public async Task<IResult> CreateOrder(HttpContext context)
{
    // XSS filter...
    // IP check...
    // Rate limit...
    // Signature...
    // Validation...
    // FINALLY: business logic
}
```

Controller has 5 reasons to change — SRP violation. Adding a new check (e.g., geo-fencing) means modifying the controller.

## The Pattern

ASP.NET Core's middleware pipeline IS the Chain of Responsibility. Each middleware handles one concern, then passes to the next (or short-circuits).

```csharp
// Each middleware is a link in the chain

// Link 1: XSS Filter
public class XssFilterMiddleware
{
    private readonly RequestDelegate _next;

    public XssFilterMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            foreach (var key in form.Keys)
            {
                var sanitized = Sanitize(form[key]!);
                context.Items[$"sanitized_{key}"] = sanitized;
            }
        }
        await _next(context);  // Pass to next link
    }

    private static string Sanitize(string input)
        => System.Net.WebUtility.HtmlEncode(input)
            .Replace("'", "&#39;")
            .Replace("\"", "&quot;");
}

// Link 2: IP Blacklist
public class IpBlacklistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIpFilter _filter;

    public IpBlacklistMiddleware(RequestDelegate next, IIpFilter filter)
    {
        _next = next;
        _filter = filter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = ResolveIp(context);
        if (await _filter.IsBlockedAsync(ip))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { code = 10000, message = "IP is blocked" });
            return;  // Short-circuit: don't call _next
        }
        await _next(context);
    }

    private static string ResolveIp(HttpContext context)
        => context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
           ?? context.Connection.RemoteIpAddress!.ToString();
}

// Link 3: Rate Limiter
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDistributedCache _cache;

    public RateLimitMiddleware(RequestDelegate next, IDistributedCache cache)
    {
        _next = next;
        _cache = cache;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = ResolveIp(context);
        var key = $"ratelimit:{ip}";
        var count = await _cache.GetOrSetAsync(key, () => 0, TimeSpan.FromSeconds(1));

        if (count >= 10)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsJsonAsync(new { code = 10007, message = "Too many requests" });
            return;  // Short-circuit
        }

        await _cache.IncrementAsync(key);
        await _next(context);
    }
}

// Link 4: Signature Verification
public class SignatureVerificationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISigningStrategyFactory _signerFactory;

    public SignatureVerificationMiddleware(RequestDelegate next, ISigningStrategyFactory factory)
    {
        _next = next;
        _signerFactory = factory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var channel = form["gateway"].ToString();
        var signer = _signerFactory.GetFor(channel);

        var parameters = form.Keys.ToDictionary(k => k.ToString(), k => (object)form[k].ToString());
        var signature = parameters["sign"].ToString() ?? "";

        if (!signer.Verify(parameters, signature, GetSecret(channel)))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { code = 10006, message = "Signature error" });
            return;
        }

        await _next(context);
    }
}

// Pipeline assembly: order matters, each link handles ONE concern
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseMiddleware<XssFilterMiddleware>();              // 1. Sanitize inputs
app.UseMiddleware<IpBlacklistMiddleware>();             // 2. Block bad IPs
app.UseMiddleware<RateLimitMiddleware>();               // 3. Throttle
app.UseMiddleware<SignatureVerificationMiddleware>();   // 4. Verify authenticity

app.MapPost("/api/collection", (HttpContext ctx) =>
{
    // Controller: ONLY business logic. All security is handled by the chain.
    var form = ctx.Request.Form;
    return Results.Json(new { code = 20000, order_id = Guid.NewGuid().ToString("N") });
});
```

## Adding a New Link (Geo-Fencing)

```csharp
// 1. Create new middleware (new file)
public class GeoFenceMiddleware
{
    private readonly RequestDelegate _next;
    public GeoFenceMiddleware(RequestDelegate next) => _next = next;
    public async Task InvokeAsync(HttpContext context) { /* check country */ await _next(context); }
}

// 2. Insert into pipeline (one line)
app.UseMiddleware<GeoFenceMiddleware>();  // Place between IpBlacklist and RateLimit
```

Zero changes to any existing middleware or controller.

## Trade-offs

| Gain | Cost |
|------|------|
| Add/remove/reorder checks without touching business logic | Ordering is critical — wrong order = security hole |
| Each middleware testable in isolation | Pipeline can get long; need documentation on order rationale |
| Short-circuit: reject early, save CPU | Every request traverses the chain (overhead for simple endpoints) |

## Key Takeaway

In the real system, the request pipeline is: XSS → IP blacklist → signature → validation → rate limit → controller. When we added CDN-aware IP resolution, we changed **one middleware** (`IpBlacklistMiddleware`). Every endpoint immediately supported it. That's Chain of Responsibility.
