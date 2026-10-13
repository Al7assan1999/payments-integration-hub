using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;
using Payments.Api.Data;
using Payments.Api.Integration;
using Payments.Webhooks;

namespace Payments.Tests;

/// <summary>
/// Runs the real API in-process on a throwaway SQLite database. The background worker is off; tests drive
/// the outbox explicitly. Outbound HTTP (partner and merchant) goes to a stub handler.
/// </summary>
public sealed class ApiFixture : IDisposable
{
    public const string PartnerSecret = "partner-test-secret-0123";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"payments-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public StubHandler Partner { get; } = new();
    public StubHandler Merchant { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public HttpClient Client { get; }

    public ApiFixture()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Payments", $"Data Source={_db};Pooling=False");
            b.UseSetting("Outbox:Enabled", "false");
            b.UseSetting("Partner:BaseUrl", "http://partner.test");
            b.UseSetting("Partner:WebhookSecret", PartnerSecret);
            b.UseSetting("Partner:CallbackUrl", "http://api.test/webhooks/partner");
            b.UseSetting("Merchant:WebhookUrl", "http://merchant.test/webhooks/payments");
            b.UseSetting("Merchant:WebhookSecret", "merchant-test-secret-0123");
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Time);
                // The resilience pipeline uses the same TimeProvider; with a fake clock its retry delays would
                // never elapse, so retry immediately in tests (the retry count and policy stay the same).
                s.PostConfigure<HttpStandardResilienceOptions>("PartnerClient-standard", o => o.Retry.Delay = TimeSpan.Zero);
                s.AddHttpClient<PartnerClient>().ConfigurePrimaryHttpMessageHandler(() => Partner);
                s.AddHttpClient<MerchantNotifier>().ConfigurePrimaryHttpMessageHandler(() => Merchant);
            });
        });
        Client = _factory.CreateClient();
    }

    public async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public void Dispose()
    {
        _factory.Dispose();
        File.Delete(_db);
    }
}

public sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
    public int Calls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Respond(request));
    }
}

