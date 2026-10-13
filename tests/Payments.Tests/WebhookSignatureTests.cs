using Payments.Api.Integration;
using Payments.Webhooks;

namespace Payments.Tests;

public class WebhookSignatureTests
{
    private const string Secret = "test-secret-0123456789";
    private const string Body = """{"paymentId":"1","status":"approved"}""";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_signed_body_verifies() =>
        Assert.True(WebhookSignature.Verify(Secret, Body, WebhookSignature.Sign(Secret, Body, Now), Now.AddSeconds(30)));

    [Fact]
    public void Tampered_body_fails() =>
        Assert.False(WebhookSignature.Verify(Secret, Body.Replace("approved", "declined"), WebhookSignature.Sign(Secret, Body, Now), Now));

    [Fact]
    public void Wrong_secret_fails() =>
        Assert.False(WebhookSignature.Verify("another-secret-012345", Body, WebhookSignature.Sign(Secret, Body, Now), Now));

    [Theory]
    [InlineData(-6)] // replay of a captured request
    [InlineData(6)]  // timestamp from the future
    public void Signatures_outside_the_tolerance_window_fail(int minutes) =>
        Assert.False(WebhookSignature.Verify(Secret, Body, WebhookSignature.Sign(Secret, Body, Now), Now.AddMinutes(-minutes)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=notanumber,v1=abc")]
    [InlineData("t=1759320000")]
    public void Malformed_headers_fail(string? header) =>
        Assert.False(WebhookSignature.Verify(Secret, Body, header, Now));

    [Fact]
    public void Outbox_backoff_doubles_and_is_capped_at_five_minutes()
    {
        Assert.Equal([2, 4, 8, 16, 32, 64, 128, 256, 300, 300],
            Enumerable.Range(1, 10).Select(a => (int)OutboxProcessor.Backoff(a).TotalSeconds));
    }
}
