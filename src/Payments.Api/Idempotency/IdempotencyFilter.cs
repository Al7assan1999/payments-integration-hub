using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Data;

namespace Payments.Api.Idempotency;

/// <summary>
/// Makes a POST endpoint safe to retry. The client sends an <c>Idempotency-Key</c>; the first request with
/// that key runs and its response is stored, and any retry with the same key and body gets the stored response
/// back (marked <c>Idempotent-Replayed: true</c>) instead of creating a second payment.
/// </summary>
public class IdempotencyFilter<TRequest>(PaymentsDbContext db, TimeProvider time) : IEndpointFilter
{
    public const string Header = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[Header].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.Problem($"An {Header} header (max 100 characters) is required.", statusCode: 400);

        var request = context.Arguments.OfType<TRequest>().Single();
        var hash = Hash(request);

        var existing = await db.IdempotencyRecords.FindAsync(key);
        if (existing is not null) return Replay(existing, hash, http);

        // Claim the key first; the primary key makes concurrent duplicates fail here rather than both running.
        var record = new IdempotencyRecord { Key = key, RequestHash = hash, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Results.Problem("A request with this Idempotency-Key is already being processed.", statusCode: 409);
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            // Failed before producing a response: release the key so the client can retry.
            db.IdempotencyRecords.Remove(record);
            await db.SaveChangesAsync();
            throw;
        }

        if (result is IStatusCodeHttpResult { StatusCode: { } status } and IValueHttpResult valueResult)
        {
            record.ResponseStatus = status;
            record.ResponseBody = JsonSerializer.Serialize(valueResult.Value, JsonSerializerOptions.Web);
            await db.SaveChangesAsync();
        }
        return result;
    }

    private static IResult Replay(IdempotencyRecord existing, string hash, HttpContext http)
    {
        if (existing.RequestHash != hash)
            return Results.Problem("This Idempotency-Key was already used with a different request body.", statusCode: 422);
        if (existing.ResponseStatus is null)
            return Results.Problem("A request with this Idempotency-Key is already being processed.", statusCode: 409);

        http.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Content(existing.ResponseBody, "application/json", statusCode: existing.ResponseStatus);
    }

    private static string Hash(TRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
}
