using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Data;
using Payments.Webhooks;

namespace Payments.Api.Integration;

/// <summary>
/// Delivers due outbox messages. Each failure schedules the next attempt with exponential backoff; after
/// <see cref="MaxAttempts"/> the message is dead-lettered (and a payment that never reached the partner is failed).
/// </summary>
// ponytail: single-instance processing. Running several API instances would need row claiming
// (e.g. SELECT ... FOR UPDATE SKIP LOCKED on PostgreSQL) so two workers don't send the same message.
public class OutboxProcessor(PaymentsDbContext db, PartnerClient partner, MerchantNotifier merchant, TimeProvider time, ILogger<OutboxProcessor> logger)
{
    public const int MaxAttempts = 8;

    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));

    /// <returns>Number of messages attempted.</returns>
    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var now = Now;
        var due = await db.Outbox
            .Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.NextAttemptAt <= now)
            .OrderBy(m => m.NextAttemptAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var message in due)
        {
            try
            {
                await DispatchAsync(message, ct);
                message.ProcessedAt = Now;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                message.Attempts++;
                message.LastError = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                if (message.Attempts >= MaxAttempts)
                {
                    message.DeadLetteredAt = Now;
                    logger.LogError(e, "Outbox message {Id} ({Type}) dead-lettered after {Attempts} attempts", message.Id, message.Type, message.Attempts);
                    await OnDeadLetterAsync(message, ct);
                }
                else
                {
                    message.NextAttemptAt = Now + Backoff(message.Attempts);
                    logger.LogWarning("Outbox message {Id} ({Type}) failed attempt {Attempts}: {Error}", message.Id, message.Type, message.Attempts, message.LastError);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        return due.Count;
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case OutboxType.SubmitToPartner:
                var paymentId = JsonSerializer.Deserialize<Guid>(message.Payload);
                var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, ct);
                if (payment.Status != PaymentStatus.Pending) return; // already handled (e.g. replayed message)

                switch (await partner.SubmitAsync(payment.Id, payment.Amount, payment.Currency, ct))
                {
                    case SubmitResult.Accepted a:
                        payment.Status = PaymentStatus.Submitted;
                        payment.PartnerReference = a.PartnerReference;
                        payment.UpdatedAt = Now;
                        break;
                    case SubmitResult.Rejected r:
                        Fail(payment, r.Reason);
                        break;
                }
                break;

            case OutboxType.NotifyMerchant:
                await merchant.SendAsync(message.Payload, ct);
                break;
        }
    }

    private async Task OnDeadLetterAsync(OutboxMessage message, CancellationToken ct)
    {
        if (message.Type != OutboxType.SubmitToPartner) return;
        var paymentId = JsonSerializer.Deserialize<Guid>(message.Payload);
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId, ct);
        if (payment.Status == PaymentStatus.Pending) Fail(payment, "Partner bank unavailable; payment was not submitted.");
    }

    private void Fail(Payment payment, string reason)
    {
        payment.Status = PaymentStatus.Failed;
        payment.FailureReason = reason;
        payment.UpdatedAt = Now;
        db.EnqueueMerchantEvent(payment, Now);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;
}

/// <summary>Polls the outbox every second.</summary>
public class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessDueAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Outbox polling failed");
            }
        }
    }
}
