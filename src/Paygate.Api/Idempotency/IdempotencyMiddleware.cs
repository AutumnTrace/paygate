using Microsoft.Extensions.Options;
using Paygate.Api.Auth;
using Paygate.Api.Infrastructure;
using Paygate.Application.Idempotency;
using Paygate.Application.Observability;

namespace Paygate.Api.Idempotency;

/// <summary>Marks an endpoint as requiring an <c>Idempotency-Key</c> header.</summary>
public sealed class RequiresIdempotencyKeyAttribute : Attribute;

public static class IdempotencyEndpointExtensions
{
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresIdempotencyKeyAttribute());
}

/// <summary>
/// Idempotency-Key semantics for unsafe requests, in the style of the IETF httpapi draft:
/// <list type="bullet">
/// <item>first request: runs, and its final response is stored;</item>
/// <item>same key, same request: the stored response is replayed with <c>Idempotent-Replayed: true</c>;</item>
/// <item>same key, different request: <c>422</c>;</item>
/// <item>same key while the first is still running: <c>409</c> with <c>Retry-After</c>.</item>
/// </list>
/// Final responses (2xx, and 4xx business errors such as 422) are stored. Validation errors (400),
/// conflicts (409), rate limiting (429) and server errors release the key, so a corrected or later
/// retry can use it.
/// Keys are scoped to the authenticated merchant.
/// </summary>
public sealed partial class IdempotencyMiddleware(
    RequestDelegate next,
    IIdempotencyStore store,
    IOptions<IdempotencyOptions> options,
    PaygateMetrics metrics,
    IProblemDetailsService problems,
    ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    private const int MaxKeyLength = 255;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresIdempotencyKeyAttribute>() is null
            || context.User.FindFirst(ApiKeyDefaults.MerchantIdClaim) is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var key = context.Request.Headers[HeaderName].ToString();
        if (key.Length == 0)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "idempotency_key_required",
                $"POST requests must carry an {HeaderName} header (a unique value such as a UUID) so they can be retried safely.").ConfigureAwait(false);
            return;
        }

        if (key.Length > MaxKeyLength || !key.All(c => c is >= '!' and <= '~'))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "invalid_idempotency_key",
                $"{HeaderName} must be 1 to {MaxKeyLength} visible ASCII characters.").ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large",
                $"Idempotent requests are limited to {options.Value.MaxRequestBodyBytes} bytes.").ConfigureAwait(false);
            return;
        }

        var merchantId = context.User.MerchantId();
        var request = new IdempotencyRequest(
            merchantId,
            key,
            RequestFingerprint.Compute(context.Request.Method, context.Request.Path.Value ?? "/", body),
            context.Request.Method,
            context.Request.Path.Value ?? "/");

        var decision = await store.BeginAsync(request, context.RequestAborted).ConfigureAwait(false);
        switch (decision.Outcome)
        {
            case IdempotencyOutcome.Replay:
                metrics.IdempotentRequest("replayed");
                await ReplayAsync(context, decision.Response!).ConfigureAwait(false);
                return;

            case IdempotencyOutcome.InFlight:
                metrics.IdempotentRequest("in_flight");
                context.Response.Headers.RetryAfter = "1";
                await WriteProblemAsync(context, StatusCodes.Status409Conflict, "idempotency_key_in_use",
                    "A request with this Idempotency-Key is still being processed. Retry shortly to get its result.").ConfigureAwait(false);
                return;

            case IdempotencyOutcome.Mismatch:
                metrics.IdempotentRequest("mismatch");
                await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "idempotency_key_reused",
                    "This Idempotency-Key was already used with a different request. Use a new key for a new request.").ConfigureAwait(false);
                return;
        }

        metrics.IdempotentRequest("new");
        await ExecuteAndStoreAsync(context, merchantId, key).ConfigureAwait(false);
    }

    private async Task ExecuteAndStoreAsync(HttpContext context, string merchantId, string key)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            await store.ReleaseAsync(merchantId, key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        context.Response.Body = originalBody;
        var status = context.Response.StatusCode;

        // Store before the client sees the response, so an immediate retry is a replay, not a 409.
        // CancellationToken.None: the outcome must be recorded even if the client has gone away.
        if (IsFinal(status))
        {
            await store.CompleteAsync(
                merchantId,
                key,
                new StoredResponse(status, context.Response.ContentType, context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null, buffer.ToArray()),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await store.ReleaseAsync(merchantId, key, CancellationToken.None).ConfigureAwait(false);
            LogReleased(logger, status);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsFinal(int status) =>
        status is < 500 and not (StatusCodes.Status400BadRequest or StatusCodes.Status409Conflict or StatusCodes.Status429TooManyRequests);

    private async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        var limit = options.Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            return null;
        }

        context.Request.EnableBuffering(limit, limit);
        using var copy = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > limit)
            {
                return null;
            }

            copy.Write(chunk, 0, read);
        }

        context.Request.Body.Position = 0;
        return copy.ToArray();
    }

    private static async Task ReplayAsync(HttpContext context, StoredResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.Headers[ReplayedHeaderName] = "true";
        if (response.ContentType is not null)
        {
            context.Response.ContentType = response.ContentType;
        }

        if (response.Location is not null)
        {
            context.Response.Headers.Location = response.Location;
        }

        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = status,
                Title = Problems.Title(status),
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        }).AsTask();
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Idempotency key released after a {StatusCode} response; the client may retry with it")]
    private static partial void LogReleased(ILogger logger, int statusCode);
}
