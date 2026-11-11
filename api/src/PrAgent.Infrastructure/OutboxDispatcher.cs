using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrAgent.Application;
using PrAgent.Application.Data;
using PrAgent.Domain;

namespace PrAgent.Infrastructure;

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>False stops the background loop; tests then drive <see cref="OutboxDispatcher.DispatchPendingAsync"/> themselves.</summary>
    public bool Enabled { get; set; } = true;
    public double PollSeconds { get; set; } = 2;

    /// <summary>Attempts before a message is parked as Failed. A failed PR creation can then be retried by hand.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Wait before the 2nd, 3rd, ... attempt. The last value repeats. Empty means 5, 30, 120.</summary>
    public List<double> RetryDelaysSeconds { get; set; } = [];

    public static readonly IReadOnlyList<double> DefaultRetryDelaysSeconds = [5, 30, 120];
}

/// <summary>
/// Runs the work queued in outbox_message: SAP PR creation and email. Messages are processed one at
/// a time by a single instance. Before running more than one API instance, change the pick-up query
/// to SELECT ... FOR UPDATE SKIP LOCKED.
/// </summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IOptions<OutboxOptions> options, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Outbox dispatcher is disabled (Outbox:Enabled is false).");
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await DispatchPendingAsync(stop) == 0)
                    await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Outbox dispatcher loop failed; retrying in 5 seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Processes every message that is due. Returns how many were attempted.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        List<Guid> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<BusinessClock>().Now;
            due = await db.Outbox.AsNoTracking()
                .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now)
                .OrderBy(m => m.CreatedAt)
                .Select(m => m.Id)
                .Take(20)
                .ToListAsync(ct);
        }

        foreach (var id in due)
            await DispatchOneAsync(id, ct);

        return due.Count;
    }

    private async Task DispatchOneAsync(Guid id, CancellationToken ct)
    {
        string? error = null;

        // The handler gets its own scope and DbContext, so a failure inside it cannot leave
        // half-tracked changes in the context that records the outcome below.
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var message = await db.Outbox.AsNoTracking().FirstAsync(m => m.Id == id, ct);
            try
            {
                switch (message.Type)
                {
                    case OutboxMessage.CreatePr:
                        var requestId = message.RequestId ?? throw new InvalidOperationException("CreatePr message has no request id.");
                        await scope.ServiceProvider.GetRequiredService<PrCreationWorker>().ProcessAsync(requestId, ct);
                        break;
                    case OutboxMessage.Email:
                        var email = JsonSerializer.Deserialize<EmailMessage>(message.Payload, Json.Options)
                            ?? throw new InvalidOperationException("Email message has no payload.");
                        await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(email, ct);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e.Message;
                logger.LogWarning(e, "Outbox message {Id} ({Type}) failed", id, message.Type);
            }
        }

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<BusinessClock>().Now;
            var message = await db.Outbox.FirstAsync(m => m.Id == id, CancellationToken.None);
            message.Attempts++;
            message.LastError = error;

            if (error is null)
            {
                message.Status = OutboxStatus.Processed;
                message.ProcessedAt = now;
            }
            else if (message.Attempts >= options.Value.MaxAttempts)
            {
                message.Status = OutboxStatus.Failed;
                message.ProcessedAt = now;
            }
            else
            {
                IReadOnlyList<double> delays = options.Value.RetryDelaysSeconds.Count > 0
                    ? options.Value.RetryDelaysSeconds
                    : OutboxOptions.DefaultRetryDelaysSeconds;
                var delay = delays[Math.Min(message.Attempts - 1, delays.Count - 1)];
                message.NextAttemptAt = now.AddSeconds(delay);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
