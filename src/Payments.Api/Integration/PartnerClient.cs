using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Payments.Webhooks;

namespace Payments.Api.Integration;

public abstract record SubmitResult
{
    public sealed record Accepted(string PartnerReference) : SubmitResult;
    /// <summary>The partner refused the request itself (4xx): retrying won't help.</summary>
    public sealed record Rejected(string Reason) : SubmitResult;
}

/// <summary>
/// Calls the partner bank. Transient failures (5xx, 408, 429, timeouts, connection errors) are retried with
/// exponential backoff and a circuit breaker by the standard resilience handler registered in Program.cs;
/// anything that still fails surfaces as an exception and the outbox retries the whole call later.
/// </summary>
public class PartnerClient(HttpClient http, IOptions<PartnerOptions> options)
{
    public async Task<SubmitResult> SubmitAsync(Guid paymentId, decimal amount, string currency, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = JsonContent.Create(new PartnerPaymentRequest(paymentId, amount, currency, options.Value.CallbackUrl)),
        };
        // The resilience handler may resend this POST; the payment id as idempotency key stops the
        // partner from charging twice, and the outbox can safely repeat the whole call later too.
        request.Headers.Add("Idempotency-Key", paymentId.ToString());
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
            and not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests)
        {
            return new SubmitResult.Rejected($"Partner rejected the payment ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        }

        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<PartnerPaymentAccepted>(ct)
            ?? throw new InvalidOperationException("Partner returned an empty body.");
        return new SubmitResult.Accepted(accepted.PartnerReference);
    }
}
