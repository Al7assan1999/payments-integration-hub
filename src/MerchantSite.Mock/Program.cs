// Stands in for a merchant's server: verifies the Payments API's webhook signatures, ignores duplicate
// deliveries by EventId, and exposes what it received at GET /events for the end-to-end test.
// Fails the first delivery of every event (Merchant:FailFirstDelivery) to prove the API retries.
using System.Collections.Concurrent;
using System.Text.Json;
using Payments.Webhooks;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var secret = app.Configuration["Merchant:WebhookSecret"] ?? "merchant-dev-secret-change-me";
var failFirst = app.Configuration.GetValue("Merchant:FailFirstDelivery", true);
var events = new ConcurrentDictionary<Guid, MerchantEvent>();
var seen = new ConcurrentDictionary<Guid, int>();
var rejectedSignatures = 0;

app.MapGet("/health", () => "healthy");
app.MapGet("/events", () => new
{
    events = events.Values,
    deliveries = seen.Values.Sum(),
    rejectedSignatures,
});

app.MapPost("/webhooks/payments", async (HttpRequest http) =>
{
    using var reader = new StreamReader(http.Body);
    var body = await reader.ReadToEndAsync();
    if (!WebhookSignature.Verify(secret, body, http.Headers[WebhookSignature.Header], DateTimeOffset.UtcNow))
    {
        Interlocked.Increment(ref rejectedSignatures);
        return Results.Unauthorized();
    }

    var e = JsonSerializer.Deserialize<MerchantEvent>(body, JsonSerializerOptions.Web)!;
    var deliveries = seen.AddOrUpdate(e.EventId, 1, (_, n) => n + 1);
    if (failFirst && deliveries == 1) return Results.StatusCode(500); // simulate an outage on first try

    events.TryAdd(e.EventId, e); // idempotent: a redelivered event changes nothing
    return Results.Ok();
});

app.Run();
