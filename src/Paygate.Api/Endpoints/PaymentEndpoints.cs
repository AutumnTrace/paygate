using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Paygate.Api.Auth;
using Paygate.Api.Contracts;
using Paygate.Api.Idempotency;
using Paygate.Api.Infrastructure;
using Paygate.Application.Payments;
using Paygate.Domain.Payments;

namespace Paygate.Api.Endpoints;

public static class PaymentEndpoints
{
    public static RouteGroupBuilder MapPaymentEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);

        var payments = v1.MapGroup("/payments")
            .WithTags("Payments")
            .RequireAuthorization(ApiKeyDefaults.MerchantPolicy);

        payments.MapPost("/", CreateDepositAsync)
            .RequireIdempotencyKey()
            .WithName("CreateDeposit")
            .WithSummary("Create a deposit")
            .WithDescription("Routes the deposit to the best eligible provider and fails over on technical errors. Card deposits need a paymentToken. Requires an Idempotency-Key.");

        payments.MapGet("/{id:guid}", GetAsync)
            .WithName("GetPayment")
            .WithSummary("Get a deposit or payout");

        payments.MapGet("/", ListAsync)
            .WithName("ListPayments")
            .WithSummary("List payments, newest first")
            .WithDescription("Keyset pagination: pass nextCreatedBefore from the previous page as createdBefore.");

        payments.MapPost("/{id:guid}/refunds", RefundAsync)
            .RequireIdempotencyKey()
            .WithName("RefundPayment")
            .WithSummary("Refund a captured deposit, fully or partially")
            .WithDescription("Refunds always go to the provider that captured the payment. Requires an Idempotency-Key.");

        v1.MapPost("/payouts", CreatePayoutAsync)
            .WithTags("Payouts")
            .RequireAuthorization(ApiKeyDefaults.MerchantPolicy)
            .RequireIdempotencyKey()
            .WithName("CreatePayout")
            .WithSummary("Create a payout")
            .WithDescription("A payout that times out at the provider stays pending and is not failed over, so it can never be paid twice. Requires an Idempotency-Key.");

        return v1;
    }

    private static async Task<Results<Created<PaymentResponse>, ValidationProblem>> CreateDepositAsync(
        CreateDepositRequest request, ClaimsPrincipal user, PaymentService service, CancellationToken cancellationToken)
    {
        if (request.Method == PaymentMethod.Card && string.IsNullOrEmpty(request.PaymentToken))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { ["paymentToken"] = ["Card deposits need a paymentToken from the hosted payment fields."] });
        }

        var payment = await service.CreateAsync(
            new CreatePaymentCommand(user.MerchantId(), PaymentKind.Deposit, request.Method, request.Amount, request.Currency, request.Country, request.CustomerId, request.PaymentToken),
            cancellationToken);

        return TypedResults.Created($"/v1/payments/{payment.Id}", PaymentResponse.From(payment));
    }

    private static async Task<Created<PaymentResponse>> CreatePayoutAsync(
        CreatePayoutRequest request, ClaimsPrincipal user, PaymentService service, CancellationToken cancellationToken)
    {
        var payout = await service.CreateAsync(
            new CreatePaymentCommand(user.MerchantId(), PaymentKind.Payout, request.Method, request.Amount, request.Currency, request.Country, request.CustomerId, request.DestinationToken),
            cancellationToken);

        return TypedResults.Created($"/v1/payments/{payout.Id}", PaymentResponse.From(payout));
    }

    private static async Task<Results<Ok<PaymentResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ClaimsPrincipal user, PaymentService service, CancellationToken cancellationToken)
    {
        var payment = await service.GetAsync(user.MerchantId(), id, cancellationToken);
        return payment is null ? Problems.NotFound($"Payment {id}") : TypedResults.Ok(PaymentResponse.From(payment));
    }

    private static async Task<Ok<PaymentListResponse>> ListAsync(
        ClaimsPrincipal user, PaymentService service, CancellationToken cancellationToken, int limit = 20, DateTimeOffset? createdBefore = null)
    {
        var page = await service.ListAsync(user.MerchantId(), createdBefore, limit, cancellationToken);
        var next = page.Count == Math.Clamp(limit, 1, 100) ? page[^1].CreatedAt : (DateTimeOffset?)null;
        return TypedResults.Ok(new PaymentListResponse(page.Select(PaymentResponse.From).ToList(), next));
    }

    private static async Task<Results<Created<RefundResultResponse>, ProblemHttpResult>> RefundAsync(
        Guid id, CreateRefundRequest request, HttpContext http, ClaimsPrincipal user, PaymentService service)
    {
        var key = http.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
        var result = await service.RefundAsync(new RefundCommand(user.MerchantId(), id, request.Amount, request.Reason, key), http.RequestAborted);

        if (result is not { } refunded)
        {
            return Problems.NotFound($"Payment {id}");
        }

        return TypedResults.Created(
            $"/v1/payments/{id}",
            new RefundResultResponse(RefundResponse.From(refunded.Refund), PaymentResponse.From(refunded.Payment)));
    }
}
