using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Payments.Webhooks;

/// <summary>
/// HMAC-SHA256 webhook signatures, in the style used by Stripe and others:
/// <c>X-Signature: t=1760000000,v1=hex(hmac(secret, "{t}.{body}"))</c>.
/// Signing the timestamp with the body lets receivers reject replays of old, captured requests.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-Signature";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string body, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds();
        return $"t={t},v1={Compute(secret, t, body)}";
    }

    public static bool Verify(string secret, string body, string? header, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (!TryParse(header, out var t, out var signature)) return false;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(t);
        if (age.Duration() > (tolerance ?? DefaultTolerance)) return false;

        var expected = Encoding.ASCII.GetBytes(Compute(secret, t, body));
        var actual = Encoding.ASCII.GetBytes(signature);
        // Constant-time comparison so response timing can't be used to guess the signature byte by byte.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexStringLower(mac);
    }

    private static bool TryParse(string? header, out long timestamp, out string signature)
    {
        timestamp = 0;
        signature = "";
        if (string.IsNullOrEmpty(header)) return false;

        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t") long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out timestamp);
            else if (kv[0] == "v1") signature = kv[1];
        }
        return timestamp > 0 && signature.Length > 0;
    }
}
