using System.Text;
using Microsoft.Extensions.Options;
using Payments.Webhooks;

namespace Payments.Api.Integration;

/// <summary>Delivers a signed webhook to the merchant. Non-2xx throws so the outbox retries with backoff.</summary>
public class MerchantNotifier(HttpClient http, IOptions<MerchantOptions> options, TimeProvider time)
{
    public async Task SendAsync(string eventJson, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.WebhookUrl)
        {
            Content = new StringContent(eventJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(options.Value.WebhookSecret, eventJson, time.GetUtcNow()));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
