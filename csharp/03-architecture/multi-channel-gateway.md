# Multi-Channel Gateway Pattern

## The Problem

A payment platform integrates with 30+ external channels. Each channel has its own:

- **Callback format**: JSON, XML, form-encoded, query string
- **Status codes**: `SUCCESS` vs `COMPLETED` vs `00` vs `1`
- **Field names**: `txnId` vs `transaction_id` vs `referenceNo` vs `orderId`
- **Signature method**: HMAC-SHA256, RSA-SHA1, MD5, plain text
- **Error handling**: some return HTTP 200 with error code, some return HTTP 500

If each channel's callback handler parses and processes independently, you get 30 different implementations of "is this order successful?" — and 30 places where a bug can live.

## The Pattern

Normalize all external callbacks into a **single internal event format** at the gateway boundary. Everything downstream sees only the normalized format.

```csharp
// Internal normalized format — ONE format for the entire system
public record NormalizedCallback
{
    public string ChannelCode { get; init; }
    public string ExternalOrderId { get; init; }
    public string InternalOrderCode { get; init; }
    public decimal Amount { get; init; }
    public string Utr { get; init; }                     // Unique Transaction Reference
    public CallbackStatus Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? RawPayload { get; init; }              // For audit/debugging
    public DateTime ReceivedAt { get; init; }
}

public enum CallbackStatus { Success, Failed, Pending, Unknown }

// Channel-specific adapter: the ONLY place that knows PhonePe's format
public class PhonePeCallbackAdapter : IChannelCallbackAdapter
{
    public string ChannelCode => "phonepe";

    public NormalizedCallback Normalize(HttpRequest request, string rawBody)
    {
        // Parse PhonePe's specific format
        var payload = JsonSerializer.Deserialize<PhonePeCallbackPayload>(rawBody)!;

        // Verify PhonePe's signature
        var signer = SigningStrategyFactory.GetFor("phonepe");
        var parameters = ParseQueryString(request.QueryString.Value!);
        if (!signer.Verify(parameters, payload.Signature, GetChannelSecret("phonepe")))
            throw new SignatureException("PhonePe callback signature verification failed");

        // Map PhonePe's fields to normalized format
        return new NormalizedCallback
        {
            ChannelCode = "phonepe",
            ExternalOrderId = payload.MerchantTransactionId,
            InternalOrderCode = ExtractOrderCode(payload.MerchantTransactionId),
            Amount = decimal.Parse(payload.Amount) / 100,  // PhonePe sends in paise
            Utr = payload.TransactionId,
            Status = MapStatus(payload.Code),
            ErrorCode = payload.Code != "SUCCESS" ? payload.Code : null,
            RawPayload = rawBody,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private static CallbackStatus MapStatus(string phonePeCode) => phonePeCode switch
    {
        "SUCCESS" => CallbackStatus.Success,
        "FAILURE" => CallbackStatus.Failed,
        "PENDING" => CallbackStatus.Pending,
        _ => CallbackStatus.Unknown
    };
}

// Another adapter: Paytm uses form-encoded data, different field names
public class PaytmCallbackAdapter : IChannelCallbackAdapter
{
    public string ChannelCode => "paytm";

    public NormalizedCallback Normalize(HttpRequest request, string rawBody)
    {
        var form = ParseFormEncoded(rawBody);

        // Verify Paytm's checksum
        var signer = SigningStrategyFactory.GetFor("paytm");
        var parameters = form.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        if (!signer.Verify(parameters, form["CHECKSUMHASH"], GetChannelSecret("paytm")))
            throw new SignatureException("Paytm callback checksum verification failed");

        return new NormalizedCallback
        {
            ChannelCode = "paytm",
            ExternalOrderId = form["ORDERID"],
            InternalOrderCode = form["ORDERID"],
            Amount = decimal.Parse(form["TXNAMOUNT"]),
            Utr = form["TXNID"],
            Status = MapStatus(form["STATUS"]),
            RawPayload = rawBody,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private static CallbackStatus MapStatus(string paytmStatus) => paytmStatus switch
    {
        "TXN_SUCCESS" => CallbackStatus.Success,
        "TXN_FAILURE" => CallbackStatus.Failed,
        "PENDING" => CallbackStatus.Pending,
        _ => CallbackStatus.Unknown
    };
}

// Gateway: routes incoming callback to correct adapter
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

    // Single entry point for ALL channel callbacks
    public async Task<IResult> HandleCallbackAsync(
        string channelCode, HttpContext context)
    {
        if (!_adapters.TryGetValue(channelCode, out var adapter))
        {
            _logger.LogWarning("Unknown channel callback: {Channel}", channelCode);
            return Results.NotFound();
        }

        // Read body once
        using var reader = new StreamReader(context.Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        try
        {
            // Adapter normalizes channel-specific format → internal format
            var callback = adapter.Normalize(context.Request, rawBody);

            _logger.LogInformation(
                "[CALLBACK] channel={Channel} status={Status} externalId={ExtId} utr={Utr}",
                callback.ChannelCode, callback.Status, callback.ExternalOrderId, callback.Utr);

            // From this point on: everything is normalized. No channel-specific logic.
            if (callback.Status == CallbackStatus.Success)
            {
                var sw = Stopwatch.StartNew();
                var completed = await _completion.CompleteOrderAsync(
                    callback.InternalOrderCode, callback.Utr);
                sw.Stop();

                if (sw.ElapsedMilliseconds > 500)
                    _monitor.ReportSlowQuery("callback_completion", sw.ElapsedMilliseconds,
                        $"code={callback.InternalOrderCode}");
            }
            else if (callback.Status == CallbackStatus.Failed)
            {
                await _completion.MarkOrderFailedAsync(
                    callback.InternalOrderCode, callback.ErrorCode ?? "unknown");
            }

            // Always return 200 to external channel (prevents retry storms)
            return Results.Ok(new { status = "received" });
        }
        catch (SignatureException ex)
        {
            _logger.LogError(ex, "Callback signature verification failed for {Channel}", channelCode);
            return Results.BadRequest(new { error = "signature" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Callback processing failed for {Channel}", channelCode);
            return Results.StatusCode(500);
        }
    }
}

// ASP.NET endpoint: one route handles ALL channels
app.MapPost("/callback/{channelCode}",
    (string channelCode, HttpContext context, CallbackGateway gateway)
        => gateway.HandleCallbackAsync(channelCode, context));
```

