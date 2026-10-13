using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Webhooks;

namespace Payments.Api.Data;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.Property(p => p.Status).HasConversion<string>();
            e.Property(p => p.Amount).HasConversion<double>(); // SQLite has no decimal type
            e.HasIndex(p => p.PartnerReference);
        });
        b.Entity<IdempotencyRecord>().HasKey(r => r.Key);
        b.Entity<OutboxMessage>(e =>
        {
            e.Property(m => m.Type).HasConversion<string>();
            e.HasIndex(m => new { m.ProcessedAt, m.DeadLetteredAt, m.NextAttemptAt });
        });
    }

    public void Enqueue<T>(OutboxType type, T payload, DateTime now) =>
        Outbox.Add(new OutboxMessage { Type = type, Payload = JsonSerializer.Serialize(payload), NextAttemptAt = now });

    /// <summary>Queues the merchant notification for a payment that just reached a final status.</summary>
    public void EnqueueMerchantEvent(Payment p, DateTime now) =>
        Enqueue(OutboxType.NotifyMerchant, new MerchantEvent(
            Guid.NewGuid(),
            p.Status == PaymentStatus.Succeeded ? MerchantEvent.Succeeded : MerchantEvent.Failed,
            p.Id, p.MerchantReference, p.Amount, p.Currency, p.FailureReason), now);
}
