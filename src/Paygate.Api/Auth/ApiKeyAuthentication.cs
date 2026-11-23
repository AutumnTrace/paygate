using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Paygate.Api.Auth;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string MerchantIdClaim = "merchant_id";
    public const string MerchantPolicy = "merchant";
    public const string AdminPolicy = "admin";
}

public enum ApiClientRole
{
    Merchant,
    Admin,
}

/// <summary>An operator (merchant) or an operations user. Only the SHA-256 of the key is stored.</summary>
public sealed class ApiClient
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ApiClientRole Role { get; set; } = ApiClientRole.Merchant;

    /// <summary>Lower-case hex SHA-256 of the API key.</summary>
    public string KeySha256 { get; set; } = string.Empty;
}

public sealed class ApiClientsOptions
{
    public const string SectionName = "ApiKeys";

    public IList<ApiClient> Clients { get; } = [];
}

public sealed class ApiClientsOptionsValidator : IValidateOptions<ApiClientsOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiClientsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        foreach (var client in options.Clients)
        {
            if (string.IsNullOrWhiteSpace(client.Id) || client.Id.Length > 64)
            {
                errors.Add("Every API client needs an Id of at most 64 characters.");
            }

            if (client.KeySha256.Length != 64 || !client.KeySha256.All(Uri.IsHexDigit))
            {
                errors.Add($"API client '{client.Id}': KeySha256 must be 64 hex characters (the SHA-256 of the key, never the key itself).");
            }
        }

        if (options.Clients.GroupBy(c => c.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            errors.Add("API client ids must be unique.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>
/// Authenticates <c>X-Api-Key</c>. The presented key is hashed with SHA-256 and compared with every
/// stored hash using <see cref="CryptographicOperations.FixedTimeEquals"/>, without stopping at the
/// first match, so response timing reveals nothing about which bytes or which client matched.
/// API keys are long random strings, so an unsalted fast hash is the right tool (as for access tokens);
/// password hashing (PBKDF2, Argon2) exists to protect low-entropy secrets.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ApiClientsOptions> apiKeys,
    IProblemDetailsService problems)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const int MaxKeyLength = 256;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values) || string.IsNullOrEmpty(values.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = values.ToString();
        if (presented.Length > MaxKeyLength)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedHash);

        ApiClient? match = null;
        Span<byte> storedHash = stackalloc byte[SHA256.HashSizeInBytes];
        foreach (var client in apiKeys.CurrentValue.Clients)
        {
            if (Convert.FromHexString(client.KeySha256, storedHash, out _, out _) == System.Buffers.OperationStatus.Done
                && CryptographicOperations.FixedTimeEquals(presentedHash, storedHash))
            {
                match ??= client;
            }
        }

        if (match is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, match.Id),
            new(ClaimTypes.Name, match.Name),
            new(ClaimTypes.Role, match.Role.ToString()),
        };
        if (match.Role == ApiClientRole.Merchant)
        {
            claims.Add(new Claim(ApiKeyDefaults.MerchantIdClaim, match.Id));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyDefaults.Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyDefaults.Scheme)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.HeaderName}\"";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = $"Send a valid API key in the {ApiKeyDefaults.HeaderName} header.",
                Extensions = { ["code"] = "unauthorized" },
            },
        }).ConfigureAwait(false);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "This API key is not allowed to call this endpoint.",
                Extensions = { ["code"] = "forbidden" },
            },
        }).ConfigureAwait(false);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated merchant. Every payment query is scoped by it.</summary>
    public static string MerchantId(this ClaimsPrincipal user) =>
        user?.FindFirstValue(ApiKeyDefaults.MerchantIdClaim)
        ?? throw new InvalidOperationException("The request is not authenticated as a merchant.");
}
