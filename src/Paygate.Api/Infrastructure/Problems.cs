using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Paygate.Application.Payments;
using Paygate.Domain.Common;
using Paygate.Domain.Payments;

namespace Paygate.Api.Infrastructure;

/// <summary>RFC 9457 problem responses with a stable machine-readable <c>code</c> extension.</summary>
public static class Problems
{
    public static string Title(int status) => ReasonPhrases.GetReasonPhrase(status);

    public static ProblemHttpResult Result(int status, string code, string detail) =>
        TypedResults.Problem(
            statusCode: status,
            title: Title(status),
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static ProblemHttpResult NotFound(string what) =>
        Result(StatusCodes.Status404NotFound, "not_found", $"{what} was not found.");
}

/// <summary>
/// Turns domain and application exceptions into problem responses inside the endpoint, so the
/// idempotency middleware sees (and stores) them like any other response.
/// </summary>
public sealed class ProblemMappingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (InvalidPaymentTransitionException ex)
        {
            return Problems.Result(StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }
        catch (DomainException ex)
        {
            return Problems.Result(StatusCodes.Status422UnprocessableEntity, ex.Code, ex.Message);
        }
        catch (ConcurrencyConflictException ex)
        {
            return Problems.Result(StatusCodes.Status409Conflict, "concurrent_modification", ex.Message);
        }
        catch (RefundFailedException ex)
        {
            context.HttpContext.Response.Headers.RetryAfter = "5";
            return Problems.Result(StatusCodes.Status503ServiceUnavailable, "provider_unavailable",
                $"{ex.Message} Retry with the same Idempotency-Key: the provider receives the same refund key and cannot refund twice.");
        }
    }
}
