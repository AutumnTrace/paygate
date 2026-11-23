using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Paygate.Application.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>How far a webhook's timestamp may be from our clock. Older deliveries are rejected as replays.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Shared secrets per provider. More than one is allowed so a secret can be rotated without
    /// downtime: add the new one, let the provider switch, then remove the old one.
    /// </summary>
    public IDictionary<string, string[]> Secrets { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
}

public enum WebhookVerificationResult
{
    Valid,
    MissingHeaders,
    InvalidTimestamp,
    TimestampOutsideTolerance,
    InvalidSignature,
    NoSecretConfigured,
}

/// <summary>
/// Webhook signatures: <c>Webhook-Signature: v1=&lt;hex HMAC-SHA256(secret, "{timestamp}.{raw body}")&gt;</c>.
/// Signing the timestamp together with the body means an attacker cannot replay an old body with a
/// fresh timestamp, and the tolerance window bounds how long a captured delivery stays usable.
/// </summary>
public static class WebhookSignature
{
    public const string TimestampHeader = "Webhook-Timestamp";
    public const string SignatureHeader = "Webhook-Signature";
    public const string Scheme = "v1";

    /// <summary>The value a provider puts in <see cref="SignatureHeader"/>.</summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body) =>
        $"{Scheme}={Convert.ToHexStringLower(Compute(secret, timestamp, body))}";

    internal static byte[] Compute(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "."));
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }
}

public sealed class WebhookVerifier(IOptionsMonitor<WebhookOptions> options, TimeProvider time)
{
    public WebhookVerificationResult Verify(string provider, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body)
    {
        if (!options.CurrentValue.Secrets.TryGetValue(provider, out var secrets) || secrets.Length == 0 || secrets.All(string.IsNullOrEmpty))
        {
            return WebhookVerificationResult.NoSecretConfigured;
        }

        if (string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return WebhookVerificationResult.MissingHeaders;
        }

        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return WebhookVerificationResult.InvalidTimestamp;
        }

        var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(timestamp, 0, 253402300799));
        if (age.Duration() > options.CurrentValue.Tolerance)
        {
            return WebhookVerificationResult.TimestampOutsideTolerance;
        }

        // Compare every presented signature with every active secret, in constant time and without
        // stopping early, so timing reveals neither which secret matched nor how many bytes did.
        var presented = ParseSignatures(signatureHeader);
        var valid = false;
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
        {
            var expected = WebhookSignature.Compute(secret, timestamp, body);
            foreach (var candidate in presented)
            {
                valid |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        return valid ? WebhookVerificationResult.Valid : WebhookVerificationResult.InvalidSignature;
    }

    /// <summary>Accepts <c>v1=abc</c>, or several separated by commas or spaces during secret rotation.</summary>
    private static List<byte[]> ParseSignatures(string header)
    {
        var signatures = new List<byte[]>();
        foreach (var part in header.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !part.AsSpan(0, separator).SequenceEqual(WebhookSignature.Scheme))
            {
                continue;
            }

            var hex = part[(separator + 1)..];
            if (hex.Length == SHA256.HashSizeInBytes * 2 && hex.All(Uri.IsHexDigit))
            {
                signatures.Add(Convert.FromHexString(hex));
            }
        }

        return signatures;
    }
}
