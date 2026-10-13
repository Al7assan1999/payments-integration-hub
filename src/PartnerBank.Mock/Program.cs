// A deliberately unreliable partner bank, so the Payments API's retry, idempotency and webhook handling
// get exercised for real:
//   - fails a configurable share of requests with 503 (Partner:FailureRate)
//   - deduplicates by Idempotency-Key, like a real payment provider
//   - settles asynchronously and calls back with a signed webhook (retrying if the callback fails)
//   - declines any amount ending in .99, so tests can force a decline deterministically
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Payments.Webhooks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

var failureRate = app.Configuration.GetValue("Partner:FailureRate", 0.3);
var secret = app.Configuration["Partner:WebhookSecret"] ?? "partner-dev-secret-change-me";
var settleDelay = TimeSpan.FromMilliseconds(app.Configuration.GetValue("Partner:SettleDelayMs", 1500));
var accepted = new ConcurrentDictionary<string, PartnerPaymentAccepted>();
var stats = new ConcurrentDictionary<string, int>();

app.MapGet("/health", () => "healthy");
app.MapGet("/stats", () => stats);

app.MapPost("/payments", (PartnerPaymentRequest request, HttpRequest http, IHttpClientFactory clients, ILogger<Program> logger) =>
{
    stats.AddOrUpdate("requests", 1, (_, n) => n + 1);

    if (Random.Shared.NextDouble() < failureRate)
    {
        stats.AddOrUpdate("injectedFailures", 1, (_, n) => n + 1);
        return Results.StatusCode(503);
    }

    var key = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrEmpty(key)) return Results.BadRequest("Idempotency-Key header is required.");
    if (request.Amount <= 0) return Results.BadRequest("Amount must be positive.");

    var isNew = false;
    var result = accepted.GetOrAdd(key, _ =>
    {
        isNew = true;
        return new PartnerPaymentAccepted($"PB-{Guid.NewGuid():N}"[..15].ToUpperInvariant());
    });
    if (!isNew)
    {
        stats.AddOrUpdate("deduplicated", 1, (_, n) => n + 1);
        return Results.Accepted(value: result);
    }

    _ = Task.Run(async () =>
    {
        await Task.Delay(settleDelay);
        var declined = request.Amount % 1 == 0.99m;
        var callback = new PartnerCallback(result.PartnerReference, request.PaymentId,
            declined ? PartnerCallback.Declined : PartnerCallback.Approved,
            declined ? "Insufficient funds" : null);
        var body = JsonSerializer.Serialize(callback, JsonSerializerOptions.Web);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, request.CallbackUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                message.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(secret, body, DateTimeOffset.UtcNow));
                using var response = await clients.CreateClient().SendAsync(message);
                if (response.IsSuccessStatusCode) return;
                logger.LogWarning("Callback for {Payment} returned {Status}", request.PaymentId, (int)response.StatusCode);
            }
            catch (HttpRequestException e)
            {
                logger.LogWarning("Callback for {Payment} failed: {Error}", request.PaymentId, e.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    });

    return Results.Accepted(value: result);
});

app.Run();
