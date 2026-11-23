using System.Security.Cryptography;
using System.Text;

namespace Paygate.Application.Idempotency;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a key and its stored response are kept. Clients must not reuse keys within this window.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an in-flight request holds its key. If the process dies mid-request, the key becomes
    /// usable again after this, instead of being blocked until it expires.
    /// </summary>
    public TimeSpan InFlightLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest request body that is fingerprinted. Larger requests are rejected with 413.</summary>
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>The scope and fingerprint of one idempotent request.</summary>
public sealed record IdempotencyRequest(string MerchantId, string Key, string Fingerprint, string Method, string Path);

/// <summary>The HTTP response stored for replay.</summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, string? Location, byte[] Body);

public enum IdempotencyOutcome
{
    /// <summary>First time this key is seen: the caller now owns it and must complete or release it.</summary>
    Proceed,

    /// <summary>The key completed before with the same request: return the stored response.</summary>
    Replay,

    /// <summary>Another request with this key is still running.</summary>
    InFlight,

    /// <summary>The key was used before with a different request.</summary>
    Mismatch,
}

public sealed record IdempotencyDecision(IdempotencyOutcome Outcome, StoredResponse? Response = null)
{
    public static readonly IdempotencyDecision Proceed = new(IdempotencyOutcome.Proceed);
    public static readonly IdempotencyDecision InFlight = new(IdempotencyOutcome.InFlight);
    public static readonly IdempotencyDecision Mismatch = new(IdempotencyOutcome.Mismatch);
}

/// <summary>
/// Durable store for Idempotency-Key semantics. Correctness under concurrency comes from the
/// database's unique constraint on (merchant, key), not from application locks, so it holds
/// across any number of gateway instances.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyDecision> BeginAsync(IdempotencyRequest request, CancellationToken cancellationToken);

    /// <summary>Stores the final response so retries replay it.</summary>
    Task CompleteAsync(string merchantId, string key, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Gives the key back (the request failed in a retryable way), so the client can retry with it.</summary>
    Task ReleaseAsync(string merchantId, string key, CancellationToken cancellationToken);

    /// <summary>Deletes expired keys. Returns how many were removed.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

public static class RequestFingerprint
{
    /// <summary>SHA-256 over method, path and the exact body bytes, as lower-case hex.</summary>
    public static string Compute(string method, string path, ReadOnlySpan<byte> body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}\n{path}\n"));
        hash.AppendData(body);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
