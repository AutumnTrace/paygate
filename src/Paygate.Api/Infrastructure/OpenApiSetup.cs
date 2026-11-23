using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Paygate.Api.Auth;
using Paygate.Api.Idempotency;

namespace Paygate.Api.Infrastructure;

public static class OpenApiSetup
{
    public static IServiceCollection AddPaygateOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "paygate API",
                    Version = "v1",
                    Description =
                        "Payment orchestration gateway: deposits and payouts routed across PSPs with health-aware failover. " +
                        "Authenticate with the X-Api-Key header. Every POST that moves money needs an Idempotency-Key header. " +
                        "Amounts are integers in minor units (2500 = 25.00 EUR).",
                    License = new OpenApiLicense { Name = "MIT", Url = new Uri("https://github.com/AutumnTrace/paygate/blob/main/LICENSE") },
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[ApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyDefaults.HeaderName,
                    Description = "Per-operator API key. Only its SHA-256 hash is stored server-side.",
                };
                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(ApiKeyDefaults.Scheme, document)] = [],
                });
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequiresIdempotencyKeyAttribute>().Any())
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyMiddleware.HeaderName,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "Unique key for this operation (a UUID works). Retrying with the same key and body replays the original response.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 255 },
                    });
                }

                return Task.CompletedTask;
            });
        });
}