public class ApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private Task<HttpResponseMessage> Create(string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return _api.Client.SendAsync(request);
    }

    private static object Order(decimal amount = 150m) => new { merchantReference = "ORDER-1", amount, currency = "SAR" };

    private Task<int> ProcessOutbox() => _api.Scoped(sp => sp.GetRequiredService<OutboxProcessor>().ProcessDueAsync(default));

    private Task<List<OutboxMessage>> Outbox(OutboxType type) =>
        _api.Scoped(sp => sp.GetRequiredService<PaymentsDbContext>().Outbox.Where(m => m.Type == type).ToListAsync());

    private Task<Payment> Payment(Guid id) => _api.Scoped(async sp => (await sp.GetRequiredService<PaymentsDbContext>().Payments.FindAsync(id))!);

    [Fact]
    public async Task Retrying_with_the_same_key_returns_the_original_payment()
    {
        var first = await Create("key-1", Order());
        var retry = await Create("key-1", Order());

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        var a = await first.Content.ReadFromJsonAsync<PaymentDto>(Json);
        var b = await retry.Content.ReadFromJsonAsync<PaymentDto>(Json);
        Assert.Equal(a!.Id, b!.Id);
        Assert.Single(await Outbox(OutboxType.SubmitToPartner)); // one payment, one submission
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_refused()
    {
        await Create("key-1", Order(150m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create("key-1", Order(999m))).StatusCode);
    }

    [Fact]
    public async Task Key_is_required_and_input_is_validated()
    {
        var noKey = await _api.Client.PostAsJsonAsync("/api/payments", Order());
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var invalid = await Create("key-2", new { merchantReference = "", amount = 10.001m, currency = "SA" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Amount", problem);
        Assert.Contains("Currency", problem);
    }

    [Fact]
    public async Task Outbox_submits_to_partner_and_backs_off_on_failure()
    {
        var payment = (await (await Create("key-1", Order())).Content.ReadFromJsonAsync<PaymentDto>(Json))!;
        _api.Partner.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await ProcessOutbox();

        var message = Assert.Single(await Outbox(OutboxType.SubmitToPartner));
        Assert.Equal(1, message.Attempts);
        Assert.Null(message.ProcessedAt);
        Assert.True(_api.Partner.Calls > 1, "the resilience handler should have retried the call");
        Assert.Equal(PaymentStatus.Pending, (await Payment(payment.Id)).Status);

        // Not due again until the backoff has passed.
        Assert.Equal(0, await ProcessOutbox());

        _api.Partner.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new PartnerPaymentAccepted("PB-123")) };
        _api.Time.Advance(OutboxProcessor.Backoff(1));
        await ProcessOutbox();

        var submitted = await Payment(payment.Id);
        Assert.Equal(PaymentStatus.Submitted, submitted.Status);
        Assert.Equal("PB-123", submitted.PartnerReference);
    }

    [Fact]
    public async Task A_partner_that_never_recovers_fails_the_payment_and_tells_the_merchant()
    {
        var payment = (await (await Create("key-1", Order())).Content.ReadFromJsonAsync<PaymentDto>(Json))!;
        _api.Partner.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        for (var attempt = 1; attempt <= OutboxProcessor.MaxAttempts; attempt++)
        {
            await ProcessOutbox();
            _api.Time.Advance(OutboxProcessor.Backoff(attempt));
        }

        Assert.NotNull(Assert.Single(await Outbox(OutboxType.SubmitToPartner)).DeadLetteredAt);
        Assert.Equal(PaymentStatus.Failed, (await Payment(payment.Id)).Status);
        var notice = Assert.Single(await Outbox(OutboxType.NotifyMerchant));
        Assert.Contains(MerchantEvent.Failed, notice.Payload);
    }

    [Fact]
    public async Task Partner_rejection_fails_immediately_without_retrying()
    {
        var payment = (await (await Create("key-1", Order())).Content.ReadFromJsonAsync<PaymentDto>(Json))!;
        _api.Partner.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Unsupported currency") };

        await ProcessOutbox();

        Assert.Equal(1, _api.Partner.Calls);
        var failed = await Payment(payment.Id);
        Assert.Equal(PaymentStatus.Failed, failed.Status);
        Assert.Contains("Unsupported currency", failed.FailureReason);
    }

    [Fact]
    public async Task Signed_partner_callback_settles_the_payment_once()
    {
        var payment = (await (await Create("key-1", Order())).Content.ReadFromJsonAsync<PaymentDto>(Json))!;
        var body = JsonSerializer.Serialize(new PartnerCallback("PB-1", payment.Id, PartnerCallback.Approved, null), JsonSerializerOptions.Web);

        HttpRequestMessage Callback(string signature)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/partner") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add(WebhookSignature.Header, signature);
            return request;
        }

        var forged = await _api.Client.SendAsync(Callback(WebhookSignature.Sign("wrong-secret-0123456", body, _api.Time.GetUtcNow())));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(PaymentStatus.Pending, (await Payment(payment.Id)).Status);

        var valid = WebhookSignature.Sign(ApiFixture.PartnerSecret, body, _api.Time.GetUtcNow());
        Assert.Equal(HttpStatusCode.OK, (await _api.Client.SendAsync(Callback(valid))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.Client.SendAsync(Callback(valid))).StatusCode); // redelivery

        Assert.Equal(PaymentStatus.Succeeded, (await Payment(payment.Id)).Status);
        Assert.Single(await Outbox(OutboxType.NotifyMerchant)); // the duplicate didn't notify twice
    }

    [Fact]
    public async Task Merchant_webhooks_are_signed_and_retried_until_delivered()
    {
        var payment = (await (await Create("key-1", Order())).Content.ReadFromJsonAsync<PaymentDto>(Json))!;
        _api.Partner.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new PartnerPaymentAccepted("PB-1")) };
        await ProcessOutbox();

        var body = JsonSerializer.Serialize(new PartnerCallback("PB-1", payment.Id, PartnerCallback.Declined, "Card expired"), JsonSerializerOptions.Web);
        var callback = new HttpRequestMessage(HttpMethod.Post, "/webhooks/partner") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        callback.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(ApiFixture.PartnerSecret, body, _api.Time.GetUtcNow()));
        await _api.Client.SendAsync(callback);

        string? signature = null;
        _api.Merchant.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await ProcessOutbox();
        Assert.Equal(1, Assert.Single(await Outbox(OutboxType.NotifyMerchant)).Attempts);

        _api.Merchant.Respond = req =>
        {
            signature = req.Headers.GetValues(WebhookSignature.Header).Single();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        _api.Time.Advance(OutboxProcessor.Backoff(1));
        await ProcessOutbox();

        var delivered = Assert.Single(await Outbox(OutboxType.NotifyMerchant));
        Assert.NotNull(delivered.ProcessedAt);
        Assert.Contains("Card expired", delivered.Payload);
        Assert.True(WebhookSignature.Verify("merchant-test-secret-0123", delivered.Payload, signature, _api.Time.GetUtcNow()));
    }
}
