using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Paygate.Application.Webhooks;

namespace Paygate.UnitTests.Webhooks;

public sealed class WebhookVerifierTests
{
    private const string Secret = "whsec_test_0123456789abcdef";
    private const string RotatedSecret = "whsec_test_new_fedcba9876543210";
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"payment.captured","providerReference":"osp_1"}""");

    private readonly FakeTimeProvider _time = new(Now);

    private WebhookVerifier Verifier(params string[] secrets)
    {
        var options = new WebhookOptions();
        options.Secrets["osprey"] = secrets.Length == 0 ? [Secret] : secrets;
        return new WebhookVerifier(new StaticOptionsMonitor(options), _time);
    }

    private static string Timestamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_correctly_signed_delivery_is_valid()
    {
        var signature = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);

        Assert.Equal(WebhookVerificationResult.Valid, Verifier().Verify("osprey", Timestamp(Now), signature, Body));
        Assert.Matches("^v1=[0-9a-f]{64}$", signature);
    }

    [Fact]
    public void Signature_matches_the_documented_openssl_recipe()
    {
        // printf '%s.%s' "$ts" "$body" | openssl dgst -sha256 -hmac "$secret"
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{Now.ToUnixTimeSeconds()}.{Encoding.UTF8.GetString(Body)}")));

        Assert.Equal($"v1={expected}", WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body));
    }

    [Fact]
    public void A_tampered_body_is_rejected()
    {
        var signature = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        var tampered = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"payment.captured","providerReference":"osp_2"}""");

        Assert.Equal(WebhookVerificationResult.InvalidSignature, Verifier().Verify("osprey", Timestamp(Now), signature, tampered));
    }

    [Fact]
    public void A_signature_made_with_another_secret_is_rejected()
    {
        var signature = WebhookSignature.Sign("whsec_someone_else_000000", Now.ToUnixTimeSeconds(), Body);

        Assert.Equal(WebhookVerificationResult.InvalidSignature, Verifier().Verify("osprey", Timestamp(Now), signature, Body));
    }

    [Fact]
    public void Changing_the_timestamp_breaks_the_signature()
    {
        // An attacker cannot refresh an old delivery's timestamp, because the timestamp is signed too.
        var old = Now.AddMinutes(-30);
        var signature = WebhookSignature.Sign(Secret, old.ToUnixTimeSeconds(), Body);

        Assert.Equal(WebhookVerificationResult.InvalidSignature, Verifier().Verify("osprey", Timestamp(Now), signature, Body));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Deliveries_outside_the_tolerance_window_are_rejected_as_replays(int offsetSeconds)
    {
        var at = Now.AddSeconds(offsetSeconds);
        var signature = WebhookSignature.Sign(Secret, at.ToUnixTimeSeconds(), Body);

        Assert.Equal(WebhookVerificationResult.TimestampOutsideTolerance, Verifier().Verify("osprey", Timestamp(at), signature, Body));
    }

    [Theory]
    [InlineData(-299)]
    [InlineData(299)]
    public void Deliveries_inside_the_tolerance_window_are_accepted(int offsetSeconds)
    {
        var at = Now.AddSeconds(offsetSeconds);
        var signature = WebhookSignature.Sign(Secret, at.ToUnixTimeSeconds(), Body);

        Assert.Equal(WebhookVerificationResult.Valid, Verifier().Verify("osprey", Timestamp(at), signature, Body));
    }

    [Theory]
    [InlineData(null, "v1=00")]
    [InlineData("1763640000", null)]
    [InlineData("", "")]
    public void Missing_headers_are_rejected(string? timestamp, string? signature) =>
        Assert.Equal(WebhookVerificationResult.MissingHeaders, Verifier().Verify("osprey", timestamp, signature, Body));

    [Theory]
    [InlineData("yesterday")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void Malformed_timestamps_are_rejected(string timestamp) =>
        Assert.Equal(WebhookVerificationResult.InvalidTimestamp, Verifier().Verify("osprey", timestamp, "v1=" + new string('0', 64), Body));

    [Theory]
    [InlineData("sha256=abc")]
    [InlineData("v1=not-hex")]
    [InlineData("v1=")]
    [InlineData("v2=0000000000000000000000000000000000000000000000000000000000000000")]
    public void Malformed_signatures_are_rejected(string signature) =>
        Assert.Equal(WebhookVerificationResult.InvalidSignature, Verifier().Verify("osprey", Timestamp(Now), signature, Body));

    [Fact]
    public void Either_secret_is_accepted_while_a_secret_is_being_rotated()
    {
        var verifier = Verifier(RotatedSecret, Secret);

        var oldSigned = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        var newSigned = WebhookSignature.Sign(RotatedSecret, Now.ToUnixTimeSeconds(), Body);

        Assert.Equal(WebhookVerificationResult.Valid, verifier.Verify("osprey", Timestamp(Now), oldSigned, Body));
        Assert.Equal(WebhookVerificationResult.Valid, verifier.Verify("osprey", Timestamp(Now), newSigned, Body));
    }

    [Fact]
    public void A_header_may_carry_several_signatures_and_one_valid_is_enough()
    {
        var header = $"v1={new string('a', 64)}, {WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body)}";

        Assert.Equal(WebhookVerificationResult.Valid, Verifier().Verify("osprey", Timestamp(Now), header, Body));
    }

    [Fact]
    public void Providers_without_a_secret_can_never_be_verified() =>
        Assert.Equal(
            WebhookVerificationResult.NoSecretConfigured,
            Verifier().Verify("heron", Timestamp(Now), WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body), Body));

    private sealed class StaticOptionsMonitor(WebhookOptions value) : IOptionsMonitor<WebhookOptions>
    {
        public WebhookOptions CurrentValue => value;

        public WebhookOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<WebhookOptions, string?> listener) => null;
    }
}
