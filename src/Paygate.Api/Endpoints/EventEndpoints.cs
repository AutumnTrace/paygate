using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Paygate.Api.Auth;
using Paygate.Api.Infrastructure;
using Paygate.Application.Events;
using Paygate.Infrastructure.Events;

namespace Paygate.Api.Endpoints;

public sealed record EventResponse(Guid Id, string Type, string Key, DateTimeOffset OccurredAt, JsonElement Data);

public static class EventEndpoints
{
    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);

        v1.MapGet("/admin/events", Recent)
            .WithTags("Admin")
            .RequireAuthorization(ApiKeyDefaults.AdminPolicy)
            .WithName("RecentEvents")
            .WithSummary("Recently published domain events (in-memory publisher only)")
            .WithDescription("With Events:Publisher=Kafka the events go to the payments.v1 topic instead, and this endpoint returns 404.");

        return v1;
    }

    private static Results<Ok<IReadOnlyList<EventResponse>>, ProblemHttpResult> Recent(IEventPublisher publisher, int limit = 20)
    {
        if (publisher is not InMemoryEventPublisher inMemory)
        {
            return Problems.Result(StatusCodes.Status404NotFound, "not_available", "Events are published to Kafka; consume the topic to see them.");
        }

        return TypedResults.Ok<IReadOnlyList<EventResponse>>(inMemory.Recent(limit)
            .Select(e => new EventResponse(e.Id, e.Type, e.Key, e.OccurredAt, JsonSerializer.Deserialize<JsonElement>(e.Payload)))
            .ToList());
    }
}
