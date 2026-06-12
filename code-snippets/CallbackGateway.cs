// code-snippets/CallbackGateway.cs
// Multi-Channel Gateway Pattern: Normalize 30+ external webhooks into one internal format
// Extracted from a production payment platform

namespace PaymentPlatform.Patterns;

// === Normalized Internal Format ===
public record NormalizedCallback
{
    public required string ChannelCode { get; init; }
    public required string ExternalOrderId { get; init; }
    public required string InternalOrderCode { get; init; }
    public decimal Amount { get; init; }
    public required string Utr { get; init; }
    public CallbackStatus Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? RawPayload { get; init; }
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
}

public enum CallbackStatus { Success, Failed, Pending, Unknown }

// === Channel Adapter Interface ===
public interface IChannelCallbackAdapter
{
    string ChannelCode { get; }
    NormalizedCallback Normalize(HttpRequest request, string rawBody);
}

// === Concrete Adapters ===

public class PhonePeCallbackAdapter : IChannelCallbackAdapter
{
    public string ChannelCode => "phonepe";

    public NormalizedCallback Normalize(HttpRequest request, string rawBody)
    {
        var payload = JsonSerializer.Deserialize<PhonePeCallbackPayload>(rawBody)!;

        // PhonePe sends amount in paise
        var amount = decimal.Parse(payload.Amount) / 100;

        return new NormalizedCallback
        {
            ChannelCode = "phonepe",
            ExternalOrderId = payload.MerchantTransactionId,
            InternalOrderCode = payload.MerchantTransactionId,
            Amount = amount,
            Utr = payload.TransactionId,
            Status = payload.Code switch
            {
                "SUCCESS" => CallbackStatus.Success,
                "FAILURE" => CallbackStatus.Failed,
                "PENDING" => CallbackStatus.Pending,
                _ => CallbackStatus.Unknown
            },
            ErrorCode = payload.Code != "SUCCESS" ? payload.Code : null,
            RawPayload = rawBody,
        };
    }

    private record PhonePeCallbackPayload(
        string MerchantTransactionId, string TransactionId,
        string Amount, string Code);
}

public class PaytmCallbackAdapter : IChannelCallbackAdapter
{
    public string ChannelCode => "paytm";

    public NormalizedCallback Normalize(HttpRequest request, string rawBody)
    {
        var form = ParseFormEncoded(rawBody);

        return new NormalizedCallback
        {
            ChannelCode = "paytm",
            ExternalOrderId = form["ORDERID"],
            InternalOrderCode = form["ORDERID"],
            Amount = decimal.Parse(form["TXNAMOUNT"]),
            Utr = form["TXNID"],
            Status = form["STATUS"] switch
            {
                "TXN_SUCCESS" => CallbackStatus.Success,
                "TXN_FAILURE" => CallbackStatus.Failed,
                "PENDING" => CallbackStatus.Pending,
                _ => CallbackStatus.Unknown
            },
            RawPayload = rawBody,
        };
    }

    private static Dictionary<string, string> ParseFormEncoded(string body)
    {
        var dict = new Dictionary<string, string>();
        foreach (var pair in body.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
                dict[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }
        return dict;
    }
}

// === Gateway: Single Entry Point for ALL Callbacks ===
public class CallbackGateway
{
    private readonly Dictionary<string, IChannelCallbackAdapter> _adapters;
    private readonly IOrderCompletionService _completion;
    private readonly ISlowQueryMonitor _monitor;
    private readonly ILogger<CallbackGateway> _logger;

    public CallbackGateway(
        IEnumerable<IChannelCallbackAdapter> adapters,
        IOrderCompletionService completion,
        ISlowQueryMonitor monitor,
        ILogger<CallbackGateway> logger)
    {
        _adapters = adapters.ToDictionary(a => a.ChannelCode);
        _completion = completion;
        _monitor = monitor;
        _logger = logger;
    }

    public async Task<IResult> HandleAsync(string channelCode, HttpContext context)
    {
        if (!_adapters.TryGetValue(channelCode, out var adapter))
        {
            _logger.LogWarning("Unknown channel callback: {Channel}", channelCode);
            return Results.NotFound(new { error = "unknown_channel" });
        }

        var rawBody = await ReadBodyAsync(context.Request);

        try
        {
            var callback = adapter.Normalize(context.Request, rawBody);

            _logger.LogInformation(
                "[CALLBACK] channel={Channel} status={Status} extId={ExtId} utr={Utr}",
                callback.ChannelCode, callback.Status, callback.ExternalOrderId, callback.Utr);

            if (callback.Status == CallbackStatus.Success)
            {
                var sw = Stopwatch.StartNew();
                await _completion.CompleteOrderAsync(callback.InternalOrderCode, callback.Utr);
                sw.Stop();

                if (sw.ElapsedMilliseconds > 500)
                    _monitor.Report("callback_completion", sw.ElapsedMilliseconds,
                        $"code={callback.InternalOrderCode}");
            }
            else if (callback.Status == CallbackStatus.Failed)
            {
                await _completion.MarkFailedAsync(callback.InternalOrderCode,
                    callback.ErrorCode ?? "unknown");
            }

            return Results.Ok(new { status = "received" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Callback failed: channel={Channel}", channelCode);
            return Results.StatusCode(500);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }
}

// === ASP.NET Endpoint (in Program.cs) ===
// app.MapPost("/callback/{channelCode}",
//     (string channelCode, HttpContext ctx, CallbackGateway gw)
//         => gw.HandleAsync(channelCode, ctx));

// === Slow Query Monitor ===
public interface ISlowQueryMonitor
{
    void Report(string operation, long elapsedMs, string detail);
}

public class SlowQueryMonitor : ISlowQueryMonitor
{
    private readonly ILogger<SlowQueryMonitor> _logger;
    public SlowQueryMonitor(ILogger<SlowQueryMonitor> logger) => _logger = logger;
    public void Report(string operation, long elapsedMs, string detail)
        => _logger.LogWarning("[SLOW] op={Op} durationMs={DurationMs} detail={Detail}",
            operation, elapsedMs, detail);
}

// Placeholder interfaces
public interface IOrderCompletionService
{
    Task CompleteOrderAsync(string code, string utr);
    Task MarkFailedAsync(string code, string errorCode);
}

// Placeholder usings
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
