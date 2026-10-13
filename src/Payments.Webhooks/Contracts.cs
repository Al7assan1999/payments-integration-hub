namespace Payments.Webhooks;

// Wire contracts between the three parties. Shared so the mocks and the API can't drift apart.

/// <summary>Payments API → partner bank.</summary>
public record PartnerPaymentRequest(Guid PaymentId, decimal Amount, string Currency, string CallbackUrl);

public record PartnerPaymentAccepted(string PartnerReference);

/// <summary>Partner bank → Payments API (signed webhook) once the payment is settled or declined.</summary>
public record PartnerCallback(string PartnerReference, Guid PaymentId, string Status, string? Reason)
{
    public const string Approved = "approved";
    public const string Declined = "declined";
}

/// <summary>Payments API → merchant (signed webhook). EventId lets the merchant ignore duplicates.</summary>
public record MerchantEvent(Guid EventId, string Type, Guid PaymentId, string MerchantReference, decimal Amount, string Currency, string? Reason)
{
    public const string Succeeded = "payment.succeeded";
    public const string Failed = "payment.failed";
}