## The Routing Table

```
POST /callback/phonepe    → PhonePeCallbackAdapter  → NormalizedCallback → OrderCompletionService
POST /callback/paytm      → PaytmCallbackAdapter    → NormalizedCallback → OrderCompletionService
POST /callback/freecharge → FreeChargeAdapter       → NormalizedCallback → OrderCompletionService
POST /callback/airtel     → AirtelCallbackAdapter   → NormalizedCallback → OrderCompletionService
... 26 more ...
```

Every downstream system sees only `NormalizedCallback`. Adding a new channel means:
1. Implement `IChannelCallbackAdapter`
2. Register in DI
3. That's it.

## Trade-offs

| Gain | Cost |
|------|------|
| One code path for order completion, not 30 | Adapter layer adds indirection |
| New channel = new adapter class + 1 DI registration | Each adapter must map its channel's quirks (unavoidable) |
| Signature verification centralized per channel | Need to maintain 30+ adapter classes |

## Key Takeaway

In the gateway pattern above, a single `CallbackGateway` handles 30+ channel callbacks via `/callback/{channelCode}`, normalizing each into a single internal format. When we added centralized slow-query monitoring, we added **3 lines** to `CallbackGateway` — every channel immediately got monitoring. Without this pattern, you'd touch 30 adapter files.
