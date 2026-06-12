# Template Method Pattern

## Real Scenario

Every request to a payment API must go through the same lifecycle, in the same order:

1. Assign trace ID
2. Resolve client IP (CDN-aware)
3. IP blacklist check
4. Parameter validation
5. Business logic
6. Structured metrics logging

If a developer writes a new endpoint and forgets step 3, the system has a security hole. If they reorder step 2 and 3, CDN IPs bypass the blacklist.

## The Pattern

Define the skeleton in a base class. Subclasses **cannot change the order** — they only fill in the blanks.

```csharp
public abstract class PaymentRequestHandler
{
    public async Task<IResult> HandleAsync(HttpContext context)
    {
        // Step 1: Trace ID (locked — subclasses can't skip or reorder)
        var traceId = Guid.NewGuid().ToString("N");
        context.Items["TraceId"] = traceId;
        var logger = context.RequestServices.GetRequiredService<ILogger<PaymentRequestHandler>>();

        using (Log.BeginScope(logger, new { TraceId = traceId }))
        {
            // Step 2: Resolve IP (locked)
            var ip = ResolveClientIp(context);

            // Step 3: IP check (locked)
            var ipFilter = context.RequestServices.GetRequiredService<IIpFilter>();
            if (await ipFilter.IsBlockedAsync(ip))
            {
                logger.LogWarning("Blocked IP {Ip} for path {Path}", ip, context.Request.Path);
                return Results.Json(new { code = 10000, message = "IP is blocked" });
            }

            // Step 4: Validate (subclass CAN override, but it runs HERE, not elsewhere)
            var (isValid, error) = await ValidateAsync(context);
            if (!isValid)
            {
                logger.LogWarning("Validation failed: {Error}", error);
                return Results.Json(new { code = 10003, message = error });
            }

            // Step 5: Business logic (subclass MUST implement)
            var sw = Stopwatch.StartNew();
            var result = await ProcessAsync(context);
            sw.Stop();

            // Step 6: Metrics (locked)
            logger.LogInformation(
                "[METRIC] path={Path} ip={Ip} duration_ms={DurationMs}",
                context.Request.Path, ip, sw.ElapsedMilliseconds);

            return result;
        }
    }

    // 🔒 Hook: subclasses MAY override (default: no-op validation)
    protected virtual Task<(bool IsValid, string? Error)> ValidateAsync(HttpContext context)
        => Task.FromResult((true, (string?)null));

    // 🔒 Hook: subclasses MUST implement
    protected abstract Task<IResult> ProcessAsync(HttpContext context);

    private static string ResolveClientIp(HttpContext context)
    {
        var cf = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        return !string.IsNullOrEmpty(cf) ? cf! : context.Connection.RemoteIpAddress!.ToString();
    }
}

// Subclass: only writes business logic
public class CollectionOrderHandler : PaymentRequestHandler
{
    protected override async Task<(bool, string?)> ValidateAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        if (!form.ContainsKey("amount") || !decimal.TryParse(form["amount"], out var a) || a <= 0)
            return (false, "amount must be a positive number");
        if (!form.ContainsKey("order_id"))
            return (false, "order_id is required");
        return (true, null);
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var orderService = context.RequestServices.GetRequiredService<ICollectionService>();
        var order = await orderService.CreateAsync(form);
        return Results.Json(new { code = 20000, data = order });
    }
}
```

## Template Method vs Strategy

| | Template Method | Strategy |
|---|---|---|
| Control | Base class controls the flow | Caller controls the flow |
| Extension | Subclass overrides specific steps | Caller injects different strategy |
| Best for | Fixed lifecycle with variation in steps | Interchangeable algorithms |
| In our system | Request lifecycle (XSS → IP → Validate → Process) | Signing algorithms (HMAC vs RSA vs MD5) |

## Key Takeaway

In the real system, we once accidentally deployed a handler that skipped IP validation. It took 3 hours to notice. After refactoring to Template Method, **that mistake is now impossible** — the base class enforces the lifecycle. 40+ handlers, zero security regressions.
