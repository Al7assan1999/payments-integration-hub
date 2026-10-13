using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Api.Data;
using Payments.Api.Idempotency;
using Payments.Api.Integration;
using Payments.Webhooks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Payments")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<PartnerOptions>().BindConfiguration(PartnerOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<MerchantOptions>().BindConfiguration(MerchantOptions.Section).ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddHttpClient<PartnerClient>((sp, http) =>
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PartnerOptions>>().Value.BaseUrl.TrimEnd('/') + "/"))
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 3;                      // exponential backoff with jitter
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // must be >= 2x attempt timeout
    });
builder.Services.AddHttpClient<MerchantNotifier>(http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<OutboxProcessor>();
if (builder.Configuration.GetValue("Outbox:Enabled", true))
    builder.Services.AddHostedService<OutboxWorker>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();

app.UseExceptionHandler();
app.MapOpenApi();
app.MapGet("/health", () => Results.Ok("healthy"));

var payments = app.MapGroup("/api/payments");

payments.MapPost("/", async (CreatePaymentRequest request, PaymentsDbContext db, TimeProvider time) =>
{
    var errors = request.Validate();
    if (errors.Count > 0) return (IResult)TypedResults.ValidationProblem(errors);

    var now = time.GetUtcNow().UtcDateTime;
    var payment = new Payment
    {
        MerchantReference = request.MerchantReference,
        Amount = request.Amount,
        Currency = request.Currency.ToUpperInvariant(),
        CreatedAt = now,
        UpdatedAt = now,
    };
    db.Payments.Add(payment);
    db.Enqueue(OutboxType.SubmitToPartner, payment.Id, now); // same transaction as the payment
    await db.SaveChangesAsync();

    return TypedResults.Created($"/api/payments/{payment.Id}", PaymentDto.From(payment));
})
.AddEndpointFilter<IdempotencyFilter<CreatePaymentRequest>>()
.WithSummary("Create a payment (requires an Idempotency-Key header)");

payments.MapGet("/{id:guid}", async (Guid id, PaymentsDbContext db) =>
    await db.Payments.FindAsync(id) is { } p ? Results.Ok(PaymentDto.From(p)) : Results.NotFound());

payments.MapGet("/", async (PaymentsDbContext db) =>
    (await db.Payments.OrderByDescending(p => p.CreatedAt).Take(50).ToListAsync()).Select(PaymentDto.From));

// Partner bank callback. The raw body is read before parsing because the signature covers the exact bytes.
app.MapPost("/webhooks/partner", async (HttpRequest http, PaymentsDbContext db, IOptions<PartnerOptions> options, TimeProvider time, ILogger<Program> logger) =>
{
    using var reader = new StreamReader(http.Body);
    var body = await reader.ReadToEndAsync();
    if (!WebhookSignature.Verify(options.Value.WebhookSecret, body, http.Headers[WebhookSignature.Header], time.GetUtcNow()))
    {
        logger.LogWarning("Rejected partner webhook with an invalid or expired signature");
        return Results.Unauthorized();
    }

    var callback = JsonSerializer.Deserialize<PartnerCallback>(body, JsonSerializerOptions.Web);
    if (callback is null) return Results.BadRequest();

    var payment = await db.Payments.FindAsync(callback.PaymentId);
    if (payment is null) return Results.NotFound();
    if (payment.IsFinal) return Results.Ok(); // duplicate delivery: already applied

    var now = time.GetUtcNow().UtcDateTime;
    payment.PartnerReference ??= callback.PartnerReference;
    payment.Status = callback.Status == PartnerCallback.Approved ? PaymentStatus.Succeeded : PaymentStatus.Failed;
    payment.FailureReason = payment.Status == PaymentStatus.Failed ? callback.Reason ?? "Declined by partner" : null;
    payment.UpdatedAt = now;
    db.EnqueueMerchantEvent(payment, now);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.Run();

public record CreatePaymentRequest(string MerchantReference, decimal Amount, string Currency)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(MerchantReference) || MerchantReference.Length > 64)
            errors[nameof(MerchantReference)] = ["Required, at most 64 characters."];
        if (Amount <= 0 || Amount > 1_000_000 || decimal.Round(Amount, 2) != Amount)
            errors[nameof(Amount)] = ["Must be positive, at most 1,000,000, with at most 2 decimals."];
        if (Currency is not { Length: 3 } || !Currency.All(char.IsLetter))
            errors[nameof(Currency)] = ["Must be a 3-letter ISO code."];
        return errors;
    }
}

public record PaymentDto(Guid Id, string MerchantReference, decimal Amount, string Currency, PaymentStatus Status,
    string? PartnerReference, string? FailureReason, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static PaymentDto From(Payment p) =>
        new(p.Id, p.MerchantReference, p.Amount, p.Currency, p.Status, p.PartnerReference, p.FailureReason, p.CreatedAt, p.UpdatedAt);
}

public partial class Program;
