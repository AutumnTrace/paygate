using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Paygate.Api.Auth;
using Paygate.Api.Endpoints;
using Paygate.Api.Idempotency;
using Paygate.Api.Infrastructure;
using Paygate.Application;
using Paygate.Infrastructure;
using Paygate.Infrastructure.Persistence;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Logging: human-readable in development, compact JSON everywhere else.
builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("service", "paygate");

    if (builder.Environment.IsDevelopment())
    {
        logger.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    }
    else
    {
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

// Application, providers and persistence.
builder.Services
    .AddSimulatedProviders(builder.Configuration)
    .AddPaygateApplication(builder.Configuration)
    .AddPaygatePersistence(builder.Configuration);

// HTTP surface.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddValidation();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
builder.Services.AddPaygateOpenApi();
builder.Services.AddPaygateRateLimiting(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<PaygateDbContext>("database", tags: [HealthChecks.ReadyTag]);

// Authentication: one API key per operator, hashed at rest.
builder.Services.AddOptions<ApiClientsOptions>().Bind(builder.Configuration.GetSection(ApiClientsOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ApiClientsOptions>, ApiClientsOptionsValidator>();
builder.Services.AddAuthentication(ApiKeyDefaults.Scheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyDefaults.MerchantPolicy, policy => policy.RequireClaim(ApiKeyDefaults.MerchantIdClaim))
    .AddPolicy(ApiKeyDefaults.AdminPolicy, policy => policy.RequireRole(nameof(ApiClientRole.Admin)));

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<IdempotencyMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("paygate API"));
}

var v1 = app.MapGroup("/v1")
    .AddEndpointFilter<ProblemMappingFilter>()
    .RequireRateLimiting(RateLimitPolicies.PerClient)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

v1.MapPaymentEndpoints();
v1.MapProviderEndpoints();

app.MapPaygateHealthChecks();
app.MapGet("/", () => Results.Redirect(app.Environment.IsDevelopment() ? "/scalar/v1" : "/health/ready")).ExcludeFromDescription();

await app.Services.InitializePaygateDatabaseAsync();
await app.RunAsync();
