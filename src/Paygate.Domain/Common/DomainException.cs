namespace Paygate.Domain.Common;

/// <summary>
/// A business rule was violated. <see cref="Code"/> is a stable, machine-readable identifier
/// that the API surfaces to clients (for example <c>refund_exceeds_captured_amount</c>).
/// </summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
