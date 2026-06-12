// code-snippets/MiddlewarePipeline.cs
// Chain of Responsibility + Template Method: Request processing pipeline
// Extracted from a production payment platform

namespace PaymentPlatform.Patterns;

// === Template Method: Base Handler ===
public abstract class PaymentRequestHandler
{
    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var traceId = Guid.NewGuid().ToString("N");
        context.Items["TraceId"] = traceId;
        var logger = context.RequestServices.GetRequiredService<ILogger<PaymentRequestHandler>>();

        using var scope = logger.BeginScope(new { TraceId = traceId });

        var ip = ResolveClientIp(context);

        var ipFilter = context.RequestServices.GetRequiredService<IIpFilter>();
        if (await ipFilter.IsBlockedAsync(ip))
        {
            logger.LogWarning("Blocked IP {Ip}", ip);
            return Results.Json(new { code = 10000, message = "IP blocked" });
        }

        var (isValid, error) = await ValidateAsync(context);
        if (!isValid)
            return Results.Json(new { code = 10003, message = error });

        var sw = Stopwatch.StartNew();
        var result = await ProcessAsync(context);
        sw.Stop();

        logger.LogInformation("[METRIC] path={Path} ip={Ip} durationMs={DurationMs}",
            context.Request.Path, ip, sw.ElapsedMilliseconds);

        return result;
    }

    protected virtual Task<(bool IsValid, string? Error)> ValidateAsync(HttpContext context)
        => Task.FromResult((true, (string?)null));

    protected abstract Task<IResult> ProcessAsync(HttpContext context);

    private static string ResolveClientIp(HttpContext context)
        => context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
           ?? context.Connection.RemoteIpAddress!.ToString();
}

// --- Subclass 1: Collection Order ---
public class CollectionOrderHandler : PaymentRequestHandler
{
    protected override async Task<(bool, string?)> ValidateAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        if (!form.ContainsKey("amount") || !decimal.TryParse(form["amount"], out var a) || a <= 0)
            return (false, "amount must be positive");
        if (!form.ContainsKey("order_id"))
            return (false, "order_id is required");
        return (true, null);
    }

    protected override async Task<IResult> ProcessAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        return Results.Json(new { code = 20000, order_id = Guid.NewGuid().ToString("N") });
    }
}

// === Chain of Responsibility: Middleware Pipeline ===

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
                context.Items[$"sanitized_{key}"] = Sanitize(form[key]!);
            }
        }
        await _next(context);
    }

    private static string Sanitize(string input)
        => WebUtility.HtmlEncode(input).Replace("'", "&#39;").Replace("\"", "&quot;");
}

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
        var ip = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
                 ?? context.Connection.RemoteIpAddress!.ToString();

        if (await _filter.IsBlockedAsync(ip))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { code = 10000, message = "IP blocked" });
            return; // Short-circuit
        }
        await _next(context);
    }
}

public class SignatureVerificationMiddleware
{
    private readonly RequestDelegate _next;

    public SignatureVerificationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var gateway = form["gateway"].ToString();
        var signer = SigningStrategyFactory.GetFor(gateway);

        var parameters = form.Keys
            .ToDictionary(k => k.ToString(), k => (object)form[k].ToString());
        var signature = parameters["sign"].ToString() ?? "";

        if (!signer.Verify(parameters, signature, "channel-secret"))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { code = 10006, message = "Signature error" });
            return;
        }
        await _next(context);
    }
}

// === Pipeline Assembly (in Program.cs) ===
//
// var app = builder.Build();
// app.UseMiddleware<XssFilterMiddleware>();               // 1. Sanitize
// app.UseMiddleware<IpBlacklistMiddleware>();              // 2. Block bad IPs
// app.UseMiddleware<SignatureVerificationMiddleware>();    // 3. Verify sign
// app.MapPost("/api/collection", (CollectionOrderHandler h, HttpContext ctx) => h.HandleAsync(ctx));

// === Supporting Types ===
public interface IIpFilter
{
    Task<bool> IsBlockedAsync(string ip);
}

// Placeholder usings for compilation
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.HttpResults;
