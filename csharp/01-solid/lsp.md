# LSP — Liskov Substitution Principle

## Real Scenario

In a payment platform, every API endpoint shares a common lifecycle:

1. Assign trace ID for request correlation
2. Resolve client IP (CDN-aware: `CF-Connecting-IP` header)
3. Check IP against blacklist
4. Validate request parameters
5. Execute business logic
6. Log structured metrics

Every handler must go through these steps in this exact order. If one handler skips IP checking, the system has a security hole.

## The Pattern

Define a base class that enforces the lifecycle. Subclasses must be **substitutable** — any derived class must work wherever the base is expected.

```csharp
// ✅ Base class: defines the contract. All handlers MUST follow this lifecycle.
public abstract class BaseHandler
{
    protected ILogger Logger { get; }
    protected IDistributedCache Cache { get; }
    private readonly IIpFilter _ipFilter;

    protected BaseHandler(ILogger logger, IDistributedCache cache, IIpFilter ipFilter)
    {
        Logger = logger;
        Cache = cache;
        _ipFilter = ipFilter;
    }

    // Template Method: lifecycle is fixed; subclasses only fill in the blanks
    public async Task<IResult> HandleAsync(HttpContext context)
    {
        // Step 1: Trace ID (enforced for ALL handlers)
        var traceId = Guid.NewGuid().ToString("N");
        context.Items["TraceId"] = traceId;

        // Step 2: Resolve real IP (enforced)
        var ip = ResolveClientIp(context);

        // Step 3: IP blacklist (enforced)
        if (await _ipFilter.IsBlockedAsync(ip))
            return Results.Problem(statusCode: 403, detail: $"IP {ip} is blocked");

        // Step 4: Validate parameters (enforced)
        var (isValid, error) = await ValidateAsync(context);
        if (!isValid)
            return Results.BadRequest(error);

        // Step 5: Business logic (subclass provides this)
        return await ProcessAsync(context);
    }

    // Subclasses implement ONLY business logic
    protected abstract Task<IResult> ProcessAsync(HttpContext context);

    // Subclasses MAY override validation (default: no-op)
    protected virtual Task<(bool, string?)> ValidateAsync(HttpContext context)
        => Task.FromResult((true, (string?)null));

    private static string ResolveClientIp(HttpContext context)
    {
        var cfIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        return !string.IsNullOrEmpty(cfIp) ? cfIp! : context.Connection.RemoteIpAddress!.ToString();
    }
}

// ✅ Subclass 1: Collection order — LSP compliant
public class CollectionHandler : BaseHandler
{
    private readonly IOrderService _orders;

    public CollectionHandler(ILogger<CollectionHandler> logger,
        IDistributedCache cache, IIpFilter ipFilter, IOrderService orders)
        : base(logger, cache, ipFilter) => _orders = orders;

    protected override async Task<(bool, string?)> ValidateAsync(HttpContext context)
    {
        // Subclass adds domain-specific validation
        var amount = context.Request.Form["amount"];
        if (string.IsNullOrEmpty(amount) || !decimal.TryParse(amount, out var a) || a <= 0)
            return (false, "Amount must be positive");
        return (true, null);
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        var order = await _orders.CreateCollectionAsync(context.Request.Form);
        return Results.Json(order);
    }
}

// ✅ Subclass 2: Disbursement — also LSP compliant
public class DisbursementHandler : BaseHandler
{
    private readonly IDisbursementService _disburse;

    public DisbursementHandler(ILogger<DisbursementHandler> logger,
        IDistributedCache cache, IIpFilter ipFilter, IDisbursementService disburse)
        : base(logger, cache, ipFilter) => _disburse = disburse;

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        var result = await _disburse.ExecuteAsync(context.Request.Form);
        return Results.Json(result);
    }
}

// ✅ ASP.NET endpoint: any BaseHandler works here
app.MapPost("/api/collection", (CollectionHandler h, HttpContext ctx) => h.HandleAsync(ctx));
app.MapPost("/api/disbursement", (DisbursementHandler h, HttpContext ctx) => h.HandleAsync(ctx));
```

## The LSP Test

```csharp
// If this compiles AND behaves correctly, LSP is satisfied:
BaseHandler handler = new CollectionHandler(...);   // OK
handler = new DisbursementHandler(...);              // OK
handler = new SomeNewHandler(...);                   // MUST also work

await handler.HandleAsync(context);  // IP checked? ✓. Trace ID assigned? ✓.
```

If `SomeNewHandler` throws when you call `HandleAsync()`, or skips IP validation, it violates LSP.

## Trade-offs

| Gain | Cost |
|------|------|
| Security/compliance enforced once, applied everywhere | Subclass can't opt out (by design — that's the point) |
| New handler type: 10 lines of business logic | Base class changes affect ALL handlers (rare after stabilization) |
| Integration tests for lifecycle run once | Inheritance depth = 1 (deliberate — no deep hierarchies) |

## Key Takeaway

In the real system, **all 40+ API endpoints** derive from `BaseHandler`. When we added CDN-aware IP resolution, we changed **one method** (`ResolveClientIp`) and every endpoint immediately supported it. That's LSP paying off.
