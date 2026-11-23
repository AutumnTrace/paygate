using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Paygate.Api.Infrastructure;
using Paygate.Application.Providers;
using Paygate.Application.Webhooks;
using Paygate.Domain.Payments;

namespace Paygate.Api.Endpoints;

public sealed record WebhookResponse(string EventId, WebhookOutcome Outcome, Guid? PaymentId, PaymentStatus? PaymentStatus, string Detail);

public static partial class WebhookEndpoints
{
    private const int MaxEventIdLength = 128;

    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);

        v1.MapPost("/webhooks/{provider}", HandleAsync)
            .WithTags("Webhooks")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Webhooks)
            .Accepts<WebhookPayload>("application/json")
            .WithName("ReceiveProviderWebhook")
            .WithSummary("Receive a status update from a provider")
            .WithDescription(
                "Authenticated by signature, not API key: Webhook-Timestamp (unix seconds) and " +
                "Webhook-Signature: v1=hex(HMAC-SHA256(secret, \"{timestamp}.{raw body}\")). Deliveries older than the " +
                "tolerance are rejected, each event id is processed once, and out-of-order updates are acknowledged but ignored.");

        return v1;
    }

    private static async Task<Results<Ok<WebhookResponse>, ProblemHttpResult>> HandleAsync(
        string provider,
        HttpContext http,
        ProviderCatalog catalog,
        WebhookVerifier verifier,
        WebhookService service,
        IOptions<WebhookOptions> options,
        ILoggerFactory loggers)
    {
        if (!catalog.TryGet(provider, out var known))
        {
            return Problems.NotFound($"Provider '{provider}'");
        }

        var body = await ReadBodyAsync(http, options.Value.MaxBodyBytes);
        if (body is null)
        {
            return Problems.Result(StatusCodes.Status413PayloadTooLarge, "request_too_large", $"Webhook bodies are limited to {options.Value.MaxBodyBytes} bytes.");
        }

        var verification = verifier.Verify(
            known.Name,
            http.Request.Headers[WebhookSignature.TimestampHeader],
            http.Request.Headers[WebhookSignature.SignatureHeader],
            body);

        if (verification != WebhookVerificationResult.Valid)
        {
            if (verification == WebhookVerificationResult.NoSecretConfigured)
            {
                LogNoSecret(loggers.CreateLogger("Paygate.Webhooks"), known.Name);
            }

            // Same status for every signature failure; the code tells an integrator what to fix
            // without helping an attacker (no secret material is involved in the message).
            return Problems.Result(StatusCodes.Status401Unauthorized, ToCode(verification), Describe(verification, options.Value.Tolerance));
        }

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Id) || payload.Id.Length > MaxEventIdLength || string.IsNullOrWhiteSpace(payload.Type))
        {
            return Problems.Result(StatusCodes.Status400BadRequest, "invalid_payload", $"The body must be JSON with an id (1 to {MaxEventIdLength} characters) and a type.");
        }

        try
        {
            var result = await service.HandleAsync(known.Name, payload, http.RequestAborted);
            return TypedResults.Ok(new WebhookResponse(result.EventId, result.Outcome, result.PaymentId, result.PaymentStatus, result.Detail));
        }
        catch (WebhookPaymentNotFoundException ex)
        {
            return Problems.Result(StatusCodes.Status404NotFound, "payment_not_found", ex.Message);
        }
        catch (WebhookPaymentNotReadyException ex)
        {
            http.Response.Headers.RetryAfter = "2";
            return Problems.Result(StatusCodes.Status409Conflict, "payment_not_ready", ex.Message);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext http, int limit)
    {
        if (http.Request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ToCode(WebhookVerificationResult result) => result switch
    {
        WebhookVerificationResult.MissingHeaders => "missing_signature",
        WebhookVerificationResult.InvalidTimestamp => "invalid_timestamp",
        WebhookVerificationResult.TimestampOutsideTolerance => "timestamp_outside_tolerance",
        _ => "invalid_signature",
    };

    private static string Describe(WebhookVerificationResult result, TimeSpan tolerance) => result switch
    {
        WebhookVerificationResult.MissingHeaders => $"Send {WebhookSignature.TimestampHeader} and {WebhookSignature.SignatureHeader} headers.",
        WebhookVerificationResult.InvalidTimestamp => $"{WebhookSignature.TimestampHeader} must be unix seconds.",
        WebhookVerificationResult.TimestampOutsideTolerance => $"The timestamp is more than {tolerance.TotalMinutes:F0} minutes from server time; the delivery is treated as a replay.",
        _ => "The signature does not match the body.",
    };

    [LoggerMessage(EventId = 6010, Level = LogLevel.Warning, Message = "Rejected a webhook from {Provider}: no webhook secret is configured for it")]
    private static partial void LogNoSecret(ILogger logger, string provider);
}
