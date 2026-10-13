namespace Payments.Api.Data;

public enum PaymentStatus { Pending, Submitted, Succeeded, Failed }

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string MerchantReference { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public string? PartnerReference { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsFinal => Status is PaymentStatus.Succeeded or PaymentStatus.Failed;
}

/// <summary>
/// The stored outcome of a POST made with an Idempotency-Key. A retry with the same key gets the same
/// response instead of creating a second payment. ResponseStatus is null while the first request is in flight.
/// </summary>
public class IdempotencyRecord
{
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum OutboxType { SubmitToPartner, NotifyMerchant }

/// <summary>
/// Work that must happen after a database change commits (call the partner, notify the merchant).
/// Written in the same transaction as the change, then delivered by <c>OutboxWorker</c> with retries.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public OutboxType Type { get; set; }
    public required string Payload { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? DeadLetteredAt { get; set; }
    public string? LastError { get; set; }
}
