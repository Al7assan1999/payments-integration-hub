using System.ComponentModel.DataAnnotations;

namespace Payments.Api.Integration;

public class PartnerOptions
{
    public const string Section = "Partner";

    [Required, Url] public string BaseUrl { get; set; } = "";
    /// <summary>Shared secret the partner signs its callbacks with.</summary>
    [Required, MinLength(16)] public string WebhookSecret { get; set; } = "";
    /// <summary>Public URL of our /webhooks/partner endpoint, sent with each payment.</summary>
    [Required, Url] public string CallbackUrl { get; set; } = "";
}

public class MerchantOptions
{
    public const string Section = "Merchant";

    [Required, Url] public string WebhookUrl { get; set; } = "";
    /// <summary>Secret we sign merchant webhooks with; the merchant verifies with the same secret.</summary>
    [Required, MinLength(16)] public string WebhookSecret { get; set; } = "";
}
